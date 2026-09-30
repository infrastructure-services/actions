using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DatabaseReleaseQualification;

// Constructed only after fresh Macro 1 qualification and governed authorization
// resolution. Merely constructing this adapter does not open a SQL connection.
internal sealed class SqlLegacyRehearsalRuntimeV1(
    LegacyQualificationOutcomeV1 qualification, string inspectionConnection,
    Func<string> mutationConnection, Func<CancellationToken, Task> revalidate,
    LegacyRuntimeContextV1 context, ILegacyRehearsalAuthorityV1 authority,
    string? singlePhase = null) : ILegacyRehearsalRuntimeV1
{
    private int executions;
    private int activeOrFailed;
    private readonly LegacyHandoffV1 handoff = qualification.Readiness!.Handoff!;
    public string EvidenceKind => "REAL_TEST";
    public LegacyRuntimeContextV1 Context => context;
    private SecurityTargetBindingV1 Binding => new(handoff.Governance.Binding.DatabaseName,
        handoff.Governance.Binding.ServerMatchPolicy, handoff.Governance.Binding.AllowedServerInstances);

    public async Task RevalidateAsync(LegacyHandoffV1 expected, CancellationToken token)
    {
        if (LegacyRuntimeEvidenceHash.Hash(expected) != LegacyRuntimeEvidenceHash.Hash(handoff)
            || expected.Governance.Environment != "TEST")
            throw new LegacyContractException("REQUALIFICATION_REQUIRED");
        LegacyRehearsalHarnessV1.VerifyQualification(qualification);
        await revalidate(token);
    }

    public async Task<ObservedCurrentSnapshotV1> CaptureAsync(CancellationToken token)
    {
        var connection = SqlClientSecurityCatalogTransport.BuildConnectionOptions(inspectionConnection, Binding).ConnectionString;
        var reader = new SqlServerSchemaReader();
        var first = await reader.CaptureWithMetadataAsync(connection, token);
        var second = await reader.CaptureWithMetadataAsync(connection, token);
        return LegacyStructuralEvidence.FromCurrentCaptures(handoff.TargetId,
            handoff.Governance.Binding.EndpointReference, first, second,
            qualification.ObservedSnapshot!.HashContract, DateTimeOffset.UtcNow);
    }

    public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope,
        RecoveryPhase phase, CancellationToken cancellationToken = default) =>
        new ReadOnlyRecoverySecurityProvider(new SqlRecoverySecurityCatalogReader(
            new SqlClientSecurityCatalogTransport(() => inspectionConnection, Binding), Binding))
            .CaptureSecurityAsync(scope, phase, cancellationToken);

    public async Task VerifyRollbackAgainstPost1Async(ReleaseScript rollback,
        ObservedCurrentSnapshotV1 post1, CancellationToken token)
    {
        if (rollback.Role != "rollback" || rollback.Sha256 != handoff.Artifacts.Rollback.Sha256)
            throw new LegacyContractException("REQUALIFICATION_REQUIRED");
        await revalidate(token);
        await LegacyStaticSafety.VerifyPhaseAsync(rollback, post1.Snapshot,
            new SqlLegacyScopeSafetySource(
                new SqlClientSecurityCatalogTransport(() => inspectionConnection, Binding), Binding),
            handoff.Governance.Binding, token);
        var analysis = new SqlScriptAnalyzer().Analyze("rollback", rollback.Text, post1.Snapshot);
        var impact = RecoveryImpact.Derive(qualification.StaticSafety!.ForwardAnalysis, analysis);
        if (!impact.Complete || impact.DataRequired != qualification.StaticSafety.Impact.DataRequired
            || impact.SecurityScope.Sha256 != handoff.Recovery.SecurityScopeHash)
            throw new LegacyContractException("BLOCKED_RECOVERY_IMPACT_CHANGED");
    }

    public async Task ApplyExactAsync(ReleaseScript script, string expectedHash,
        string transactionPolicy, ObservedCurrentSnapshotV1 expectedPre, CancellationToken token)
    {
        if (Interlocked.Exchange(ref activeOrFailed,1) != 0)
            throw new LegacyContractException("REHEARSAL_RESUME_NOT_SUPPORTED");
        try { await ApplyCoreAsync(script,expectedHash,transactionPolicy,expectedPre,token); }
        catch (SqlException exception) when (exception.Number == -2)
        { throw new LegacyContractException("REHEARSAL_SQL_TIMEOUT"); }
        // Remains latched on every failure, including uncertain commit outcome.
        Volatile.Write(ref activeOrFailed,0);
    }

    private async Task ApplyCoreAsync(ReleaseScript script, string expectedHash,
        string transactionPolicy, ObservedCurrentSnapshotV1 expectedPre, CancellationToken token)
    {
        var phase = singlePhase ?? (executions switch { 0 => "FORWARD1", 1 => "ROLLBACK", 2 => "FORWARD2",
            _ => throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID") });
        if (singlePhase is not null && (executions != 0
                || singlePhase is not ("FORWARD1" or "ROLLBACK" or "FORWARD2")))
            throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
        if (script.Role != (phase == "ROLLBACK" ? "rollback" : "forward"))
            throw new LegacyContractException("REHEARSAL_PHASE_ORDER_INVALID");
        // Consume the attempt before opening the connection: failure cannot be retried.
        executions++;
        await authority.VerifyAsync(handoff, phase, token);
        VerifyExactScript(handoff,script,expectedHash,transactionPolicy);
        var current = await CaptureAsync(token);
        if (current.TargetId != expectedPre.TargetId || current.ObservedIdentity != expectedPre.ObservedIdentity
            || current.ObservedSchemaHash != expectedPre.ObservedSchemaHash
            || current.Metadata != expectedPre.Metadata
            || !current.CaptureComparison.IdentityMatch || !current.CaptureComparison.StructuralMatch
            || LegacyRuntimeEvidenceHash.Hash(current.Snapshot.ImpactMetrics)
                != LegacyRuntimeEvidenceHash.Hash(expectedPre.Snapshot.ImpactMetrics))
            throw new LegacyContractException("DRIFT_BEFORE_MUTATION");
        await LegacyStaticSafety.VerifyPhaseAsync(script, current.Snapshot,
            new SqlLegacyScopeSafetySource(new SqlClientSecurityCatalogTransport(() => inspectionConnection, Binding), Binding),
            handoff.Governance.Binding, token);
        var batches = ExactBatches(script);
        var options = SqlClientSecurityCatalogTransport.BuildConnectionOptions(mutationConnection(), Binding);
        options.ApplicationName = "legacy-rehearsal-v1";
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(token);
        // Check the identity on the actual mutating connection, independently of
        // the read-only connection. No values or SQL output reach logs.
        await using (var identity = connection.CreateCommand())
        {
            identity.CommandText = "SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME()";
            identity.CommandTimeout = 15;
            await using var row = await identity.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.IsDBNull(0) || row.IsDBNull(1)
                || row.GetString(0) != current.ObservedIdentity.ServerInstance
                || row.GetString(1) != current.ObservedIdentity.DatabaseName)
                throw new LegacyContractException("MUTATION_CONNECTION_TARGET_MISMATCH");
        }
        // Supported V1 statements are constrained by Static Safety. Script-owned
        // transactions and nontransactional statement families are rejected.
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        foreach (var batch in batches)
        {
            token.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 60;
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(token);
        }
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token);
        // Disposal on error releases the uncommitted phase transaction. It never
        // executes the package rollback. Commit uncertainty requires human recovery.
    }

    internal static void VerifyExactScript(LegacyHandoffV1 h, ReleaseScript script, string expectedHash, string policy)
    {
        if (h.Governance.Environment != "TEST" || script.Sha256 != expectedHash
            || expectedHash != (script.Role == "rollback" ? h.Artifacts.Rollback.Sha256 : h.Artifacts.Forward.Sha256)
            || script.Role is not ("forward" or "rollback")
            || policy != "HARNESS_OWNS_PHASE_TRANSACTIONS_V1")
            throw new LegacyContractException("REQUALIFICATION_REQUIRED");
    }

    public static IReadOnlyList<string> ExactBatches(ReleaseScript script)
    {
        var parsed = SqlScriptAnalyzer.Parse(script.Text);
        if (parsed.Errors.Count != 0 || parsed.Fragment is not TSqlScript ast)
            throw new LegacyContractException("SQL_UNSUPPORTED");
        // Preserve original text spans; never regenerate SQL from the AST.
        var result = ast.Batches.Where(x => x.Statements.Count > 0)
            .Select(x => script.Text.Substring(x.StartOffset, x.FragmentLength)).ToArray();
        if (result.Length == 0) throw new LegacyContractException("SQL_NO_STATEMENTS");
        foreach (var token in ast.ScriptTokenStream.Where(x => x.TokenType.ToString() == "Go"))
            if (!string.Equals(script.Text.Split('\n')[token.Line - 1].Trim(), "GO", StringComparison.OrdinalIgnoreCase))
                throw new LegacyContractException("SQL_GO_UNSUPPORTED");
        return result;
    }
}
