using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace SqlDiscovery.V2;

public enum SqlTlsMode { Strict, TestUntrustedCertificate }
public enum TlsFailureCategory
{
    KnownCertificateTrust,
    HostnameOrIdentityMismatch,
    Authentication,
    Timeout,
    Cancelled,
    TransportOther,
    Unknown
}

public sealed record SanitizedExceptionFingerprint(
    [property: JsonPropertyName("exceptionType")] string ExceptionType,
    [property: JsonPropertyName("hResult")] string HResult,
    [property: JsonPropertyName("sqlExceptionNumber")] int? SqlExceptionNumber,
    [property: JsonPropertyName("sqlErrorNumbers")] IReadOnlyList<int> SqlErrorNumbers,
    [property: JsonPropertyName("sqlErrorStates")] IReadOnlyList<byte> SqlErrorStates,
    [property: JsonPropertyName("sqlErrorClasses")] IReadOnlyList<byte> SqlErrorClasses,
    [property: JsonPropertyName("innerExceptionType")] string? InnerExceptionType,
    [property: JsonPropertyName("innerHResult")] string? InnerHResult,
    [property: JsonPropertyName("nativeErrorCode")] int? NativeErrorCode,
    [property: JsonPropertyName("tlsFailureCategory")] string TlsFailureCategory)
{
    public static SanitizedExceptionFingerprint Capture(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var sql = exception as SqlException;
        var errors = sql?.Errors.Cast<SqlError>().ToArray() ?? [];
        var inner = exception.InnerException;
        var native = ExceptionChain(exception).OfType<Win32Exception>().FirstOrDefault()?.NativeErrorCode;
        return new(
            SafeType(exception), Hex(exception.HResult), sql?.Number,
            new ReadOnlyCollection<int>(errors.Select(error => error.Number).ToArray()),
            new ReadOnlyCollection<byte>(errors.Select(error => error.State).ToArray()),
            new ReadOnlyCollection<byte>(errors.Select(error => error.Class).ToArray()),
            inner is null ? null : SafeType(inner), inner is null ? null : Hex(inner.HResult), native,
            CategoryName(TestTlsFallbackPolicy.ClassifyFailure(exception)));
    }

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException) yield return current;
    }

    private static string SafeType(Exception exception) => exception.GetType().FullName ?? exception.GetType().Name;
    private static string Hex(int value) => $"0x{unchecked((uint)value):X8}";
    private static string CategoryName(global::SqlDiscovery.V2.TlsFailureCategory category) => category switch
    {
        global::SqlDiscovery.V2.TlsFailureCategory.KnownCertificateTrust => "KNOWN_CERTIFICATE_TRUST",
        global::SqlDiscovery.V2.TlsFailureCategory.HostnameOrIdentityMismatch => "HOSTNAME_OR_IDENTITY_MISMATCH",
        global::SqlDiscovery.V2.TlsFailureCategory.Authentication => "AUTHENTICATION",
        global::SqlDiscovery.V2.TlsFailureCategory.Timeout => "TIMEOUT",
        global::SqlDiscovery.V2.TlsFailureCategory.Cancelled => "CANCELLED",
        global::SqlDiscovery.V2.TlsFailureCategory.TransportOther => "TRANSPORT_OTHER",
        _ => "UNKNOWN"
    };
}

public sealed record TlsDiscoveryEvidence(
    string TlsRequestedMode,
    string TlsInitialMode,
    string TlsInitialResult,
    bool TlsFallbackAllowed,
    bool TlsFallbackAttempted,
    string TlsEffectiveMode,
    bool TlsCertificateValidated,
    bool TransportEncrypted,
    string TlsPolicySource,
    SanitizedExceptionFingerprint? DiagnosticFingerprint = null);

public sealed class SqlTlsPolicy
{
    private string result = "NOT_ATTEMPTED";
    private SanitizedExceptionFingerprint? diagnosticFingerprint;

    public SqlTlsPolicy(string environmentName, SqlTlsMode requestedMode = SqlTlsMode.Strict)
    {
        if (string.IsNullOrWhiteSpace(environmentName))
            throw new ArgumentException("Environment is required.", nameof(environmentName));
        if (requestedMode == SqlTlsMode.TestUntrustedCertificate
            && !string.Equals(environmentName, "TEST", StringComparison.Ordinal))
            throw new InvalidOperationException("TEST_UNTRUSTED_CERTIFICATE_FORBIDDEN_OUTSIDE_TEST");
        RequestedMode = requestedMode;
    }

