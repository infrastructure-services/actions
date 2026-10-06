using System.Text;
using DatabaseReleaseQualification;
using SqlDiscovery.V2;

internal static class NewEfCreationFirstTests
{
    private static readonly byte[] Up=Encoding.UTF8.GetBytes(NewEfSql.UpTemplate);
    private static readonly byte[] Down=Encoding.UTF8.GetBytes(NewEfSql.DownTemplate);
    private static readonly string Sha=new('a',40), Hash=new('b',64);
    internal static NewEfPlanV1 Plan()=>NewEfContract.Bind(new(1,"NEW_EF_CREATION_FIRST_PLAN",NewEfContract.TargetId,
        "3602","TEST",NewEfContract.Database,"DBCICDV3TEST","sqlv1testdcsrv1","sqlv1testdcsrv1","STRICT",
        "infrastructure-services/devops-prueba-migraciones-api",Sha,Sha,Sha,Sha,Hash,Sha,Hash,"123","1",
        "627d64c8-87eb-4e6d-adfd-cefbf08ac98e",DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(30),
        [NewEfContract.MigrationId],Hashing.Sha256(Up),Hashing.Sha256(Down),NewEfContract.PayloadHash(Hashing.Sha256(Up),Hashing.Sha256(Down)),
        "CREATE_ONLY_IF_CONFIRMED_ABSENT",NewEfContract.Recovery,600,""));
    internal static NewEfCreationReceipt Creation(NewEfPlanV1 p) {
        var r=new NewEfCreationReceipt(p.TargetId,p.ApplicationId,"TEST",p.ServerInstance,p.DatabaseName,p.OperationId,
            "github-environment-approval:123", "test-provisioner",DateTimeOffset.UtcNow,
            "c86bed1a-edb1-4270-a7a6-cd3fd27eebc0",p.GovernanceRevision,p.SourceRevision,p.WorkflowRevision,p.ActionsRevision,p.PlanHash,"DATABASE_CREATED","");
        return r with {ReceiptHash=NewEfContract.Hash(r)};
    }
    public static readonly (string Name,Func<Task> Run)[] Cases = BuildCases();
    private static (string,Func<Task>)[] BuildCases() {
        var tests=new List<(string,Func<Task>)> {
            ("NEW creation-first exact plan and real SQL shape",()=> {var p=Plan();NewEfContract.Verify(p,p.PlanHash,Up,Down,DateTimeOffset.UtcNow);return Task.CompletedTask;}),
            ("NEW creation-first complete fake CREATE UP DOWN UP",()=>Cycle("valid")),
            ("NEW creation-first SQL rejects unsupported/cross-target payloads",Sql),
            ("NEW creation-first connection rejects wrong original catalogs and sources",Connection),
            ("NEW creation-first repeat invocation cannot resume",Repeat)
            ,("NEW grant expiry cancels during PRE before SQL write",async()=> {
                var p=NewEfContract.Bind(Plan() with {ExpiresAtUtc=DateTimeOffset.UtcNow.AddMilliseconds(200)});
                var runtime=new Fake("grant-expires");var creation=Creation(p);
                var result=await new NewEfCycle(runtime).RunAsync(p,p.PlanHash,Up,Down,creation,creation.AuthorizationReference,CancellationToken.None);
                if(result.Status!="BLOCKED" || result.Reason!="NEW_EF_CANCELLED_OR_TIMEOUT" || runtime.Writes.Count!=0)
                    throw new Exception("Expired authority allowed write");
            })
            ,("NEW security capture proves globals before object exists",()=> {
                var empty=new SchemaSnapshot();
                var scope=SqlNewEfRuntime.SecurityScope(empty,"MigrationTestItems");
                if(scope.Securables.Count!=2 || scope.Securables.Any(s=>s.Kind=="OBJECT"))throw new Exception("Absent object must use global security");
                empty.Objects.Add(new SchemaObject {Kind="table",Schema="dbo",Name="MigrationTestItems"});
                if(SqlNewEfRuntime.SecurityScope(empty,"MigrationTestItems").Securables.Count!=3)throw new Exception("Observed object security required");
                if(SqlNewEfRuntime.SecurityScope(empty,"__EFMigrationsHistory").Securables.Count!=2)throw new Exception("History must remain absent");
                return Task.CompletedTask;
            })
        };
        foreach(var (name,mutate) in new (string,Func<NewEfPlanV1,NewEfPlanV1>)[] {
            ("QA",p=>p with {Environment="QA"}), ("PROD",p=>p with {Environment="PROD"}),
            ("CICDV3",p=>p with {DatabaseName="CICDV3"}), ("wrong-target",p=>p with {TargetId="other"}),
            ("wrong-app",p=>p with {ApplicationId="other"}), ("wrong-server",p=>p with {ServerInstance="prod"}),
            ("missing-source",p=>p with {SourceRevision="main"}), ("missing-binding",p=>p with {ConnectionDataSource=""}),
            ("operation",p=>p with {OperationId="unknown"}), ("rerun",p=>p with {RunAttempt="2"}),
            ("expired",p=>p with {ExpiresAtUtc=DateTimeOffset.UtcNow.AddSeconds(-1)}),
            ("future",p=>p with {PreparedAtUtc=DateTimeOffset.UtcNow.AddMinutes(5)}),
            ("too-long-window",p=>p with {ExpiresAtUtc=DateTimeOffset.UtcNow.AddHours(2)}),
            ("duplicate-lineage",p=>p with {MigrationIds=[NewEfContract.MigrationId,NewEfContract.MigrationId]}),
            ("up-hash",p=>p with {UpHash=Hash}), ("down-hash",p=>p with {DownHash=Hash}),
            ("payload-hash",p=>p with {PayloadHash=Hash}), ("recovery-policy",p=>p with {RecoveryStrategy="DROP"}),
            ("provisioning-policy",p=>p with {ProvisioningIntent="ADOPT_IF_EXISTS"}),
            ("TLS",p=>p with {TlsMode="AUTO"}), ("timeout",p=>p with {TimeoutSeconds=9999}),
            ("governance-revision",p=>p with {GovernanceRevision=new string('c',40)})
        }) tests.Add(("NEW plan rejects "+name,()=> {
            var p=NewEfContract.Bind(mutate(Plan()));Expect(()=>NewEfContract.Verify(p,p.PlanHash,Up,Down,DateTimeOffset.UtcNow));return Task.CompletedTask;
        }));
        foreach(var (name,mutate) in new (string,Func<NewEfServerEvidence,NewEfServerEvidence>)[] {
            ("already-existing-empty",e=>e with {DatabaseExists=true}), ("wrong-server",e=>e with {Server="prod"}),
            ("wrong-catalog",e=>e with {Catalog="CICDV3"}), ("unknown-principal",e=>e with {Principal=""}),
            ("insufficient-visibility",e=>e with {CanSeeDatabases=false}), ("no-create-authority",e=>e with {CanCreate=false}),
            ("hidden-DDL-trigger",e=>e with {CanSeeDefinitions=false}), ("server-DDL-trigger",e=>e with {EnabledDdlTrigger=true})
        }) tests.Add(("NEW CREATE blocks "+name,()=> {
            Expect(()=>NewEfCreationGuard.Verify(Plan(),mutate(new("sqlv1testdcsrv1","master","creator",true,false,true,true,false)),true));return Task.CompletedTask;
        }));
        foreach(var fault in new[] {"classification","not-found","populated","history-empty-before-UP","taxonomy-partial",
            "incarnation","identity","schema-coverage","up1-failure","down-failure","pre2-nonempty","pre2-security",
            "history-schema","history-security","post2-schema","post2-security","post-history","post-data","post-version","cancelled","receipt-swap"}) {
            var name=fault; tests.Add(("NEW cycle fail-closes "+name,()=>Cycle(name)));
        }
        return tests.ToArray();
    }
    private static void Expect(Action action) {
        try {action();}catch(LegacyContractException){return;}throw new Exception("Expected fail-closed block");
    }
    private static Task Sql() {
        foreach(var sql in new[] {NewEfSql.UpTemplate+"\nDROP DATABASE [CICDV3];\nGO\n",
            NewEfSql.UpTemplate.Replace("[dbo].[MigrationTestItems]","[CICDV3].[dbo].[MigrationTestItems]"),
            NewEfSql.UpTemplate.Replace("datetime2","datetime"),NewEfSql.UpTemplate+"\nEXEC(N'SELECT 1');\nGO\n",
            NewEfSql.UpTemplate.Replace("GO","GO 2"),"BEGIN TRANSACTION;\n"+NewEfSql.UpTemplate,
            NewEfSql.UpTemplate+"\nINSERT INTO dbo.MigrationTestItems VALUES (N'x',GETUTCDATE());\nGO\n"})
            Expect(()=>NewEfSql.Validate(Encoding.UTF8.GetBytes(sql),"UP"));
        return Task.CompletedTask;
    }
    private static Task Connection() {
        var p=Plan();
        var strict=SqlNewEfRuntime.Options("Server=sqlv1testdcsrv1;Database=master;Integrated Security=true",p,"master");
        if(!strict.Encrypt.ToString().Equals("Strict")||strict.TrustServerCertificate||strict.ConnectRetryCount!=0)throw new Exception("TLS/retry");
        var test=SqlNewEfRuntime.Options(strict.ConnectionString,p with {TlsMode="TEST_UNTRUSTED_CERTIFICATE"},"master");
        if(!test.TrustServerCertificate)throw new Exception("Explicit TEST TLS missing");
        foreach(var raw in new[] {"Server=sqlv1testdcsrv1;Database=CICDV3", "Server=prod;Database=master", "Server=sqlv1testdcsrv1;Database=master;AttachDBFilename=x.mdf"})
            Expect(()=>SqlNewEfRuntime.Options(raw,p,"master"));
        return Task.CompletedTask;
    }
    private static async Task Cycle(string fault) {
        var p=Plan();var runtime=new Fake(fault);var creation=await runtime.CreateAsync(p,"github-environment-approval:123",CancellationToken.None);
        if(fault=="receipt-swap") {
            creation=creation with {OperationId=Guid.NewGuid().ToString()};
            creation=creation with {ReceiptHash=NewEfContract.Hash(creation with {ReceiptHash=""})};
            try { await new NewEfCycle(runtime).RunAsync(p,p.PlanHash,Up,Down,creation,"github-environment-approval:123",CancellationToken.None); }
            catch(LegacyContractException) { if(runtime.Writes.Count!=0)throw new Exception("write before receipt guard");return; }
            throw new Exception("forged receipt accepted");
        }
        var result=await new NewEfCycle(runtime).RunAsync(p,p.PlanHash,Up,Down,creation,"github-environment-approval:123",CancellationToken.None);
        if(fault=="valid") {
            if(result.Status!="NEW_EF_REHEARSAL_COMPLETE_NOT_CERTIFIED"||!result.RecoveryVerified||!result.ReapplyVerified
                ||!runtime.Writes.SequenceEqual(new[]{"UP","DOWN","UP"}))throw new Exception("Cycle incomplete");
            var pre=result.Phases.Single(x=>x.Phase=="PRE");var pre2=result.Phases.Single(x=>x.Phase=="PRE2");
            var post1=result.Phases.Single(x=>x.Phase=="POST1");var post2=result.Phases.Single(x=>x.Phase=="POST2");
            if(pre.BusinessSchemaHash is null || pre.BusinessSchemaHash!=pre2.BusinessSchemaHash
                || pre.BusinessSecurityHash is null || pre.BusinessSecurityHash!=pre2.BusinessSecurityHash
                || post1.HistorySchemaHash is null || post1.HistorySchemaHash!=pre2.HistorySchemaHash
                || post1.HistorySecurityHash is null || post1.HistorySecurityHash!=pre2.HistorySecurityHash
                || post1.SchemaHash!=post2.SchemaHash || post1.DatabaseIncarnation!=creation.DatabaseIncarnation
                || post2.DataEmpty!=true || post2.HistoryProductVersionValid!=true
                || !post2.MigrationIds!.SequenceEqual(p.MigrationIds))throw new Exception("Receipt omits recovery evidence");
        } else {
            if(result.Status!="BLOCKED")throw new Exception("Fault accepted: "+fault);
            if(fault is "classification" or "not-found" or "populated" or "history-empty-before-UP" or "taxonomy-partial"
                or "incarnation" or "identity" or "schema-coverage" or "cancelled")
                if(runtime.Writes.Count!=0)throw new Exception("write after invalid pre-state");
            if(fault is "down-failure" or "pre2-nonempty" or "pre2-security" or "history-schema" or "history-security")
                if(runtime.Writes.Count!=2)throw new Exception("Reapply after failed recovery");
        }
    }
    private static async Task Repeat() {
        var p=Plan();var runtime=new Fake("valid");var c=Creation(p);var engine=new NewEfCycle(runtime);
        await engine.RunAsync(p,p.PlanHash,Up,Down,c,c.AuthorizationReference,CancellationToken.None);
        try {await engine.RunAsync(p,p.PlanHash,Up,Down,c,c.AuthorizationReference,CancellationToken.None);}catch(LegacyContractException){return;}
        throw new Exception("resume accepted");
    }
    private sealed class Fake(string fault) : INewEfRuntime {
        public List<string> Writes {get;}=[];
        public Task<NewEfCreationReceipt> CreateAsync(NewEfPlanV1 p,string authorization,CancellationToken token) {
            NewEfCreationGuard.Verify(p,new(p.ServerInstance,"master","creator",true,false,true,true,false),true);
            return Task.FromResult(Creation(p));
        }
        public Task VerifyClassificationAsync(NewEfPlanV1 p,CancellationToken token) {
            if(fault=="classification")throw new LegacyContractException("CLASSIFICATION_BLOCKED");return Task.CompletedTask;
        }
        public Task ExecuteAsync(NewEfPlanV1 p,byte[] bytes,string phase,NewEfObservation expected,CancellationToken token) {
            Writes.Add(phase);NewEfSql.Validate(bytes,phase);
            if(fault=="up1-failure"&&Writes.Count==1||fault=="down-failure"&&phase=="DOWN")throw new Exception("secret=must-not-leak");
            return Task.CompletedTask;
        }
        public Task<NewEfObservation> CaptureAsync(NewEfPlanV1 p,CancellationToken token) {
            if(fault=="grant-expires") {
                async Task<NewEfObservation> Delayed() {await Task.Delay(TimeSpan.FromSeconds(5),token);throw new Exception("Grant expiry deadline ignored");}
                return Delayed();
            }
            if(fault=="cancelled")throw new OperationCanceledException();
            bool post=Writes.Count is 1 or 3, recovered=Writes.Count==2;
            var history=post?HistoryStatus.Present:recovered?HistoryStatus.Empty:HistoryStatus.Absent;
            if(fault=="history-empty-before-UP"&&Writes.Count==0)history=HistoryStatus.Empty;
            var counts=new Dictionary<string,long>();foreach(var category in EmptyForNewEfV1.Categories)counts[category]=0;
            var physical=new PhysicalResult(PhysicalStatus.Complete,post?1:0,TechnicalObjectCount:0,Taxonomy:new(1,"COMPLETE",counts));
            if(fault=="populated"&&Writes.Count==0||fault=="pre2-nonempty"&&recovered)physical=physical with {BusinessObjectCount=1};
            if(fault=="taxonomy-partial")physical=physical with {Taxonomy=new(1,"PARTIAL")};
            var id=new ObservedDatabaseIdentity(fault=="identity"?"prod":p.ServerInstance,p.DatabaseName);
            var discovery=new SqlDiscoveryResult(new(ConnectionStatus.Succeeded),new(fault=="not-found"?DatabaseLookupStatus.NotFoundConfirmed:DatabaseLookupStatus.Found),
                new(ConnectionStatus.Succeeded),new(ObservedIdentityStatus.Available,id),new(MetadataStatus.Sufficient),physical,
                new(history,post?(fault=="post-history"?new[]{"other"}:p.MigrationIds):[]));
            var snapshot=new SchemaSnapshot();snapshot.Objects.Add(new(){Kind="schema",Schema="dbo",Name="dbo"});
            if(post)snapshot.Objects.Add(new(){Kind="table",Schema="dbo",Name="MigrationTestItems"});
            if(fault=="post2-schema"&&Writes.Count==3)snapshot.Objects.Add(new(){Kind="table",Schema="dbo",Name="rogue"});
            if(fault=="schema-coverage")snapshot.UnsupportedSchemaFeatures.Add("unknown");
            return Task.FromResult(new NewEfObservation(discovery,snapshot,SchemaCanonicalizer.Canonicalize(snapshot).Sha256,
                "business-empty","history-shape"+(fault=="history-schema"&&recovered?"wrong":""),
                "business-security"+(fault=="pre2-security"&&recovered||fault=="post2-security"&&Writes.Count==3?"wrong":""),
                "history-security"+(fault=="history-security"&&recovered?"wrong":""),
                fault=="incarnation"?Guid.NewGuid().ToString():Creation(p).DatabaseIncarnation,
                fault!="post-data",fault!="post-version"));
        }
    }
}
