using DatabaseReleaseQualification;

internal static class RecoveryCoverageTests
{
    public static readonly (string Name, Func<Task> Run)[] Cases = [
        ("coverage: original DROP VIEW witness rejects lost grant", LostGrant),
        ("coverage: exact permission restoration matches", ExactRestoration),
        ("coverage: permission-only forward and rollback", PermissionOnly),
        ("coverage: permission-only rollback mismatch", PermissionMismatch),
        ("coverage: POST1 POST2 security mismatch", ReapplyMismatch),
        ("coverage: missing provider stops before forward", MissingProvider),
        ("coverage: partial PRE stops before forward", PartialPre),
        ("coverage: missing visibility is not complete", MissingVisibility),
        ("coverage: source timeout is fail closed", SourceTimeout),
        ("coverage: source cancellation is fail closed", SourceCancelled),
        ("coverage: identity mismatch stops rehearsal", IdentityMismatch),
        ("coverage: scope hash mismatch is rejected", ScopeMismatch),
        ("coverage: missing securable is not observed absence", MissingSecurable),
        ("coverage: principal identity must resolve", MissingPrincipal),
        ("coverage: explicit owner mismatch blocks", OwnerMismatch),
        ("coverage: schema owner mismatch blocks", SchemaOwnerMismatch),
        ("coverage: principal SID mismatch blocks", SidMismatch),
        ("coverage: grantor mismatch blocks", GrantorMismatch),
        ("coverage: column exception and grant option are significant", ColumnAndGrantOption),
        ("coverage: ordering and SID hex case are canonical", CanonicalOrdering),
        ("coverage: unrelated securable is rejected", ExtraSecurable),
        ("coverage: unrelated principal is rejected", ExtraPrincipal),
        ("coverage: duplicate permission is rejected", DuplicatePermission),
        ("coverage: no security impact requires no provider", NotRequired),
        ("coverage: structure PARTIAL blocks", PartialStructure),
        ("coverage: data evidence missing preserves existing block", MissingData),
        ("coverage: original data validation remains valid", ValidData),
        ("coverage: DATA reapply evidence is separate", MissingDataReapply),
        ("coverage: DATA reapply mismatch blocks", DataReapplyMismatch),
        ("coverage: nominal FullReversible lacks coverage", NominalClass),
        ("coverage: database schema and object permission scopes", PermissionScopes),
        ("coverage: server and ambiguous security stay unsupported", UnsupportedSecurity),
        ("coverage: EXISTS EF and NEW EF retain qualification", EfRegression),
        ("coverage: read-only provider validates source envelope", ProviderValidation),
        ("coverage: post evidence partial cannot progress", PartialPost),
        ("coverage: PRE2 missing evidence blocks reapply", PartialRecovery),
        ("coverage: attestation retains bound coverage evidence", PackageEvidence),
        ("coverage: stale phase and version are rejected", InvalidEnvelope),
        ("coverage: corrupted canonical evidence cannot be complete", TamperedCoverage),
        ("coverage: ERROR state never becomes complete", ErrorCoverage),
        ("coverage: snapshot absence is explicit and comparable", ObjectAbsence),
        ("coverage: unknown or ambiguous impact cannot be qualified", UnknownImpact)
    ];

