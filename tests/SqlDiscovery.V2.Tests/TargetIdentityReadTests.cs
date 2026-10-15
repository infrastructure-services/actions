using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SqlDiscovery.V2;

// The SQL command/reader are doubles; the production identity reader, orchestrator
// and public envelope run unchanged. No connection or SQL server is constructed.
internal static class TargetIdentityReadTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("identity command preserves exact SQL-observed identity", ExactIdentity);
        yield return ("identity command keeps absent and invalid identity unavailable", MissingIdentity);
        foreach (var atRead in new[] { false, true })
        {
            foreach (var failure in new[] { "TIMEOUT", "CANCELLED", "AUTHENTICATION_FAILED", "TRANSPORT_FAILED" })
            {
                var stage = atRead ? "ReadAsync" : "ExecuteReaderAsync";
                yield return ($"identity {stage} preserves {failure} through public envelope",
                    () => Failure(atRead, failure));
            }
        }
    }

    private static async Task ExactIdentity()
    {
        using var reader = Reader("SQLNODE01\\INSTANCE", "ObservedDb");
        using var command = new IdentityCommand(reader);
        using var cancellation = new CancellationTokenSource();
        var transport = new RecordingTransport {
            ConnectTarget = token => SqlClientDiscoveryTransportV2.ReadTargetIdentityAsync(command, token)
        };
        var orchestrator = new SqlDiscoveryOrchestratorV2(transport);
        var result = await orchestrator.DiscoverAsync(Target(), cancellation.Token);
        Equal(ConnectionStatus.Succeeded, result.TargetConnection.Status);
        Equal(ObservedIdentityStatus.Available, result.ObservedIdentity.Status);
        Equal("SQLNODE01\\INSTANCE", result.ObservedIdentity.Identity?.ServerInstance);
        Equal("ObservedDb", result.ObservedIdentity.Identity?.DatabaseName);
        Equal(1, command.ExecuteCalls);
        Equal(cancellation.Token, command.SeenToken);
        Equal(cancellation.Token, reader.SeenToken);
        Check(reader.Disposed, "identity reader must be disposed");
        using var json = JsonDocument.Parse(SqlDiscoveryPublicCli.SerializeSuccessEnvelope(
            result, orchestrator.ProjectSources(result), new SqlTlsPolicy("TEST").Evidence));
        Equal("ObservedDb", json.RootElement.GetProperty("observedDatabaseIdentity").GetProperty("databaseName").GetString());
    }

    private static async Task MissingIdentity()
    {
        foreach (var item in new (string? Server, string? Database, bool HasRow)[] {
            ("SQLNODE01", "ObservedDb", false), (null, "ObservedDb", true),
            ("SQLNODE01", null, true), ("", "ObservedDb", true),
            ("SQLNODE01", "", true), ("invalid/server", "ObservedDb", true)
        })
        {
            using var reader = Reader(item.Server, item.Database, item.HasRow);
            using var command = new IdentityCommand(reader);
            var result = await SqlClientDiscoveryTransportV2.ReadTargetIdentityAsync(command, CancellationToken.None);
            Equal(ObservedIdentityStatus.Unavailable, result.Status);
            Check(result.Identity is null, "unavailable identity cannot publish target names");
            Equal("OBSERVED_DATABASE_IDENTITY_UNAVAILABLE", result.Diagnostic?.Code);
            Check(reader.Disposed, "unavailable identity reader must be disposed");
        }
    }

    private static async Task Failure(bool atRead, string failure)
    {
        const string privateMessage = "Server=private-endpoint;Password=must-never-escape;private-path";
        using var cancellation = new CancellationTokenSource();
        Exception Fault(CancellationToken token)
        {
            Equal(cancellation.Token, token);
            if (failure == "CANCELLED") { cancellation.Cancel(); return new OperationCanceledException(privateMessage, token); }
            return failure switch {
                "TIMEOUT" => new TimeoutException(privateMessage),
                "AUTHENTICATION_FAILED" => new SqlDiscoveryAuthenticationException(),
                _ => new IOException(privateMessage)
            };
        }
        using var reader = Reader("SQLNODE01", "ObservedDb");
        using var command = new IdentityCommand(reader);
        if (atRead) reader.Fault = Fault; else command.Fault = Fault;
        var transport = new RecordingTransport {
            ConnectTarget = token => SqlClientDiscoveryTransportV2.ReadTargetIdentityAsync(command, token)
        };
        var orchestrator = new SqlDiscoveryOrchestratorV2(transport);
        var result = await orchestrator.DiscoverAsync(Target(), cancellation.Token);
        var expected = failure switch {
            "TIMEOUT" => ConnectionStatus.TimedOut, "CANCELLED" => ConnectionStatus.Cancelled,
            "AUTHENTICATION_FAILED" => ConnectionStatus.AuthenticationFailed, _ => ConnectionStatus.TransportFailed
        };
        Equal(ConnectionStatus.Succeeded, result.ServerConnection.Status);
        Equal(DatabaseLookupStatus.Found, result.DatabaseLookup.Status);
        Equal(expected, result.TargetConnection.Status);
        Equal(failure, result.TargetConnection.Diagnostic?.Code);
        Equal("TARGET_CONNECTION", result.TargetConnection.Diagnostic?.Stage);
        Equal(ObservedIdentityStatus.NotAttempted, result.ObservedIdentity.Status);
        Equal(MetadataStatus.NotAttempted, result.Metadata.Status);
        Equal(PhysicalStatus.NotAttempted, result.Physical.Status);
        Equal(HistoryStatus.NotAttempted, result.History.Status);
        Check(transport.Calls.SequenceEqual(new[] { "server", "lookup", "target" }), "dependent SQL stages must not run");
        Equal(1, command.ExecuteCalls);
        Equal(atRead ? 1 : 0, reader.ReadCalls);
        if (atRead) Check(reader.Disposed, "failed identity reader must be disposed");
        var projection = orchestrator.ProjectSources(result);
        Check(projection.IsRepresentable, "failure must retain its exact public state");
        var serialized = SqlDiscoveryPublicCli.SerializeSuccessEnvelope(result, projection, new SqlTlsPolicy("TEST").Evidence);
        Check(!serialized.Contains("must-never-escape") && !serialized.Contains("private-endpoint")
            && !serialized.Contains("private-path"), "exception content escaped into evidence");
        using var json = JsonDocument.Parse(serialized);
        Equal(failure, json.RootElement.GetProperty("targetConnectionSource").GetProperty("status").GetString());
        foreach (var section in new[] { "metadataSource", "physicalSource", "historySource" })
            Equal("NOT_ATTEMPTED", json.RootElement.GetProperty(section).GetProperty("status").GetString());
        Check(!json.RootElement.TryGetProperty("observedDatabaseIdentity", out _), "failed target must not publish identity");
    }

    private static SqlDiscoveryTarget Target() => new("unused-test-connection", "RequestedDb");
    private static IdentityReader Reader(string? server, string? database, bool hasRow = true)
    {
        var table = new DataTable();
        table.Columns.Add("server", typeof(string)); table.Columns.Add("database", typeof(string));
        if (hasRow) table.Rows.Add((object?)server ?? DBNull.Value, (object?)database ?? DBNull.Value);
        return new(table.CreateDataReader());
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class IdentityCommand(IdentityReader reader) : DbCommand
    {
        internal Func<CancellationToken, Exception>? Fault { get; set; }
        internal int ExecuteCalls { get; private set; }
        internal CancellationToken SeenToken { get; private set; }
        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            ExecuteCalls++; SeenToken = cancellationToken;
            return Fault is null ? Task.FromResult<DbDataReader>(reader) : Task.FromException<DbDataReader>(Fault(cancellationToken));
        }
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() => throw new NotSupportedException();
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class IdentityReader(DataTableReader inner) : DbDataReader
    {
        internal Func<CancellationToken, Exception>? Fault { get; set; }
        internal int ReadCalls { get; private set; }
        internal CancellationToken SeenToken { get; private set; }
        internal bool Disposed { get; private set; }
        public override Task<bool> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCalls++; SeenToken = cancellationToken;
            return Fault is null ? inner.ReadAsync(cancellationToken) : Task.FromException<bool>(Fault(cancellationToken));
        }
        public override ValueTask DisposeAsync() { Disposed = true; return inner.DisposeAsync(); }
        protected override void Dispose(bool disposing) { if (disposing) { Disposed = true; inner.Dispose(); } base.Dispose(disposing); }
        public override void Close() => inner.Close();
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => inner.GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override bool Read() => inner.Read();
    }
}
