using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlDiscovery.V2;

namespace DatabaseReleaseQualification;

// Separate creation-first transport. No generic SQL/admin input, DROP, EF update,
// retry, credential fallback or implicit catalog correction exists here.
public sealed class SqlNewEfRuntime(string inspectionMaster, string provisioningMaster,
    string migrationConnection, Func<CancellationToken, Task> verifyAuthority,
    Func<CancellationToken, Task> verifyClassification) : INewEfRuntime
{
    private int creationStarted;
    public static SqlConnectionStringBuilder Options(string raw, NewEfPlanV1 p, string catalog)
    {
        NewEfContract.Require(p.Environment=="TEST" && p.TargetId==NewEfContract.TargetId
            && p.ApplicationId=="3602" && p.DatabaseName==NewEfContract.Database
            && p.ServerInstance=="sqlv1testdcsrv1", "NEW_EF_TARGET_INVALID");
        var b = new SqlConnectionStringBuilder(raw);
        NewEfContract.Require(b.InitialCatalog == catalog && b.DataSource == p.ConnectionDataSource
            && b.AttachDBFilename.Length == 0 && !b.UserInstance, "NEW_EF_CONNECTION_BINDING_MISMATCH");
        new SqlTlsPolicy("TEST", SqlTlsPolicy.Parse(p.TlsMode)).Apply(b);
        b.ConnectTimeout = 15; b.ConnectRetryCount = 0; b.Pooling = false;
        b.Enlist = false; b.MultipleActiveResultSets = false;
        b.ApplicationName = "new-ef-creation-first-v1";
        return b;
    }
    public static async Task VerifyServerAbsenceAsync(SqlConnection c, NewEfPlanV1 p,
        bool creationAuthority, CancellationToken token)
    {
        await using var q = c.CreateCommand();
        q.CommandTimeout = 30;
        q.CommandText = """
            SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME(), ORIGINAL_LOGIN(),
                CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DATABASE')=1 THEN 1 ELSE 0 END,
                CASE WHEN EXISTS(SELECT 1 FROM sys.databases WHERE name=@name) THEN 1 ELSE 0 END,
                CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'CREATE ANY DATABASE')=1
                     OR HAS_PERMS_BY_NAME(NULL,NULL,'ALTER ANY DATABASE')=1
                     OR HAS_PERMS_BY_NAME(NULL,NULL,'CONTROL SERVER')=1 THEN 1 ELSE 0 END,
                CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DEFINITION')=1 THEN 1 ELSE 0 END,
                CASE WHEN EXISTS(SELECT 1 FROM sys.server_triggers WHERE is_disabled=0) THEN 1 ELSE 0 END;
            """;
        q.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = p.DatabaseName });
        await using var r = await q.ExecuteReaderAsync(token);
        NewEfContract.Require(await r.ReadAsync(token) && r.FieldCount == 8
            && Enumerable.Range(0,8).All(i => !r.IsDBNull(i)), "NEW_EF_ABSENCE_EVIDENCE_INCOMPLETE");
        NewEfCreationGuard.Verify(p,new(r.GetString(0),r.GetString(1),r.GetString(2),
            r.GetInt32(3)==1,r.GetInt32(4)!=0,r.GetInt32(5)==1,r.GetInt32(6)==1,r.GetInt32(7)!=0),creationAuthority);
        NewEfContract.Require(!await r.ReadAsync(token) && !await r.NextResultAsync(token), "NEW_EF_ABSENCE_EVIDENCE_INCOMPLETE");
    }
    public async Task<NewEfCreationReceipt> CreateAsync(NewEfPlanV1 p, string authorization, CancellationToken token)
    {
        NewEfContract.Require(Interlocked.Exchange(ref creationStarted,1)==0, "NEW_EF_CREATE_RETRY_FORBIDDEN");
        NewEfContract.Require(!string.IsNullOrWhiteSpace(authorization), "NEW_EF_AUTHORIZATION_REQUIRED");
        await verifyAuthority(token);
        await using var c = new SqlConnection(Options(provisioningMaster,p,"master").ConnectionString);
        await c.OpenAsync(token);
        // Same verified administrative connection performs exactly one creation.
        await VerifyServerAbsenceAsync(c,p,true,token);
        await using var create = c.CreateCommand(); create.CommandTimeout = 60;
        create.CommandText = "CREATE DATABASE [CICD_NEW_EF_TEST];";
        await create.ExecuteNonQueryAsync(token); // FIRST SQL WRITE; never retried.
        await using var q = c.CreateCommand(); q.CommandTimeout = 30;
        q.CommandText = """
            SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), ORIGINAL_LOGIN(),
                CONVERT(nvarchar(36),r.database_guid), d.create_date
            FROM sys.databases d JOIN sys.database_recovery_status r ON d.database_id=r.database_id
            WHERE d.name=@name;
            """;
        q.Parameters.AddWithValue("@name",p.DatabaseName);
        await using var row = await q.ExecuteReaderAsync(token);
        NewEfContract.Require(await row.ReadAsync(token) && Enumerable.Range(0,4).All(i=>!row.IsDBNull(i))
            && row.GetString(0).Equals(p.ServerInstance,StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(row.GetString(2),out _), "NEW_EF_CREATION_RESULT_UNCERTAIN");
        var receipt = new NewEfCreationReceipt(p.TargetId,p.ApplicationId,"TEST",p.ServerInstance,p.DatabaseName,
            p.OperationId,authorization,row.GetString(1),DateTimeOffset.UtcNow,row.GetString(2),
            p.GovernanceRevision,p.SourceRevision,p.WorkflowRevision,p.ActionsRevision,p.PlanHash,"DATABASE_CREATED","");
        NewEfContract.Require(!await row.ReadAsync(token),"NEW_EF_CREATION_RESULT_UNCERTAIN");
        return receipt with { ReceiptHash = NewEfContract.Hash(receipt) };
    }
    public Task VerifyClassificationAsync(NewEfPlanV1 p, CancellationToken token) => verifyClassification(token);

    private static SchemaSnapshot Partition(SchemaSnapshot s, bool history) => new() {
        FormatVersion=s.FormatVersion, UnsupportedSchemaFeatures=s.UnsupportedSchemaFeatures.ToList(),
        Objects=s.Objects.Where(o => ((o.Schema=="dbo" && o.Name=="__EFMigrationsHistory" && o.Kind=="table")
            || (o.Schema=="dbo" && o.Parent=="__EFMigrationsHistory")) == history).ToList()
    };
    public static RecoverySecurityScope SecurityScope(SchemaSnapshot snapshot,string name) {
        var items=new List<RecoverySecuritySecurable> { new("DATABASE","",""),new("SCHEMA","dbo","") };
        if(snapshot.Objects.Any(o=>o.Kind=="table" && o.Schema=="dbo" && o.Name==name))
            items.Add(new("OBJECT","dbo",name));
        return new(items,[]);
    }
    public async Task<NewEfObservation> CaptureAsync(NewEfPlanV1 p, CancellationToken token)
    {
        var mode=SqlTlsPolicy.Parse(p.TlsMode);
        var targetOptions=new SqlConnectionStringBuilder(inspectionMaster) { InitialCatalog=p.DatabaseName };
        // Only the read-only inspection adapter derives the target connection.
        var raw=Options(targetOptions.ConnectionString,p,p.DatabaseName).ConnectionString;
        var discovery=await new SqlDiscoveryOrchestratorV2(new SqlClientDiscoveryTransportV2(environmentName:"TEST",tlsMode:mode))
            .DiscoverAsync(new SqlDiscoveryTarget(inspectionMaster,p.DatabaseName),token);
        var reader=new SqlServerSchemaReader();
        var first=await reader.CaptureWithMetadataAsync(raw,"TEST",mode,token);
        var second=await reader.CaptureWithMetadataAsync(raw,"TEST",mode,token);
        var canonical=SchemaCanonicalizer.Canonicalize(first.Snapshot);
        NewEfContract.Require(first.ServerInstance.Equals(p.ServerInstance,StringComparison.OrdinalIgnoreCase)
            && first.DatabaseName==p.DatabaseName && second.ServerInstance==first.ServerInstance
            && second.DatabaseName==first.DatabaseName
            && canonical.Sha256==SchemaCanonicalizer.Canonicalize(second.Snapshot).Sha256,"NEW_EF_CAPTURE_NONDETERMINISTIC");
        await using var c=new SqlConnection(raw); await c.OpenAsync(token);
        await using var q=c.CreateCommand(); q.CommandTimeout=30;
        q.CommandText="SELECT CONVERT(nvarchar(36),database_guid) FROM sys.database_recovery_status WHERE database_id=DB_ID();";
        var incarnation=Convert.ToString(await q.ExecuteScalarAsync(token));
        NewEfContract.Require(Guid.TryParse(incarnation,out _),"NEW_EF_INCARNATION_UNAVAILABLE");
        bool dataEmpty=true, product=true;
        if(first.Snapshot.Objects.Any(o=>o.Kind=="table" && o.Schema=="dbo" && o.Name=="MigrationTestItems")) {
            q.CommandText="SELECT COUNT_BIG(*) FROM dbo.MigrationTestItems;";
            dataEmpty=Convert.ToInt64(await q.ExecuteScalarAsync(token))==0;
        }
        if(discovery.History.Status==HistoryStatus.Present) {
            q.CommandText="SELECT COUNT_BIG(*) FROM dbo.__EFMigrationsHistory WHERE ProductVersion<>N'10.0.12';";
            product=Convert.ToInt64(await q.ExecuteScalarAsync(token))==0;
        }
        var provider=new ReadOnlyRecoverySecurityProvider(new SqlRecoverySecurityCatalogReader(
            new SecurityTransport(raw,p),new SecurityTargetBindingV1(p.DatabaseName,"ALLOW_LIST",[p.ServerInstance])));
        async Task<string> Security(string name) {
            // The unchanged catalog reader deliberately refuses to infer an
            // absent object. Its absence is proven by complete schema capture
            // and discovery; always capture DB/schema security and add object
            // security only when the object was actually observed.
            var scope=SecurityScope(first.Snapshot,name);
            var e=await provider.CaptureSecurityAsync(scope,RecoveryPhase.Pre,token);
            var result=RecoverySecurityCanonicalizer.Canonicalize(scope,RecoveryPhase.Pre,e);
            NewEfContract.Require(result is not null,"NEW_EF_SECURITY_COVERAGE_REQUIRED");
            return NewEfContract.Hash(new {present=scope.Securables.Any(s=>s.Kind=="OBJECT"),securityHash=result!.Sha256});
        }
        return new(discovery,first.Snapshot,canonical.Sha256,
            SchemaCanonicalizer.Canonicalize(Partition(first.Snapshot,false)).Sha256,
            SchemaCanonicalizer.Canonicalize(Partition(first.Snapshot,true)).Sha256,
            await Security("MigrationTestItems"),await Security("__EFMigrationsHistory"),incarnation!,dataEmpty,product);
    }
    public async Task ExecuteAsync(NewEfPlanV1 p, byte[] bytes, string phase, NewEfObservation expected,CancellationToken token)
    {
        await verifyAuthority(token);
        var now=await CaptureAsync(p,token);
        NewEfContract.Require(now.SchemaHash==expected.SchemaHash && now.DatabaseIncarnation==expected.DatabaseIncarnation
            && now.BusinessSecurityHash==expected.BusinessSecurityHash && now.HistorySecurityHash==expected.HistorySecurityHash
            && now.Discovery.History.Status==expected.Discovery.History.Status
            && now.Discovery.History.MigrationIds.SequenceEqual(expected.Discovery.History.MigrationIds)
            && now.DataEmpty && now.HistoryProductVersionValid,"NEW_EF_DRIFT_BEFORE_WRITE");
        var batches=NewEfSql.Validate(bytes,phase);
        await verifyAuthority(token);
        await using var c=new SqlConnection(Options(migrationConnection,p,p.DatabaseName).ConnectionString);
        await c.OpenAsync(token);
        await using var identity=c.CreateCommand(); identity.CommandTimeout=15;
        identity.CommandText="""
            SELECT CONVERT(nvarchar(128),SERVERPROPERTY('ServerName')), DB_NAME(),
              CONVERT(nvarchar(36),r.database_guid),
              CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CONTROL')=1 THEN 1 ELSE 0 END,
              CASE WHEN EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=0 AND is_disabled=0) THEN 1 ELSE 0 END,
              CASE WHEN HAS_PERMS_BY_NAME(NULL,NULL,'VIEW ANY DEFINITION')=1 THEN 1 ELSE 0 END,
              CASE WHEN EXISTS(SELECT 1 FROM sys.server_triggers WHERE is_disabled=0) THEN 1 ELSE 0 END
            FROM sys.database_recovery_status r WHERE database_id=DB_ID();
            """;
        await using(var r=await identity.ExecuteReaderAsync(token)) {
            NewEfContract.Require(await r.ReadAsync(token) && r.FieldCount==7 && Enumerable.Range(0,7).All(i=>!r.IsDBNull(i))
                && r.GetString(0).Equals(p.ServerInstance,StringComparison.OrdinalIgnoreCase) && r.GetString(1)==p.DatabaseName
                && r.GetString(2)==expected.DatabaseIncarnation && r.GetInt32(3)==1 && r.GetInt32(4)==0
                && r.GetInt32(5)==1 && r.GetInt32(6)==0,
                "NEW_EF_MUTATION_IDENTITY_OR_AUTHORITY_INVALID");
            NewEfContract.Require(!await r.ReadAsync(token) && !await r.NextResultAsync(token),"NEW_EF_MUTATION_IDENTITY_OR_AUTHORITY_INVALID");
        }
        await using var transaction=(SqlTransaction)await c.BeginTransactionAsync(token);
        foreach(var batch in batches) {
            token.ThrowIfCancellationRequested();
            await using var command=c.CreateCommand(); command.Transaction=transaction;
            command.CommandTimeout=60; command.CommandText=batch;
            await command.ExecuteNonQueryAsync(token);
        }
        token.ThrowIfCancellationRequested(); await transaction.CommitAsync(token);
        // Disposal rolls back uncommitted work. A lost commit response stops the
        // cycle; neither phase retry nor automatic DOWN/DROP is permitted.
    }
    private sealed class SecurityTransport(string raw,NewEfPlanV1 p) : ISecurityCatalogTransport {
        public async Task<ISecurityCatalogSession> OpenAsync(CancellationToken token) {
            var c=new SqlConnection(Options(raw,p,p.DatabaseName).ConnectionString);
            try { await c.OpenAsync(token); return new SecuritySession(c); }
            catch { await c.DisposeAsync(); throw; }
        }
    }
    private sealed class SecuritySession(SqlConnection c) : ISecurityCatalogSession {
        public ValueTask DisposeAsync()=>c.DisposeAsync();
        public async Task<IReadOnlyList<IReadOnlyDictionary<string,object?>>> QueryAsync(string sql,CancellationToken token) {
            SecuritySqlGuard.Validate(sql);
            await using var q=c.CreateCommand(); q.CommandTimeout=30; q.CommandText=sql;
            await using var r=await q.ExecuteReaderAsync(token);
            var rows=new List<IReadOnlyDictionary<string,object?>>();
            while(await r.ReadAsync(token)) {
                NewEfContract.Require(rows.Count<10000,"NEW_EF_SECURITY_EVIDENCE_LIMIT");
                var row=new Dictionary<string,object?>();
                for(int i=0;i<r.FieldCount;i++) row.Add(r.GetName(i),r.IsDBNull(i)?null:r.GetValue(i));
                rows.Add(row);
            }
            return rows;
        }
    }
}