    private static readonly RecoverySecurityPrincipal Dbo = new("dbo", "S", "01", "INSTANCE", "dbo", null, false);
    private static readonly RecoverySecurityPrincipal Reader = new("AppReader", "R", "02", "NONE", null, "dbo", false);
    private static readonly RecoverySecurityPermission Grant = new("SELECT", "G", "AppReader", "dbo");
    private static readonly RecoverySecuritySecurable View = new("OBJECT", "dbo", "V");
    private static SchemaSnapshot Snapshot(bool view = true) => new() { Objects = view ? [new() { Kind = "view", Schema = "dbo", Name = "V", Properties = new(StringComparer.Ordinal) { ["definitionSha256"] = Hashing.Sha256("view") } }] : [] };
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new InvalidOperationException(message); }
    private static void Eq<T>(T expected, T actual) => Assert(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}");

    internal static RecoverySecuritySnapshot Security(RecoverySecurityScope scope, RecoveryPhase phase, SchemaSnapshot snapshot)
    {
        var principals = scope.RequiredPrincipals.Append("dbo").Distinct(StringComparer.Ordinal).Select(x => x == "dbo" ? Dbo : Reader with { Name = x }).ToArray();
        return new(1, phase, scope.Sha256, "FAKE_SQL", "FakeLab", RecoveryEvidenceCoverage.Complete, true,
            scope.Securables.Select(s => new RecoverySecurityObjectState(s,
                s.Kind != "OBJECT" || snapshot.Objects.Any(x => x.Schema == s.Schema && (x.Name == s.Name || x.Parent == s.Name)),
                null, s.Kind == "DATABASE" ? null : "dbo", [])).ToArray(),
            scope.Securables.Any(x => x.Kind != "DATABASE") ? principals : principals.Where(x => scope.RequiredPrincipals.Contains(x.Name) || x.Name == "dbo" && scope.RequiredPrincipals.Count > 0).ToArray());
    }

    private static RecoverySecuritySnapshot WithGrant(RecoverySecuritySnapshot x, bool present = true) => x with {
        Securables = x.Securables.Select(s => s with { Permissions = present && s.Exists ? [Grant] : [] }).ToArray(),
        Principals = present && x.Securables.Any(s => s.Exists) || x.Principals.Any(p => p.Name == "AppReader") ? [Dbo, Reader] : [Dbo]
    };

    private sealed class Lab : IRehearsalDatabase, IRecoverySecurityEvidenceProvider
    {
        public readonly List<string> Executions = [];
        public Func<RecoverySecuritySnapshot, RecoverySecuritySnapshot> Transform = x => x;
        public bool PermissionChange;
        public bool StructuralChange = true;
        private int captures;
        private SchemaSnapshot current = Snapshot();
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken cancellationToken = default)
        { current = Snapshot(!StructuralChange || captures % 2 == 0); captures++; return Task.FromResult(current); }
        public Task ExecuteSqlAsync(ReleaseScript script, string hash, CancellationToken cancellationToken = default)
        { Eq(script.Sha256, hash); Executions.Add(script.Role); return Task.CompletedTask; }
        public Task<RecoverySecuritySnapshot> CaptureSecurityAsync(RecoverySecurityScope scope, RecoveryPhase phase, CancellationToken cancellationToken = default)
        {
            var evidence = Security(scope, phase, current);
            if (PermissionChange) evidence = WithGrant(evidence, phase is RecoveryPhase.Post1 or RecoveryPhase.Post2);
            else evidence = WithGrant(evidence);
            return Task.FromResult(Transform(evidence));
        }
    }
    private sealed class NoSecurity(Lab lab) : IRehearsalDatabase
    {
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken t = default) => lab.CaptureSchemaAsync(t);
        public Task ExecuteSqlAsync(ReleaseScript s, string h, CancellationToken t = default) => lab.ExecuteSqlAsync(s, h, t);
    }
    private static Task<RehearsalResult> Run(IRehearsalDatabase db, string forward = "DROP VIEW dbo.V;",
        string rollback = "CREATE VIEW dbo.V AS SELECT 1 AS Value;", IDataRollbackValidationContract? data = null, string scenario = "EXISTING_LEGACY")
        => new RehearsalEngine().QualifyAsync(new() { ReleaseId = "coverage-test", Environment = "TEST", SourceKind = scenario == "EXISTING_LEGACY" ? "SQL" : "EF", Scenario = scenario, DatabaseLifecycle = scenario == "NEW_EF" ? "NEW" : "EXISTING" },
            new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "SYNTHETIC" }, ReleaseScript.FromText("forward", forward), ReleaseScript.FromText("rollback", rollback), db, data);
    private static async Task<RehearsalResult> Block(Func<RecoverySecuritySnapshot, RecoverySecuritySnapshot> transform, string reason)
    {
        var lab = new Lab { Transform = transform };
        var result = await Run(lab); Eq(reason, result.QualificationStatus); Assert(!result.CanProceed); return result;
    }
    private static async Task LostGrant()
    {
        var lab = new Lab { Transform = x => x.Phase == RecoveryPhase.Pre2 ? WithGrant(x, false) with { Principals = [Dbo] } : x };
        var r = await Run(lab); Eq("BLOCKED_SECURITY_MISMATCH", r.QualificationStatus);
        Eq(RecoveryComparison.Match, r.RecoveryCoverage!.Structure.Recovery);
        Eq(RecoveryComparison.Mismatch, r.RecoveryCoverage.Security.Recovery); Assert(!r.CanProceed && !r.RollbackCertified); Eq(2, lab.Executions.Count);
    }
    private static async Task ExactRestoration()
    { var r = await Run(new Lab(), rollback: "CREATE VIEW dbo.V AS SELECT 1 AS Value;\nGO\nGRANT SELECT ON OBJECT::dbo.V TO AppReader;"); Assert(r.CanProceed, r.QualificationStatus); Eq(RecoveryComparison.Match, r.RecoveryCoverage!.Security.Recovery); Eq(RecoveryComparison.Match, r.RecoveryCoverage.Security.Reapply); Eq(4, r.RecoveryCoverage.SecurityHashes.Count); }
    private static async Task PermissionOnly()
    { var r = await Run(new Lab { PermissionChange = true, StructuralChange = false }, "GRANT SELECT ON OBJECT::dbo.V TO AppReader;", "REVOKE SELECT ON OBJECT::dbo.V FROM AppReader;"); Assert(r.CanProceed, r.QualificationStatus); }
    private static async Task PermissionMismatch()
    { var lab = new Lab { PermissionChange = true, StructuralChange = false, Transform = x => x.Phase == RecoveryPhase.Pre2 ? WithGrant(x) : x }; var r = await Run(lab, "GRANT SELECT ON OBJECT::dbo.V TO AppReader;", "REVOKE SELECT ON OBJECT::dbo.V FROM AppReader;"); Eq("BLOCKED_SECURITY_MISMATCH", r.QualificationStatus); Assert(!r.CanProceed); }
    private static async Task ReapplyMismatch() => await Block(x => x.Phase == RecoveryPhase.Post2 ? x with { Securables = x.Securables.Select(s => s with { Exists = true }).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    private static async Task MissingProvider() { var lab = new Lab(); var r = await Run(new NoSecurity(lab)); Eq("BLOCKED_SECURITY_EVIDENCE_MISSING", r.QualificationStatus); Eq(0, lab.Executions.Count); }
    private static async Task PartialPre() => await Block(x => x with { Coverage = RecoveryEvidenceCoverage.Partial }, "BLOCKED_SECURITY_COVERAGE");
    private static async Task MissingVisibility() => await Block(x => x with { MetadataVisibilityComplete = false }, "BLOCKED_SECURITY_COVERAGE");
    private static async Task SourceTimeout() => await Block(_ => throw new TimeoutException(), "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task SourceCancelled() => await Block(_ => throw new OperationCanceledException(), "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task IdentityMismatch() => await Block(x => x.Phase == RecoveryPhase.Post1 ? x with { DatabaseName = "Other" } : x, "BLOCKED_SECURITY_IDENTITY_MISMATCH");
    private static async Task ScopeMismatch() => await Block(x => x with { ScopeHash = new string('0', 64) }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task MissingSecurable() => await Block(x => x with { Securables = [] }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task MissingPrincipal() => await Block(x => x with { Principals = [Dbo] }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task OwnerMismatch() => await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Securables = x.Securables.Select(s => s with { ExplicitOwner = "AppReader" }).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    private static async Task SchemaOwnerMismatch() => await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Securables = x.Securables.Select(s => s with { SchemaOwner = "AppReader" }).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    private static async Task SidMismatch() => await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Principals = x.Principals.Select(p => p.Name == "AppReader" ? p with { SidHex = "ff" } : p).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    private static async Task GrantorMismatch() => await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Securables = x.Securables.Select(s => s with { Permissions = [Grant with { Grantor = "AppReader" }] }).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    private static async Task ColumnAndGrantOption()
    {
        foreach (var permission in new[] { Grant with { State = "W" }, Grant with { State = "R", Column = "Value" } })
            await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Securables = x.Securables.Select(s => s with { Permissions = [permission] }).ToArray() } : x, "BLOCKED_SECURITY_MISMATCH");
    }
    private static Task CanonicalOrdering()
    {
        var scope = new RecoverySecurityScope([View], []);
        var a = WithGrant(Security(scope, RecoveryPhase.Pre, Snapshot()));
        a = a with { Principals = a.Principals.Select(p => p with { SidHex = "Ab" }).ToArray(), Securables = a.Securables.Select(s => s with { Permissions = [Grant, Grant with { Permission = "UPDATE" }] }).ToArray() };
        var b = a with { Phase = RecoveryPhase.Pre2, Principals = a.Principals.Reverse().Select(p => p with { SidHex = "aB" }).ToArray(), Securables = a.Securables.Select(s => s with { Permissions = s.Permissions.Reverse().ToArray() }).ToArray() };
        Eq(RecoverySecurityCanonicalizer.Canonicalize(scope, RecoveryPhase.Pre, a)!.Sha256, RecoverySecurityCanonicalizer.Canonicalize(scope, RecoveryPhase.Pre2, b)!.Sha256); return Task.CompletedTask;
    }
    private static async Task ExtraSecurable() => await Block(x => x with { Securables = x.Securables.Append(new(new("OBJECT", "dbo", "Other"), true, null, "dbo", [])).ToArray() }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task ExtraPrincipal() => await Block(x => x with { Principals = x.Principals.Append(Reader with { Name = "Unrelated" }).ToArray() }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task DuplicatePermission() => await Block(x => x with { Securables = x.Securables.Select(s => s with { Permissions = [Grant, Grant] }).ToArray() }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    private static async Task NotRequired()
    { var r = await Run(new NoSecurity(new Lab { StructuralChange = false }), "SELECT 1;", "SELECT 1;"); Assert(r.CanProceed); Eq(RecoveryEvidenceCoverage.NotRequired, r.RecoveryCoverage!.Security.Coverage); }
    private sealed class PartialLab : IRehearsalDatabase
    {
        public Task<SchemaSnapshot> CaptureSchemaAsync(CancellationToken t = default) => Task.FromResult(new SchemaSnapshot { UnsupportedSchemaFeatures = ["unmodeled"] });
        public Task ExecuteSqlAsync(ReleaseScript s, string h, CancellationToken t = default) => throw new Exception("Must not execute");
    }
    private static async Task PartialStructure() { var r = await Run(new PartialLab(), "SELECT 1;", "SELECT 1;"); Assert(!r.CanProceed); }
    private static async Task MissingData()
    { var r = await Run(new NoSecurity(new Lab { StructuralChange = false }), "UPDATE dbo.T SET C = 1;", "UPDATE dbo.T SET C = 0;"); Eq("BLOCKED_DATA_ROLLBACK_UNVERIFIED", r.QualificationStatus); Assert(!r.CanProceed); }
    private sealed class DataOnly : IDataRollbackValidationContract
    {
        public Task CapturePreDataAsync(CancellationToken t = default) => Task.CompletedTask;
        public Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken t = default) => Task.FromResult(DataRollbackValidity.Valid);
    }
    private sealed class DataBoth(bool matches = true) : IDataRollbackValidationContract, IDataReapplyValidationContract
    {
        public Task CapturePreDataAsync(CancellationToken t = default) => Task.CompletedTask;
        public Task<DataRollbackValidity> ValidateRollbackDataAsync(CancellationToken t = default) => Task.FromResult(DataRollbackValidity.Valid);
        public Task CapturePostDataAsync(CancellationToken t = default) => Task.CompletedTask;
        public Task<DataRollbackValidity> ValidateReapplyDataAsync(CancellationToken t = default) => Task.FromResult(matches ? DataRollbackValidity.Valid : DataRollbackValidity.Invalid);
    }
    private static async Task ValidData() { var r = await Run(new NoSecurity(new Lab { StructuralChange = false }), "UPDATE dbo.T SET C = 1;", "UPDATE dbo.T SET C = 0;", new DataBoth()); Assert(r.CanProceed); }
    private static async Task MissingDataReapply() { var r = await Run(new NoSecurity(new Lab { StructuralChange = false }), "UPDATE dbo.T SET C = 1;", "UPDATE dbo.T SET C = 0;", new DataOnly()); Eq("BLOCKED_DATA_REAPPLY_EVIDENCE_MISSING", r.QualificationStatus); Assert(!r.CanProceed); Eq(DataRollbackValidity.Valid, r.DataRollbackValidity); }
    private static async Task DataReapplyMismatch() { var r = await Run(new NoSecurity(new Lab { StructuralChange = false }), "UPDATE dbo.T SET C = 1;", "UPDATE dbo.T SET C = 0;", new DataBoth(false)); Eq("BLOCKED_DATA_REAPPLY_UNVERIFIED", r.QualificationStatus); Assert(!r.CanProceed); }
    private static Task NominalClass()
    {
        var r = new RehearsalResult { QualificationStatus = "QUALIFIED", SchemaRollbackValidity = SchemaRollbackValidity.Valid, DataRollbackValidity = DataRollbackValidity.NotApplicable, RollbackCapability = RollbackCapability.FullReversible, RollbackCertified = true, ReapplyCertified = true };
        Assert(!r.CanProceed); return Task.CompletedTask;
    }
    private static Task PermissionScopes()
    {
        foreach (var sql in new[] { "GRANT SELECT ON OBJECT::dbo.V TO AppReader;", "DENY SELECT ON SCHEMA::dbo TO AppReader;", "REVOKE CONNECT FROM AppReader;", "GRANT SELECT ON OBJECT::dbo.V TO AppReader WITH GRANT OPTION;" })
        { var a = new SqlScriptAnalyzer().Analyze("forward", sql, Snapshot()); Eq(AnalysisConfidence.Complete, a.Confidence); Assert(RecoveryImpact.Derive(a, a).SecurityRequired, sql); }
        return Task.CompletedTask;
    }
    private static Task UnsupportedSecurity()
    {
        foreach (var sql in new[] { "GRANT CONTROL SERVER TO AppReader;", "GRANT SELECT ON OBJECT::Other.dbo.V TO AppReader;", "CREATE LOGIN Evil WITH PASSWORD='synthetic';", "REVOKE SELECT ON OBJECT::dbo.V FROM AppReader CASCADE;", "EXEC('GRANT SELECT ON dbo.V TO AppReader');" })
        { var a = new SqlScriptAnalyzer().Analyze("forward", sql, Snapshot()); Assert(!RecoveryImpact.Derive(a, a).Complete, sql); }
        return Task.CompletedTask;
    }
    private static async Task EfRegression() { foreach (var scenario in new[] { "NEW_EF", "EXISTING_EF" }) { var r = await Run(new Lab(), scenario: scenario); Assert(r.CanProceed, scenario + r.QualificationStatus); } }
    private sealed class ReaderFake : IRecoverySecurityCatalogReader
    { public Task<RecoverySecuritySnapshot> ReadAsync(RecoverySecurityScope s, RecoveryPhase p, CancellationToken t = default) => Task.FromResult(Security(s, p, Snapshot())); }
    private static async Task ProviderValidation()
    { var scope = new RecoverySecurityScope([View], []); var p = new ReadOnlyRecoverySecurityProvider(new ReaderFake()); var r = await p.CaptureSecurityAsync(scope, RecoveryPhase.Pre); Assert(RecoverySecurityCanonicalizer.Canonicalize(scope, RecoveryPhase.Pre, r) is not null); }
    private static async Task PartialPost() => await Block(x => x.Phase == RecoveryPhase.Post1 ? x with { Coverage = RecoveryEvidenceCoverage.Partial } : x, "BLOCKED_SECURITY_COVERAGE");
    private static async Task PartialRecovery() => await Block(x => x.Phase == RecoveryPhase.Pre2 ? x with { Coverage = RecoveryEvidenceCoverage.Insufficient } : x, "BLOCKED_SECURITY_COVERAGE");
    private static async Task PackageEvidence()
    {
        var r = await Run(new Lab());
        Eq(Hashing.Sha256(System.Text.Encoding.UTF8.GetBytes("DROP VIEW dbo.V;")), r.RecoveryCoverage!.ForwardHash);
        var forward = ReleaseScript.FromText("forward", "DROP VIEW dbo.V;");
        var rollback = ReleaseScript.FromText("rollback", "CREATE VIEW dbo.V AS SELECT 1 AS Value;");
        var release = new ReleaseDescriptor { ReleaseId = "coverage-test", Environment = "TEST", SourceKind = "SQL", Scenario = "EXISTING_LEGACY", DatabaseLifecycle = "EXISTING" };
        var root = Path.Combine(Path.GetTempPath(), "coverage-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = new ReleasePackageWriter().Write(root, "proof", release, forward, rollback, Snapshot(),
                r.AnalysisEvidence!.EffectiveDependencyAnalysis, r.AnalysisEvidence.EffectiveRisk, r);
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(package.AttestationDirectory, "qualification-attestation.json")));
            Assert(json.RootElement.GetProperty("recoveryCoverage").GetProperty("complete").GetBoolean());
            Eq(r.RecoveryCoverage.ForwardHash, json.RootElement.GetProperty("forwardHash").GetString());
            var rejected = false;
            try { new ReleasePackageWriter().Write(root, "changed", release, ReleaseScript.FromText("forward", "DROP VIEW dbo.V; -- changed"), rollback, Snapshot(),
                r.AnalysisEvidence.EffectiveDependencyAnalysis, r.AnalysisEvidence.EffectiveRisk, r); }
            catch (InvalidOperationException e) { rejected = e.Message == "RECOVERY_COVERAGE_PAYLOAD_MISMATCH"; }
            Assert(rejected);
        }
        finally
        {
            // Only remove this test's freshly allocated directory under the temp root.
            Assert(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private static async Task InvalidEnvelope()
    {
        await Block(x => x with { ContractVersion = 2 }, "BLOCKED_SECURITY_COVERAGE_ERROR");
        await Block(x => x with { Phase = RecoveryPhase.Post2 }, "BLOCKED_SECURITY_COVERAGE_ERROR");
        await Block(x => x with { Coverage = (RecoveryEvidenceCoverage)42 }, "BLOCKED_SECURITY_COVERAGE_ERROR");
    }
    private static async Task TamperedCoverage()
    {
        var r = await Run(new Lab()); var evidence = r.RecoveryCoverage!;
        var corrupted = evidence.SecurityEvidence.ToDictionary();
        corrupted[RecoveryPhase.Pre] = corrupted[RecoveryPhase.Pre] with { Json = "{}" };
        Assert(!(evidence with { SecurityEvidence = corrupted }).Complete);
        Assert(!(evidence with { Security = evidence.Security with { Coverage = RecoveryEvidenceCoverage.Partial } }).Complete);
        Assert(!(evidence with { Data = new(true, RecoveryEvidenceCoverage.Complete, RecoveryComparison.Match) }).Complete);
    }
    private static async Task ErrorCoverage() => await Block(x => x with { Coverage = RecoveryEvidenceCoverage.Error }, "BLOCKED_SECURITY_COVERAGE");
    private static async Task ObjectAbsence()
    {
        var r = await Run(new Lab()); Assert(r.CanProceed);
        Assert(r.RecoveryCoverage!.SecurityHashes[RecoveryPhase.Pre] != r.RecoveryCoverage.SecurityHashes[RecoveryPhase.Post1]);
    }
    private static async Task UnknownImpact()
    {
        var lab = new Lab(); var r = await Run(lab, "DROP VIEW V;", "SELECT 1;");
        Eq("BLOCKED_RECOVERY_IMPACT_INCOMPLETE", r.QualificationStatus); Eq(0, lab.Executions.Count);
    }
}
