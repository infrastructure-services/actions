using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace DatabaseReleaseQualification;

public static class NewEfCli
{
    public static async Task<int> RunAsync(string operation,string[] args)
    {
        string? output=null;
        using var shutdown=new CancellationTokenSource();
        ConsoleCancelEventHandler cancelled=(_,e)=> {e.Cancel=true;shutdown.Cancel();};
        Console.CancelKeyPress+=cancelled;
        try {
            NewEfContract.Require(args.Length%2==0,"NEW_EF_ARGUMENTS_INVALID");
            var options=new Dictionary<string,string>(StringComparer.Ordinal);
            for(int i=0;i<args.Length;i+=2) {
                NewEfContract.Require(args[i].StartsWith("--",StringComparison.Ordinal),"NEW_EF_ARGUMENTS_INVALID");
                options.Add(args[i],args[i+1]);
            }
            string Required(string key)=>options.TryGetValue(key,out var value)?value:throw new LegacyContractException("NEW_EF_ARGUMENT_REQUIRED");
            output=Required("--output");
            if(operation=="PAYLOAD") {
                NewEfContract.Require(options.Count==3,"NEW_EF_ARGUMENTS_INVALID");
                var u=await File.ReadAllBytesAsync(Required("--up"));
                var d=await File.ReadAllBytesAsync(Required("--down"));
                NewEfSql.Validate(u,"UP"); NewEfSql.Validate(d,"DOWN");
                var uh=Hashing.Sha256(u); var dh=Hashing.Sha256(d);
                await Write(output,new {upHash=uh,downHash=dh,payloadHash=NewEfContract.PayloadHash(uh,dh)});
                return 0;
            }
            var p=LegacyRehearsalCli.Parse<NewEfPlanV1>(await File.ReadAllBytesAsync(Required("--plan")));
            var up=await File.ReadAllBytesAsync(Required("--up"));
            var down=await File.ReadAllBytesAsync(Required("--down"));
            var inspection=Env("NEW_EF_INSPECTION_CONNECTION_STRING");
            if(operation=="PREPARE") {
                NewEfContract.Require(options.Count==4,"NEW_EF_ARGUMENTS_INVALID");
                p=p with { ConnectionDataSource=new SqlConnectionStringBuilder(inspection).DataSource };
                p=NewEfContract.Bind(p);
                NewEfContract.Verify(p,p.PlanHash,up,down,DateTimeOffset.UtcNow);
                await VerifyLocalSources(p,false,shutdown.Token);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(60));
                await using var c=new SqlConnection(SqlNewEfRuntime.Options(inspection,p,"master").ConnectionString);
                await c.OpenAsync(deadline.Token);
                await SqlNewEfRuntime.VerifyServerAbsenceAsync(c,p,false,deadline.Token);
                await Write(output,p);
                Console.WriteLine("READY_FOR_HUMAN_TEST_WRITE_AUTHORIZATION:"+p.PlanHash);
                return 0;
            }
            var trusted=Env("NEW_EF_TRUSTED_PLAN_HASH");
            NewEfContract.Verify(p,trusted,up,down,DateTimeOffset.UtcNow);
            var authorization=Env("NEW_EF_AUTHORIZATION_REFERENCE");
            NewEfContract.Require(authorization.StartsWith("github-environment-approval:",StringComparison.Ordinal),"NEW_EF_AUTHORIZATION_REQUIRED");
            async Task Authority(CancellationToken token) {
                NewEfContract.Verify(p,trusted,up,down,DateTimeOffset.UtcNow);
                await VerifyLocalSources(p,true,token);
                // The pinned workflow guard rechecks remote main containment and
                // actual protected-environment approval before EACH SQL phase.
                var script=Path.Combine(Env("GITHUB_WORKSPACE"),".new/workflow/.github/workflows/scripts/new-ef-authorization.mjs");
                await Command("node",[script,"VERIFY",p.PlanHash],token);
            }
            async Task Classified(CancellationToken token) {
                token.ThrowIfCancellationRequested();
                using var doc=JsonDocument.Parse(await File.ReadAllBytesAsync(Required("--classification"),token));
                var r=doc.RootElement;
                var g=r.GetProperty("governance");
                var v=r.GetProperty("classification").GetProperty("classificationResult");
                NewEfContract.Require(r.GetProperty("artifactKind").GetString()=="CLASSIFICATION_V2_TEST_READ_ONLY"
                    && r.GetProperty("status").GetString()=="CLASSIFIED" && r.GetProperty("classificationInvoked").GetBoolean()
                    && r.GetProperty("environment").GetString()=="TEST" && r.GetProperty("targetId").GetString()==p.TargetId
                    && g.GetProperty("databaseLifecycle").GetString()=="NEW" && g.GetProperty("changeManagementMode").GetString()=="EF_MIGRATIONS"
                    && g.GetProperty("binding").GetProperty("databaseName").GetString()==p.DatabaseName
                    && r.GetProperty("sourceProvenance").GetProperty("sourceRevision").GetString()==p.GovernanceRevision
                    && r.GetProperty("sourceProvenance").GetProperty("sourceSha256").GetString()==p.GovernanceHash
                    && v.GetProperty("decision").GetProperty("status").GetString()=="ELIGIBLE_FOR_NEXT_READ_ONLY_STAGE"
                    && v.GetProperty("inferences").GetProperty("scenario").GetString()=="NEW_EF","NEW_EF_CLASSIFICATION_REQUIRED");
                var provenance=r.GetProperty("evidenceProvenance");
                foreach(var (kind,sourcePath) in new[] {("SQL_DISCOVERY","sql-discovery-v2/action.yml"),
                    ("REPOSITORY","repository-discovery-v2/action.yml"),("SCHEMA_CAPTURE","schema-capture-new-ef-bootstrap/action.yml")}) {
                    var item=provenance.GetProperty(kind);
                    NewEfContract.Require(item.GetProperty("sourceRepository").GetString()=="infrastructure-services/actions"
                        && item.GetProperty("sourcePath").GetString()==sourcePath
                        && item.GetProperty("sourceRevision").GetString()==p.ActionsRevision
                        && item.GetProperty("sourceSha256").GetString()==Hashing.Sha256(await File.ReadAllBytesAsync(
                            Path.Combine(Env("GITHUB_WORKSPACE"),".new/actions",sourcePath),token)),"NEW_EF_CLASSIFICATION_PROVENANCE_INVALID");
                }
                NewEfContract.Require(provenance.GetProperty("REGISTRY").GetProperty("sourceSha256").GetString()==p.GovernanceHash
                    && provenance.GetProperty("ONBOARDING").GetProperty("sourceRevision").GetString()==p.OnboardingRevision
                    && provenance.GetProperty("ONBOARDING").GetProperty("sourceSha256").GetString()==p.OnboardingHash,
                    "NEW_EF_CLASSIFICATION_PROVENANCE_INVALID");
                var states=r.GetProperty("sourceStates");
                foreach(var (key,value) in new[] {("databaseLookup","FOUND"),("targetConnection","SUCCEEDED"),("metadata","SUFFICIENT"),
                    ("physical","OBSERVED"),("history","ABSENT"),("repository","PRESENT_VALID"),("schema","NOT_EVALUATED"),
                    ("registry","NOT_EVALUATED"),("onboarding","NOT_REQUIRED")})
                    NewEfContract.Require(states.GetProperty(key).GetString()==value,"NEW_EF_CLASSIFICATION_SOURCES_INVALID");
            }
            var runtime=new SqlNewEfRuntime(inspection,Env("NEW_EF_PROVISIONING_CONNECTION_STRING"),
                Env("NEW_EF_MIGRATION_CONNECTION_STRING"),Authority,Classified);
            if(operation=="CREATE") {
                NewEfContract.Require(options.Count==4,"NEW_EF_ARGUMENTS_INVALID");
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(p.TimeoutSeconds));
                await Write(output+".intent.json",new {status="CREATE_STARTED_RESULT_UNKNOWN",p.OperationId,p.PlanHash});
                var receipt=await runtime.CreateAsync(p,authorization,deadline.Token);
                await Write(output,receipt);
                return 0;
            }
            NewEfContract.Require(operation=="QUALIFY" && options.Count==6,"NEW_EF_ARGUMENTS_INVALID");
            var creation=LegacyRehearsalCli.Parse<NewEfCreationReceipt>(await File.ReadAllBytesAsync(Required("--creation")));
            var journalPath=output+".journal.jsonl";
            NewEfContract.Require(!File.Exists(journalPath),"NEW_EF_RESUME_FORBIDDEN");
            var cycle=new NewEfCycle(runtime,e=>File.AppendAllText(journalPath,JsonSerializer.Serialize(e,JsonDefaults.Compact)+"\n"));
            var result=await cycle.RunAsync(p,trusted,up,down,creation,authorization,shutdown.Token);
            await Write(output,result);
            return result.Status=="NEW_EF_REHEARSAL_COMPLETE_NOT_CERTIFIED"?0:66;
        }
        catch(Exception e) {
            var reason=e is LegacyContractException c?c.Code:e is OperationCanceledException?"NEW_EF_CANCELLED_OR_TIMEOUT":"NEW_EF_EXECUTION_UNCERTAIN";
            // Never replace creation evidence, a journal or payload with a failure.
            if(output is not null) try { await Write(output+".blocked.json",new {status="BLOCKED",reason}); } catch { }
            Console.Error.WriteLine("NEW_EF_BLOCKED:"+reason); return 66;
        }
        finally {
            Console.CancelKeyPress-=cancelled;
            Environment.SetEnvironmentVariable("NEW_EF_PROVISIONING_CONNECTION_STRING",null);
            Environment.SetEnvironmentVariable("NEW_EF_MIGRATION_CONNECTION_STRING",null);
        }
    }
    private static string Env(string name)=>Environment.GetEnvironmentVariable(name) is {Length:>0} value?value:throw new LegacyContractException("NEW_EF_RUNTIME_CONTEXT_REQUIRED");
    private static async Task VerifyLocalSources(NewEfPlanV1 p,bool write,CancellationToken token) {
        NewEfContract.Require(Env("GITHUB_RUN_ID")==p.RunId && Env("GITHUB_RUN_ATTEMPT")==p.RunAttempt
            && Env("GITHUB_SHA")==p.SourceRevision && Env("GITHUB_REPOSITORY")==p.SourceRepository
            && Env("NEW_EF_WORKFLOW_SHA")==p.WorkflowRevision && Env("NEW_EF_ACTIONS_SHA")==p.ActionsRevision
            && Env("GITHUB_REF")=="refs/heads/main" && Env("GITHUB_EVENT_NAME")=="workflow_dispatch"
            && Env("GITHUB_JOB")== (write?"write_new_ef_test":"prepare_new_ef_test"),"NEW_EF_RUNTIME_CONTEXT_INVALID");
        var workspace=Env("GITHUB_WORKSPACE");
        foreach(var (folder,sha) in new[] {("application",p.SourceRevision),("workflow",p.WorkflowRevision),("actions",p.ActionsRevision)}) {
            var actual=await Command("git",["-C",Path.Combine(workspace,".new",folder),"rev-parse","HEAD"],token);
            NewEfContract.Require(actual.Trim()==sha,"NEW_EF_SOURCE_REVISION_MISMATCH");
            await Command("git",["-C",Path.Combine(workspace,".new",folder),"diff","--quiet","HEAD","--"],token);
            var extra=await Command("git",["-C",Path.Combine(workspace,".new",folder),"ls-files","--others","--exclude-standard"],token);
            NewEfContract.Require(extra.Length==0,"NEW_EF_SOURCE_WORKTREE_DIRTY");
        }
        var root=Path.Combine(workspace,".new/workflow/database-registry");
        NewEfContract.Require(Hashing.Sha256(await File.ReadAllBytesAsync(Path.Combine(root,"governance-targets.json"),token))==p.GovernanceHash
            && Hashing.Sha256(await File.ReadAllBytesAsync(Path.Combine(root,"onboarding-targets.json"),token))==p.OnboardingHash,"NEW_EF_GOVERNANCE_CHANGED");
    }
    private static async Task<string> Command(string command,string[] arguments,CancellationToken token) {
        var start=new ProcessStartInfo(command) {UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(var a in arguments) start.ArgumentList.Add(a);
        using var process=Process.Start(start)??throw new LegacyContractException("NEW_EF_AUTHORITY_UNAVAILABLE");
        var stdout=process.StandardOutput.ReadToEndAsync(token);var stderr=process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); await stderr; NewEfContract.Require(process.ExitCode==0,"NEW_EF_AUTHORITY_REVALIDATION_FAILED"); return await stdout; }
        catch { if(!process.HasExited) process.Kill(true); throw; }
    }
    private static async Task Write<T>(string path,T value) {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await JsonSerializer.SerializeAsync(stream,value,JsonDefaults.Indented); await stream.FlushAsync();
    }
}
