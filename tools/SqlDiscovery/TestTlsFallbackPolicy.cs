using System.ComponentModel;
using System.Security.Authentication;
using Microsoft.Data.SqlClient;

namespace SqlDiscovery.V2;

public enum SqlTlsMode { Strict, TestUntrustedCertificate }

public sealed class TestTlsFallbackPolicy
{
    private const int SqlServerStrictCertificateError = 17821;
    private static readonly HashSet<int> CertificateTrustHResults =
    [
        unchecked((int)0x800B0101), // CERT_E_EXPIRED
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x800B010A), // CERT_E_CHAINING
        unchecked((int)0x80092012)  // CRYPT_E_NO_REVOCATION_CHECK
    ];

    private bool fallbackAttempted;
    private string initialResult = "NOT_ATTEMPTED";
    private bool strictSucceeded;

    public TestTlsFallbackPolicy(string environmentName, bool allowTestUntrustedCertificateFallback)
    {
        if (string.IsNullOrWhiteSpace(environmentName)) throw new ArgumentException("Environment is required.", nameof(environmentName));
        if (allowTestUntrustedCertificateFallback && !string.Equals(environmentName, "TEST", StringComparison.Ordinal))
            throw new InvalidOperationException("TEST_TLS_FALLBACK_FORBIDDEN_OUTSIDE_TEST");
        FallbackAllowed = allowTestUntrustedCertificateFallback;
    }

    public bool FallbackAllowed { get; }

    public TlsDiscoveryEvidence Evidence => new(
        "STRICT",
        initialResult,
        FallbackAllowed,
        fallbackAttempted,
        fallbackAttempted ? "TEST_UNTRUSTED_CERTIFICATE" : "STRICT",
        strictSucceeded && !fallbackAttempted,
        true);

    public async Task<T> ExecuteAsync<T>(
        Func<SqlTlsMode, CancellationToken, Task<T>> attempt,
        CancellationToken cancellationToken,
        Func<Exception, bool>? certificateFailureClassifier = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        certificateFailureClassifier ??= IsCertificateTrustFailure;
        if (fallbackAttempted)
            return await attempt(SqlTlsMode.TestUntrustedCertificate, cancellationToken);
        try
        {
            var result = await attempt(SqlTlsMode.Strict, cancellationToken);
            strictSucceeded = true;
            initialResult = "SUCCEEDED";
            return result;
        }
        catch (Exception exception) when (FallbackAllowed && certificateFailureClassifier(exception))
        {
            initialResult = "CERTIFICATE_VALIDATION_FAILED";
            fallbackAttempted = true;
            return await attempt(SqlTlsMode.TestUntrustedCertificate, cancellationToken);
        }
        catch
        {
            initialResult = "OTHER_FAILURE";
            throw;
        }
    }

    public static bool IsCertificateTrustFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException
                && sqlException.Errors.Cast<SqlError>().Any(error => error.Number == SqlServerStrictCertificateError))
                return true;
            if (current.HResult == unchecked((int)0x800B010F)) return false; // CERT_E_CN_NO_MATCH
            if (CertificateTrustHResults.Contains(current.HResult)) return true;
            if (current is Win32Exception win32 && CertificateTrustHResults.Contains(win32.NativeErrorCode)) return true;
            if (current is AuthenticationException && HasCertificateTrustMessage(current.Message)) return true;
            if (current is SqlException && HasCertificateTrustMessage(current.Message)) return true;
        }
        return false;
    }

    public static bool HasCertificateTrustMessage(string message)
    {
        if (message.Contains("hostname", StringComparison.OrdinalIgnoreCase)
            || message.Contains("name mismatch", StringComparison.OrdinalIgnoreCase)
            || message.Contains("does not match", StringComparison.OrdinalIgnoreCase)
            || message.Contains("target principal name is incorrect", StringComparison.OrdinalIgnoreCase))
            return false;

        return message.Contains("certificate chain was issued by an authority that is not trusted", StringComparison.OrdinalIgnoreCase)
            || message.Contains("self-signed certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unable to get local issuer certificate", StringComparison.OrdinalIgnoreCase)
            || message.Contains("certificate verify failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown ca", StringComparison.OrdinalIgnoreCase)
            || message.Contains("valid TLS certificate is not configured to accept strict", StringComparison.OrdinalIgnoreCase);
    }
}
