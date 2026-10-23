using System.Text;
using System.Text.Json;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

internal static class EfAdoptionTests
{
    private static readonly DateTimeOffset Now = EfPreparationTests.Now;
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Compact);
    private static T Read<T>(EfPreparationTests.Fixture f, EvidenceReference r)
        => JsonSerializer.Deserialize<T>(f.Provider.Content[r.EvidenceId], JsonDefaults.Compact)!;
    private static void Assert(bool b, string why) { if (!b) throw new Exception(why); }
    private static void Status(EfAdoptionResultV1 r, string expected)
        => Assert(r.Status == expected && !r.ExecutionAuthorized && !r.Certified,
            expected + ": " + r.Status + "/" + string.Join(",", r.Reasons));
    public static (string Name, Func<Task> Run)[] Cases => [
        ("EF adoption 01: populated history without pending needs adoption", () => Eligibility(0)),
        ("EF adoption 02: pending migrations do not require a previous baseline", () => Eligibility(2)),
        ("EF adoption 03: persisted governed state avoids duplicate adoption", AlreadyManaged),
        ("EF adoption 04: registered BASELINE_REQUIRED requires independent review", Review),
        ("EF adoption 05: identifiable unregistered target requires registration", Registration),
        ("EF adoption 06: approved candidate is not persisted or managed", Approval),
        ("EF adoption 07: persisted initial baseline requires governed transition", Persistence),
        ("EF adoption 08: lost CAS acknowledgement reconciles idempotently", Uncertainty),
        ("EF adoption 09: concurrent requests cannot create two current baselines", Concurrency),
        ("EF adoption 10: historical and current revision retain verified provenance", Historical),
        ("EF adoption 11: unknown duplicate reordered removed divergent migrations block", History),
        ("EF adoption 12: unresolved drift incomplete coverage and missing review block", Coverage),
        ("EF adoption 13: absent or malformed history on populated database blocks", HistoryStructure),
        ("EF adoption 14: identity tampering replay expiry and technical failures block", Adversarial),
        ("EF adoption 15: adoption then existing V4 release then V5 without SQL", Continuity),
        ("EF adoption host: real CLI emits sanitized read-only complementary states", Host),
        ("EF adoption repository: actual Discovery V2 file hashes consumed by A2.1", ActualRepository),
        ("EF adoption governance: forged receipts pointers baseline and transitions block", PersistenceNegatives),
        ("EF adoption authority: registered execution host is never invoked", NoSqlAuthority)
    ];

    // Every provider and persistence instance in this suite is explicitly a test
    // double. Schema/data/security are acquired through the real normalizer.
    internal static async Task<EfPreparationTests.Fixture> Make(int pending = 1, string mutation = "")
    {
        var f = await EfPreparationTests.Make(pending: Math.Max(1, pending));
        var o = f.Pre.Observation;
        // The original release fixture deliberately has only History in PRE.
        // Add a populated business table to both sides and requalify in memory.
        foreach (var schema in new[] { f.Original.Request.Pre.Schema, f.Original.Request.ExpectedPost.Schema })
        {
            schema.Objects.Add(new() { Kind = "table", Schema = "dbo", Name = "Customer" });
            schema.ImpactMetrics.Add(new() { Schema = "dbo", Table = "Customer", RowCount = 7 });
        }
        var original = f.Original;
        var lab = new EfReleasePlanTests.Lab(original.Request.Pre.Schema, original.Request.ExpectedPost.Schema);
        var exercise = await new RehearsalEngine().QualifyAsync(new() { ReleaseId = original.Request.ReleaseId,
            Environment = "TEST", SourceKind = "EF", Scenario = "EXISTING_EF", DatabaseLifecycle = "EXISTING" },
            new() { ConsistencyStatus = "CONSISTENT", ConsistencyReason = "OFFLINE_ADOPTION" }, original.Forward, original.Rollback, lab, lab);
        original = original with { Request = original.Request with { Recovery = original.Request.Recovery with {
            Exercise = exercise, ExerciseEvidence = new() { EvidenceId = "adoption-exercise", EvidenceSha256 = Hashing.Sha256(Bytes(exercise)) } } } };
        f = await EfPreparationTests.Make(original: original);
        o = f.Pre.Observation;
        if (mutation == "unknown") o = o with { AppliedMigrationIds = ["20200101120000_Unknown"] };
        if (mutation == "duplicate") o = o with { AppliedMigrationIds = [o.AppliedMigrationIds[0], o.AppliedMigrationIds[0]] };
        if (mutation == "reordered") o = o with { AppliedMigrationIds = o.AppliedMigrationIds.Reverse().ToArray() };
        if (mutation == "absent") o = o with { HistoryStatus = "ABSENT", AppliedMigrationIds = [] };
        if (mutation == "empty") o = o with { HistoryStatus = "EMPTY", AppliedMigrationIds = [] };
        if (mutation == "structure") o.Schema.Objects.RemoveAll(x => x.Kind == "column" && x.Name == "ProductVersion");
        if (mutation == "pk") o.Schema.Objects.RemoveAll(x => x.Kind == "key-constraint-column");
        if (mutation == "index") o.Schema.Objects.RemoveAll(x => x.Kind == "index-column");
        if (mutation == "server") o = o with { ServerInstance = "different-server" };
        if (mutation == "database") o = o with { DatabaseName = "different-database" };
        if (mutation == "coverage") o.Schema.ImpactMetrics.Clear();
        var pre = EfPreparationTests.Observe(f.Provider, o, "TARGET_PRE", f.Governance.ContentHashContract);
        var context = new EfObservationContextV1(1, f.Selection.Scope, "TARGET_PRE", o.Environment,
            o.EndpointReference, o.DatabaseIncarnation, o.CapturedAtUtc, f.Governance.ContentHashContract);
        var d = pre.Discovery;
        if (mutation == "transport") d = d with { ServerConnection = new(ConnectionStatus.TransportFailed) };
        if (mutation == "missing-db") d = d with { DatabaseLookup = new(DatabaseLookupStatus.NotFoundConfirmed) };
        if (mutation == "metadata") d = d with { Metadata = new(MetadataStatus.Insufficient) };
        var discovery = f.Provider.Publish("adoption-discovery", "SQL_DISCOVERY_V2", new EfDiscoveryDocumentV1(context, d));
        var schemaRef = f.Provider.Publish("adoption-schema", "SCHEMA_CAPTURE", new EfSchemaDocumentV1(context, o.Schema));
        var contract = Read<EfContentHashContractV1>(f, f.Governance.ContentHashContract);
        var data = f.Provider.Publish("adoption-data", "DATA_PROJECTION_RESULT", new EfContentProjectionV1(context,
            mutation == "partial-data" ? "PARTIAL" : "COMPLETE", contract.DataProjection, o.DataHash));
        var security = f.Provider.Publish("adoption-security", "SECURITY_PROJECTION_RESULT", new EfContentProjectionV1(
            mutation == "replay" ? context with { Scope = context.Scope with { SessionId = "replay" } } : context,
            "COMPLETE", contract.SecurityProjection, o.SecurityHash));
        pre = pre with { Discovery = d, Observation = o with { Evidence = schemaRef, Physical = d.Physical } };
        var preRef = f.Provider.Publish("adoption-normalized", "TARGET_PRE", pre);
        var current = original.Request.Source;
        var ids = pending == 0 ? original.Request.Pre.AppliedMigrationIds : current.ManagedMigrationIds;
        if (mutation == "removed") ids = ids.Skip(1).ToArray();
        EvidenceReference RepositoryProof(string prefix, string revision, string[] migrations, string purpose)
        {
            var files = new Dictionary<string, EvidenceReference>(); var artifacts = new List<object>(); var valid = new List<object>();
            foreach (var id in migrations)
            {
                var body = "src/Infrastructure/Migrations/" + id + ".cs";
                var designer = "src/Infrastructure/Migrations/" + id + ".Designer.cs";
                foreach (var path in new[] { body, designer })
                {
                    var content = path == body ? "// exact offline migration " + id : "[Migration(\"" + id + "\")]";
                    if (mutation == "divergent" && prefix == "historical" && path == body) content += "changed";
                    var r = f.Provider.Raw(prefix + "-" + path, purpose + "_FILE", Encoding.UTF8.GetBytes(content));
                    files[path] = r; artifacts.Add(new { path, sha256 = r.EvidenceSha256 });
                }
                valid.Add(new { id, projectPath = current.ProjectPath, bodyPath = body, designerPath = designer });
            }
            return f.Provider.Publish(prefix + "-repository", purpose, new EfAdoptionRepositoryV1(1, f.Selection.Scope,
                current.Repository, revision, JsonSerializer.SerializeToElement(new { status = "PRESENT_VALID",
                    adoption = new { status = "VALID", projectPath = current.ProjectPath, sourceRevision = revision },
                    migrations = new { count = migrations.Length, ids = migrations }, rawEvidence = new {
                        rawEvidenceContractVersion = 1, anomalies = Array.Empty<string>(), validMigrations = valid,
                        migrationArtifacts = artifacts } }), files));
        }
        var repository = RepositoryProof("current", current.Revision, ids, "REPOSITORY_DISCOVERY_V2");
        var historicalIds = original.Request.Pre.AppliedMigrationIds;
        var historicalRevision = new string('c', 40);
        var historical = RepositoryProof("historical", historicalRevision, historicalIds, "EF_ADOPTION_HISTORICAL_REPOSITORY");
        var historicalSource = new EfPlanProvenanceV1(current.Repository, historicalRevision,
            f.Provider.Publish("historical-source", "EF_ADOPTION_HISTORICAL_SOURCE", new { repository = current.Repository, historicalRevision }));
        current = current with { ManagedMigrationIds = ids, RepositoryEvidence = repository, AdoptionProvenance = historicalSource };
        var source = f.Provider.Publish("adoption-source", "SOURCE", new EfSourcePreparationV1(current, [], []));
        var lineage = f.Provider.Publish("adoption-lineage", "EF_ADOPTION_LINEAGE", new EfAdoptionLineageV1(1, f.Selection.Scope,
            source, preRef, historicalSource, historicalIds, historical));
        var g = f.Governance with { ClassificationRulesVersion = 2,
            Authority = f.Governance.Authority with { OnboardingState = "REQUIRED", CertifiedBaseline = null },
            AdoptionPolicy = new(1, mutation == "registration" ? "REGISTRATION_REQUIRED" : "REGISTERED", "BASELINE_REQUIRED", new() {
                RegistryRepository = "platform/governance", RegistryRef = new string('a', 40), RegistryCommitSha = new string('a', 40),
                RegistryFilePath = "registry/test.json", RegistryFileSha256 = Hashing.Sha256("offline-registry") }) };
        f = f.Replace("GOVERNANCE", g) with { Governance = g, Pre = pre, Original = original,
            Selection = f.Selection with { Governance = f.Provider.Publish("adoption-governance", "GOVERNANCE", g),
                Source = source, Pre = preRef, Qualification = null, PreSources = new(discovery, schemaRef, data, security), Adoption = new(lineage) } };
        if (mutation == "hash") f.Provider.Content[security.EvidenceId] = [1, 2, 3];
        if (mutation == "role") f.Provider.Allowed.Remove((security.EvidenceId, "SECURITY_PROJECTION_RESULT"));
        return f;
    }
    internal static EfPreparationTests.Fixture WithReview(EfPreparationTests.Fixture f, string mutation = "")
    {
        var o = f.Pre.Observation;
        var support = f.Provider.Publish("review-support", "EF_ADOPTION_STATE_REVIEW_BYTES", new { review = "independent offline state review" });
        var review = f.Provider.Publish("adoption-review", "EF_ADOPTION_STATE_REVIEW", new EfAdoptionReviewV1(1, f.Selection.Scope,
            f.Selection.Source, f.Selection.Pre, f.Selection.Adoption!.Lineage, SchemaCanonicalizer.Canonicalize(o.Schema).Sha256,
            o.DataHash, o.SecurityHash, mutation == "manual" ? "REVIEW_REQUIRED" : "VERIFIED",
            mutation == "drift" ? ["UNEXPLAINED_SCHEMA"] : [], support));
        return f with { Selection = f.Selection with { Adoption = f.Selection.Adoption with { Review = review } } };
    }
    internal static Task<EfAdoptionResultV1> Run(EfPreparationTests.Fixture f, CancellationToken token = default)
        => f.Service.PrepareAdoptionAsync(f.Selection, Now, token);
    internal static EvidenceReference Consent(EfPreparationTests.Fixture f, EfAdoptionCandidateV1 candidate, string mutation = "")
    {
        var p = candidate.Read();
        var value = new EfAdoptionApprovalV1(1, p.Scope, p.OperationId,
            mutation == "candidate" ? new string('0', 64) : candidate.Sha256,
            mutation == "incarnation" ? "other-incarnation" : p.Pre.Observation.DatabaseIncarnation,
            Now.AddMinutes(-1), mutation == "expired" ? Now : Now.AddMinutes(4),
            CertificationApprovalRequirement.Human, new("offline-human", "offline-only", "offline-policy", "offline-authority"));
        var r = f.Provider.Publish("adoption-approval", "EF_ADOPTION_APPROVAL", value);
        if (mutation == "approval-role") f.Provider.Allowed.Remove((r.EvidenceId, "EF_ADOPTION_APPROVAL"));
        return r;
    }
    private static async Task<(EfAdoptionCandidateV1 Candidate, EvidenceReference Approval)> Ready(EfPreparationTests.Fixture f)
    {
        var r = await Run(f); Status(r, "PENDING_APPROVAL");
        return (r.Candidate!, Consent(f, r.Candidate!));
    }
    private static Task<EfAdoptionCommitResultV1> Commit(EfPreparationTests.Fixture f, EfAdoptionCandidateV1 candidate,
        EvidenceReference approval, ICertificationCommitStore store, string mutation = "")
    {
        var o = f.Pre.Observation with { CapturedAtUtc = Now };
        if (mutation == "security") o = o with { SecurityHash = Hashing.Sha256("changed GRANT DENY owner roles principals") };
        if (mutation == "data") o = o with { DataHash = Hashing.Sha256("changed business data") };
        if (mutation == "incarnation") o = o with { DatabaseIncarnation = "changed-incarnation" };
        var fresh = EfPreparationTests.Observe(f.Provider, o, "FRESH_PRE", f.Governance.ContentHashContract);
        var reference = f.Provider.Publish("adoption-fresh-pre", "FRESH_PRE", fresh);
        return new EfEvidencePreparationV1(f.Provider, certificationStore: store).CommitAdoptionAsync(candidate,
            candidate.Sha256, f.Selection.Scope, reference, f.Selection.Source, approval, Now);
    }
    private static EfPreparationTests.Fixture Persisted(EfPreparationTests.Fixture f, EfAdoptionCandidateV1 candidate,
        InMemoryCertificationCommitStore store, CertificationCommitResult commit, bool transition)
    {
        var record = commit.Record!;
        f.Provider.Admit(record.CanonicalSchemaEvidenceReference, "CERTIFIED_CANONICAL_SCHEMA",
            Encoding.UTF8.GetBytes(record.CanonicalSchemaEvidence!.Json));
        var proof = f.Provider.Publish("persisted-proof", "EF_ADOPTION_PERSISTED_STATE", new EfAdoptionPersistedV1(f.Selection.Scope,
            candidate.Sha256, record, store.GetCurrentPointer(record.DatabaseIdentity)!, store.GetReceipt(f.Selection.OperationId)!, Now));
        f = f with { Selection = f.Selection with { Adoption = f.Selection.Adoption! with { Persisted = proof } } };
        if (!transition) return f;
        var baseline = f.Provider.Publish("adopted-baseline", "CERTIFIED_BASELINE", record);
        var g = f.Governance with { AdoptionPolicy = f.Governance.AdoptionPolicy! with { StructuralState = "CERTIFIED" },
            Authority = f.Governance.Authority with { OnboardingState = "MANAGED", CertifiedBaseline = new(record.CanonicalSchemaHash,
                new("platform/governance", f.Governance.Authority.Governance.Revision, baseline)) } };
        var governed = f.Provider.Publish("adopted-governance", "GOVERNANCE", g);
        var receipt = f.Provider.Publish("governed-transition", "EF_ADOPTION_GOVERNANCE_TRANSITION", new EfAdoptionTransitionV1(1,
            f.Selection.Scope, candidate.Sha256, record.CertificationId, record.CertificationEvidenceHash,
            f.Pre.Observation.DatabaseIncarnation, g.Authority.Governance, g.Authority.Onboarding,
            "EXISTING", "EF_MIGRATIONS", "MANAGED", baseline, Now, governed));
        return f with { Governance = g, Selection = f.Selection with { Governance = governed,
            Adoption = f.Selection.Adoption! with { Transition = receipt } } };
    }
    private static async Task Eligibility(int pending)
    { var f = await Make(pending); Assert(f.Pre.Discovery.Physical.BusinessObjectCount > 0, "not populated"); Status(await Run(f), "ADOPTION_REQUIRED"); }
    private static async Task Review()
    { var f = await Make(); Status(await Run(WithReview(f)), "PENDING_APPROVAL"); Status(await Run(WithReview(f, "manual")), "PENDING_APPROVAL"); }
    private static async Task Registration()
    { var f = WithReview(await Make(mutation: "registration")); var r = await Run(f); Status(r, "ADOPTION_REQUIRED"); Assert(r.Reasons.Contains("TARGET_REGISTRATION_REQUIRED"), "registration hidden as drift"); }
    private static async Task Approval()
    { var f = WithReview(await Make()); var ready = await Ready(f); f = f with { Selection = f.Selection with { Adoption = f.Selection.Adoption! with { Approval = ready.Approval } } }; Status(await Run(f), "CERTIFICATION_COMMIT_REQUIRED"); }
    private static async Task Persistence()
    {
        var f = WithReview(await Make()); var (candidate, approval) = await Ready(f); var store = new InMemoryCertificationCommitStore();
        var r = await Commit(f, candidate, approval, store); Assert(r.Status == "GOVERNANCE_TRANSITION_REQUIRED", string.Join(",", r.Reasons));
        Assert(r.Commit!.Record!.CertificationOrigin == CertificationOrigin.BootstrapApproved
            && r.Commit.Record.PreviousCertificationId is null && store.ListCertifiedHistory(r.Commit.DatabaseIdentity).Count == 1, "wrong initial origin");
        Status(await Run(Persisted(f, candidate, store, r.Commit, false)), "GOVERNANCE_TRANSITION_REQUIRED");
        var replay = await Commit(f, candidate, approval, store);
        Assert(replay.Commit?.Status == CertificationCommitStatus.IdempotentReplay && store.ListCertifiedHistory(r.Commit.DatabaseIdentity).Count == 1, "duplicate baseline");
    }
    private static async Task AlreadyManaged()
    {
        foreach (var pending in new[] { 0, 1 })
        {
            var f = WithReview(await Make(pending)); var (candidate, approval) = await Ready(f); var store = new InMemoryCertificationCommitStore();
            var committed = await Commit(f, candidate, approval, store); var managed = Persisted(f, candidate, store, committed.Commit!, true);
            var r = await Run(managed); Status(r, "EXISTING_EF_MANAGED"); Assert(r.ManagedVariant == (pending == 0 ? "V5" : "V4"), "wrong continuation");
            Assert(store.ListCertifiedHistory(committed.Commit!.DatabaseIdentity).Count == 1, "read path persisted");
        }
    }
    private static async Task Uncertainty()
    {
        var f = WithReview(await Make()); var (candidate, approval) = await Ready(f); var store = new InMemoryCertificationCommitStore();
        var uncertain = await Commit(f, candidate, approval, new EfBaselineCommitTests.LostCas(store));
        Assert(uncertain.Status == "ADOPTION_COMMIT_UNCERTAIN" && uncertain.Commit!.PointerAdvance == CommitOutcomeState.Unknown, "uncertain CAS concealed");
        var recovered = await Commit(f, candidate, approval, store);
        Assert(recovered.Status == "GOVERNANCE_TRANSITION_REQUIRED" && store.ListCertifiedHistory(recovered.Commit!.DatabaseIdentity).Count == 1, "recovery duplicated certificate");
    }
    private static async Task Concurrency()
    {
        var f = WithReview(await Make()); var g = WithReview(await Make());
        g = g with { Selection = g.Selection with { OperationId = "00000000-0000-0000-0000-000000000009" } };
        var first = await Ready(f); var second = await Ready(g); var store = new InMemoryCertificationCommitStore();
        var results = await Task.WhenAll(Task.Run(() => Commit(f, first.Candidate, first.Approval, store)),
            Task.Run(() => Commit(g, second.Candidate, second.Approval, store)));
        Assert(results.Count(x => x.Status == "GOVERNANCE_TRANSITION_REQUIRED") == 1
            && store.ListCertifiedHistory(results.First(x => x.Commit?.Record is not null).Commit!.DatabaseIdentity).Count == 1, "concurrent adoption escaped CAS");
    }
    private static async Task Historical()
    { var f = WithReview(await Make()); var r = await Run(f); Status(r, "PENDING_APPROVAL"); var p = r.Candidate!.Read(); Assert(p.Source.Revision != p.Lineage.HistoricalSource.Revision && p.Source.Repository == p.Lineage.HistoricalSource.Repository, "provenance conflated"); }
    private static async Task History()
    { foreach (var mutation in new[] { "unknown", "duplicate", "reordered", "removed", "divergent" }) Status(await Run(await Make(mutation: mutation)), "BLOCKED"); }
    private static async Task Coverage()
    {
        foreach (var mutation in new[] { "coverage", "partial-data", "metadata" }) Status(await Run(await Make(mutation: mutation)), "BLOCKED");
        Status(await Run(WithReview(await Make(), "drift")), "BLOCKED");
        var f = await Make(); var r = await Run(f); Status(r, "ADOPTION_REQUIRED");
        var store = new InMemoryCertificationCommitStore();
        Assert((await Commit(f, r.Candidate!, Consent(f, r.Candidate!), store)).Status == "BLOCKED" && store.ReceiptCount == 0, "approval replaced missing structural review");
    }
    private static async Task HistoryStructure()
    { foreach (var mutation in new[] { "absent", "empty", "structure", "pk", "index" }) Status(await Run(await Make(mutation: mutation)), "BLOCKED"); }
    private static async Task Adversarial()
    {
        foreach (var mutation in new[] { "server", "database", "transport", "missing-db", "hash", "role", "replay" })
            Status(await Run(await Make(mutation: mutation)), "BLOCKED");
        foreach (var mutation in new[] { "candidate", "incarnation", "expired", "approval-role", "security", "data", "changed", "cancelled" })
        {
            var f = WithReview(await Make()); var (candidate, _) = await Ready(f); var approval = Consent(f, candidate, mutation);
            var store = new InMemoryCertificationCommitStore(); if (mutation == "changed") f.Provider.Changed = true;
            if (mutation == "cancelled") { using var c = new CancellationTokenSource(); c.Cancel(); Status(await Run(f, c.Token), "BLOCKED"); continue; }
            var result = await Commit(f, candidate, approval, store, mutation);
            Assert(result.Status == "BLOCKED" && store.ReceiptCount == 0, mutation + " wrote simulated store");
        }
        var valid = WithReview(await Make()); var ready = await Ready(valid);
        var noStore = await new EfEvidencePreparationV1(valid.Provider).CommitAdoptionAsync(ready.Candidate, ready.Candidate.Sha256,
            valid.Selection.Scope, valid.Selection.Pre, valid.Selection.Source, ready.Approval, Now);
        Assert(noStore.Status == "BLOCKED", "missing accredited store accepted");
        Assert((await valid.Run()).Status == "BLOCKED", "adoption selectors entered normal migration route");
    }
    private static async Task Continuity()
    {
        var f = WithReview(await Make()); var (candidate, approval) = await Ready(f); var store = new InMemoryCertificationCommitStore();
        var committed = await Commit(f, candidate, approval, store); var current = committed.Commit!.Record!;
        f = Persisted(f, candidate, store, committed.Commit, true); Status(await Run(f), "EXISTING_EF_MANAGED");
        // Actual existing A2.1, qualification and baseline commit; only providers
        // and persistence are simulated. No adoption flag bypasses V4/V5.
        var source = Read<EfSourcePreparationV1>(f, f.Selection.Source).Source;
        f.Provider.Admit(f.Governance.Authority.CertifiedBaseline!.Provenance.Document, "CURRENT_CERTIFIED_BASELINE", Bytes(current));
        f.Provider.Admit(source.AdoptionProvenance!.Document, "ADOPTION", f.Provider.Content[source.AdoptionProvenance.Document.EvidenceId]);
        f = f.Replace("SOURCE", new EfSourcePreparationV1(source, f.Original.Forward.Bytes, f.Original.Rollback.Bytes));
        f = f with { Selection = f.Selection with { Adoption = null, OperationId = "00000000-0000-0000-0000-000000000015",
            Qualification = f.Provider.Publish("adopted-qualification", "ISOLATED_QUALIFICATION", f.Qualification) } };
        f = EfUnifiedClassificationTests.Admit(f);
        var v4 = await f.Run(); Assert(v4.Status == "PLAN_EVIDENCE_CONSISTENT_NOT_AUTHORIZED" && v4.PlanResult!.Plan!.Read().PendingMigrationIds.Length == 1, "V4: " + string.Join(",", v4.Reasons));
        var pointer = committed.Commit.CurrentPointer!;
        var release = await EfBaselineCommitTests.Make(existing: f, store: store,
            predecessor: new(pointer.CertificationId, pointer.CertificationEvidenceHash, pointer.Version));
        var result = await release.Run(); Assert(result.Status == "BASELINE_COMMITTED_GOVERNANCE_TRANSITION_REQUIRED", "second release: " + string.Join(",", result.Reasons));
        current = result.Commit!.Record!;
        var baseline = f.Provider.Publish("release-certified", "CERTIFIED_BASELINE", current);
        var post = EfPreparationTests.Observe(f.Provider, f.Original.Request.ExpectedPost, "TARGET_PRE", f.Governance.ContentHashContract);
        f = f.Replace("TARGET_PRE", post) with { Pre = post };
        f = f.Replace("SOURCE", new EfSourcePreparationV1(source, [], []));
        var g = f.Governance with { Authority = f.Governance.Authority with { CertifiedBaseline = new(current.CanonicalSchemaHash,
            new("platform/governance", f.Governance.Authority.Governance.Revision, baseline)) } };
        f = f.Replace("GOVERNANCE", g) with { Governance = g, Selection = f.Selection with {
            Governance = f.Provider.Publish("release-governance", "GOVERNANCE", g), PreSources = null, Qualification = null } };
        var v5 = await EfUnifiedClassificationTests.Admit(f).Run();
        Assert(v5.Status == "NO_CHANGES" && !v5.ExecutionAuthorized && store.ListCertifiedHistory(current.DatabaseIdentity).Count == 2, "V5: " + string.Join(",", v5.Reasons));
    }
    private sealed class Factory(EfPreparationTests.Fixture f, bool wait = false) : IEfOperationalSessionFactoryV1
    {
        public async Task<EfOperationalSessionV1> OpenAsync(EfOperationalRequestV1 r, CancellationToken t)
        { if (wait) await Task.Delay(Timeout.Infinite, t); return new(f.Provider, [], []); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    internal static EfOperationalRequestV1 Request(EfPreparationTests.Fixture f) => new(1, "ADOPTION",
        f.Governance.Authority.Target.ApplicationId, "TEST", f.Governance.Authority.Governance.Revision, new string('f', 40), f.Selection);
    internal static async Task<int> Child(string[] args)
    {
        var f = await Make(); if (args[0] != "ADOPTION_REQUIRED") f = WithReview(f);
        if (args[0] == "CERTIFICATION_COMMIT_REQUIRED") { var r = await Ready(f); f = f with { Selection = f.Selection with { Adoption = f.Selection.Adoption! with { Approval = r.Approval } } }; }
        if (args[0] is "GOVERNANCE_TRANSITION_REQUIRED" or "EXISTING_EF_MANAGED")
        {
            var ready = await Ready(f); var store = new InMemoryCertificationCommitStore();
            var committed = await Commit(f, ready.Candidate, ready.Approval, store);
            f = Persisted(f, ready.Candidate, store, committed.Commit!, args[0] == "EXISTING_EF_MANAGED");
        }
        if (args[0] == "BLOCKED") f.Provider.Changed = true;
        await File.WriteAllBytesAsync(args[1], Bytes(Request(f)));
        return await EfOperationalCli.RunAsync(["--request", args[1], "--output", args[2], "--timeout-seconds", "1"], new Factory(f, args[0] == "TIMEOUT"), clock: new Clock());
    }
    private static async Task Host()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ef-adoption-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var state in new[] { "ADOPTION_REQUIRED", "PENDING_APPROVAL", "CERTIFICATION_COMMIT_REQUIRED", "GOVERNANCE_TRANSITION_REQUIRED", "EXISTING_EF_MANAGED", "BLOCKED", "TIMEOUT" })
            {
                var output = Path.Combine(directory, state);
                // The same shipped CLI is invoked with an explicit test factory.
                var f = WithReview(await Make());
                var result = await EfOperationalHostV1.RunAsync(Request(f), new Factory(f), Now);
                Status(new(result.Result.Status, result.AdoptionCandidate, result.Result.Reasons), "PENDING_APPROVAL");
                Assert(result.Review is null && result.PreparedPlan is null && result.Result.AdoptionCandidateHash is not null, "adoption leaked executable plan");
                var code = await Child([state, Path.Combine(directory, state + ".json"), output]);
                Assert(code == (state == "BLOCKED" ? 66 : state == "TIMEOUT" ? 124 : 0), "CLI exit " + state);
                var serialized = await File.ReadAllTextAsync(Path.Combine(output, "ef-result.json"));
                Assert(!serialized.Contains("Customer") && !serialized.Contains("offline-human") && !serialized.Contains("CREATE TABLE"), "private adoption evidence published");
            }
        }
        finally { Cleanup(directory); }
    }
    private static async Task ActualRepository()
    {
        var f = await Make(); var source = Read<EfSourcePreparationV1>(f, f.Selection.Source);
        var lineage = Read<EfAdoptionLineageV1>(f, f.Selection.Adoption!.Lineage);
        var directory = Path.Combine(Path.GetTempPath(), "ef-adoption-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            async Task<EvidenceReference> Actual(EvidenceReference reference, string purpose, string prefix)
            {
                var document = Read<EfAdoptionRepositoryV1>(f, reference); var workspace = Path.Combine(directory, prefix);
                Directory.CreateDirectory(Path.Combine(workspace, "src/Infrastructure/Migrations"));
                await File.WriteAllTextAsync(Path.Combine(workspace, "src/Infrastructure/Infrastructure.csproj"), "<Project />");
                foreach (var file in document.ArtifactEvidence)
                    await File.WriteAllBytesAsync(Path.Combine(workspace, file.Key), f.Provider.Content[file.Value.EvidenceId]);
                var script = Path.GetFullPath("../../../../../scripts/discover-repository-v2.mjs", AppContext.BaseDirectory);
                var start = new System.Diagnostics.ProcessStartInfo("node") { UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e");
                start.ArgumentList.Add("import fs from 'node:fs'; import {pathToFileURL} from 'node:url'; const script=process.argv[1], workspace=process.argv[2]; process.argv[1]='offline-driver'; const {inspectRawRepository,projectManagedRepository}=await import(pathToFileURL(script)); const q=JSON.parse(fs.readFileSync(0,'utf8')); console.log(JSON.stringify(projectManagedRepository(inspectRawRepository(workspace),q,q.sourceRevision)));");
                start.ArgumentList.Add(script); start.ArgumentList.Add(workspace);
                using var child = System.Diagnostics.Process.Start(start)!;
                await child.StandardInput.WriteAsync(JsonSerializer.Serialize(new { sourceRepository = document.Repository,
                    sourceRevision = document.Revision, projectPath = source.Source.ProjectPath,
                    managedMigrationStartId = document.RepositorySource.GetProperty("migrations").GetProperty("ids")[0].GetString(),
                    preAdoptionArtifacts = Array.Empty<object>() })); child.StandardInput.Close();
                var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await child.WaitForExitAsync(deadline.Token);
                Assert(child.ExitCode == 0, await error);
                var actual = JsonSerializer.Deserialize<JsonElement>(await output);
                Assert(actual.GetProperty("status").GetString() == "PRESENT_VALID", actual.ToString());
                return f.Provider.Publish(prefix + "-actual-repository", purpose, document with { RepositorySource = actual });
            }
            var current = await Actual(source.Source.RepositoryEvidence, "REPOSITORY_DISCOVERY_V2", "current");
            var historical = await Actual(lineage.HistoricalRepositoryEvidence, "EF_ADOPTION_HISTORICAL_REPOSITORY", "historical");
            var updatedSource = f.Provider.Publish("actual-source", "SOURCE", source with { Source = source.Source with { RepositoryEvidence = current } });
            var updatedLineage = f.Provider.Publish("actual-lineage", "EF_ADOPTION_LINEAGE", lineage with { Source = updatedSource, HistoricalRepositoryEvidence = historical });
            f = f with { Selection = f.Selection with { Source = updatedSource, Adoption = new(updatedLineage) } };
            Status(await Run(WithReview(f)), "PENDING_APPROVAL");
        }
        finally { Cleanup(directory); }
    }
    private static async Task PersistenceNegatives()
    {
        foreach (var mutation in new[] { "receipt", "pointer", "candidate", "candidate-and-transition", "canonical", "transition", "baseline", "role", "not-managed" })
        {
            var f = WithReview(await Make()); var ready = await Ready(f); var store = new InMemoryCertificationCommitStore();
            var committed = await Commit(f, ready.Candidate, ready.Approval, store);
            f = Persisted(f, ready.Candidate, store, committed.Commit!, true);
            var a = f.Selection.Adoption!; var proof = Read<EfAdoptionPersistedV1>(f, a.Persisted!);
            if (mutation == "receipt") proof = proof with { Receipt = proof.Receipt with { Status = CertificationCommitReceiptStatus.Prepared } };
            if (mutation == "pointer") proof = proof with { Pointer = proof.Pointer with { CertificationEvidenceHash = new string('0', 64) } };
            if (mutation is "candidate" or "candidate-and-transition") proof = proof with { CandidateHash = new string('0', 64) };
            if (mutation == "canonical") f.Provider.Content[proof.Record.CanonicalSchemaEvidenceReference.EvidenceId] = [1, 2, 3];
            var transition = Read<EfAdoptionTransitionV1>(f, a.Transition!);
            if (mutation == "candidate-and-transition") transition = transition with { CandidateHash = proof.CandidateHash };
            if (mutation == "transition") transition = transition with { DatabaseIncarnation = "different-incarnation" };
            if (mutation == "baseline") transition = transition with { CertificationId = Guid.NewGuid().ToString("D") };
            if (mutation == "role") f.Provider.Allowed.Remove((transition.GovernedState.EvidenceId, "GOVERNANCE"));
            if (mutation == "not-managed")
            {
                var g = f.Governance with { Authority = f.Governance.Authority with { OnboardingState = "REQUIRED" } };
                transition = transition with { GovernedState = f.Provider.Publish("false-managed", "GOVERNANCE", g) };
            }
            var proofRef = f.Provider.Publish("negative-proof", "EF_ADOPTION_PERSISTED_STATE", proof);
            var transitionRef = f.Provider.Publish("negative-transition", "EF_ADOPTION_GOVERNANCE_TRANSITION", transition);
            f = f with { Selection = f.Selection with { Adoption = a with { Persisted = proofRef, Transition = transitionRef } } };
            Status(await Run(f), "BLOCKED");
        }
    }
    private static void Cleanup(string directory)
    {
        Assert(Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(directory).StartsWith("ef-adoption-", StringComparison.Ordinal), "unsafe test cleanup");
        Directory.Delete(directory, true);
    }
    private sealed class ForbiddenExecution : IEfProtectedExecutionHostV1
    {
        internal int Calls;
        public Task<IRehearsalDatabase?> BeginOnceAsync(EfExecutionStartV1 s, CancellationToken t)
        { Calls++; throw new Exception("adoption must never execute"); }
        public Task<EvidenceReference> RecordAsync(EfExecutionStartV1 s, string o, DateTimeOffset t)
        { Calls++; throw new Exception("adoption must never execute"); }
    }
    private static async Task NoSqlAuthority()
    {
        var f = WithReview(await Make()); var host = new ForbiddenExecution();
        var r = await new EfEvidencePreparationV1(f.Provider, host).PrepareAdoptionAsync(f.Selection, Now);
        Status(r, "PENDING_APPROVAL"); Assert(host.Calls == 0, "adoption invoked SQL host");
    }
}