    public SqlTlsMode RequestedMode { get; }
    public string RequestedModeName => Name(RequestedMode);
    public string PolicySource => RequestedMode == SqlTlsMode.Strict ? "DEFAULT_STRICT" : "EXPLICIT_TEST_CONFIGURATION";
    public TlsDiscoveryEvidence Evidence => new(
        RequestedModeName,
        RequestedModeName,
        result,
        false,
        false,
        RequestedModeName,
        RequestedMode == SqlTlsMode.Strict && result == "SUCCEEDED",
        true,
        PolicySource,
        diagnosticFingerprint);

    public static SqlTlsMode Parse(string? value)
        => (value ?? "STRICT").Trim().ToUpperInvariant() switch
        {
            "STRICT" => SqlTlsMode.Strict,
            "TEST_UNTRUSTED_CERTIFICATE" => SqlTlsMode.TestUntrustedCertificate,
            _ => throw new InvalidOperationException("TLS_MODE_INVALID")
        };

    public void Apply(SqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Encrypt = RequestedMode == SqlTlsMode.Strict
            ? SqlConnectionEncryptOption.Strict
            : SqlConnectionEncryptOption.Mandatory;
        builder.TrustServerCertificate = RequestedMode == SqlTlsMode.TestUntrustedCertificate;
    }

    public async Task<T> ExecuteAsync<T>(Func<SqlTlsMode, CancellationToken, Task<T>> open, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        try
        {
            var value = await open(RequestedMode, cancellationToken);
            result = "SUCCEEDED";
            return value;
        }
        catch (Exception exception)
        {
            result = "OTHER_FAILURE";
            diagnosticFingerprint = SanitizedExceptionFingerprint.Capture(exception);
            throw;
        }
    }

    private static string Name(SqlTlsMode mode) => mode switch
    {
        SqlTlsMode.Strict => "STRICT",
        SqlTlsMode.TestUntrustedCertificate => "TEST_UNTRUSTED_CERTIFICATE",
        _ => throw new InvalidOperationException("TLS_MODE_INVALID")
    };
}

public static class TestTlsFallbackPolicy
{
    private const int SqlServerStrictCertificateError = 17821;
    private static readonly HashSet<int> CertificateTrustHResults =
    [
        unchecked((int)0x800B0101), // CERT_E_EXPIRED
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x800B010A), // CERT_E_CHAINING
        unchecked((int)0x80092012)  // CRYPT_E_NO_REVOCATION_CHECK
    ];

    public static bool IsCertificateTrustFailure(Exception exception)
        => ClassifyFailure(exception) == TlsFailureCategory.KnownCertificateTrust;

    public static TlsFailureCategory ClassifyFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.HResult == unchecked((int)0x800B010F) || HasHostnameOrIdentityMessage(current.Message))
                return TlsFailureCategory.HostnameOrIdentityMismatch;
            if (current is SqlException sqlException
                && sqlException.Errors.Cast<SqlError>().Any(error => error.Number == SqlServerStrictCertificateError))
                return TlsFailureCategory.KnownCertificateTrust;
            if (CertificateTrustHResults.Contains(current.HResult)) return TlsFailureCategory.KnownCertificateTrust;
            if (current is Win32Exception win32 && CertificateTrustHResults.Contains(win32.NativeErrorCode))
                return TlsFailureCategory.KnownCertificateTrust;
            if ((current is AuthenticationException or SqlException) && HasCertificateTrustMessage(current.Message))
                return TlsFailureCategory.KnownCertificateTrust;
        }
        if (exception is OperationCanceledException) return TlsFailureCategory.Cancelled;
        if (exception is TimeoutException || exception is SqlException { Number: -2 }) return TlsFailureCategory.Timeout;
        if (exception is SqlException { Number: 18456 }
            || string.Equals(exception.GetType().Name, "SqlDiscoveryAuthenticationException", StringComparison.Ordinal))
            return TlsFailureCategory.Authentication;
        if (ExceptionChain(exception).Any(item => item is SqlException or AuthenticationException or IOException or SocketException or Win32Exception))
            return TlsFailureCategory.TransportOther;
        return TlsFailureCategory.Unknown;
    }

    public static bool HasCertificateTrustMessage(string message)
    {
        if (HasHostnameOrIdentityMessage(message))
            return false;

        return message.Contains("certificate chain was issued by an authority that is not trusted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("self-signed certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unable to get local issuer certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown ca", StringComparison.OrdinalIgnoreCase)
            || message.Contains("valid TLS certificate is not configured to accept strict", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHostnameOrIdentityMessage(string message) =>
        message.Contains("hostname", StringComparison.OrdinalIgnoreCase)
        || message.Contains("name mismatch", StringComparison.OrdinalIgnoreCase)
        || message.Contains("does not match", StringComparison.OrdinalIgnoreCase)
        || message.Contains("target principal name is incorrect", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Exception> ExceptionChain(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException) yield return current;
    }
}
