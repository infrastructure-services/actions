using System.Text.Json;
using System.Text.RegularExpressions;
using DatabaseReleaseQualification;

return await QualificationCli.RunAsync(args);

public static class QualificationCli
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || !string.Equals(args[0], "analyze", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("DATABASE_RELEASE_QUALIFICATION_FAILED:INVALID_COMMAND");
                return Task.FromResult(2);
            }

            var options = Parse(args.Skip(1).ToArray());
            var environment = Required(options, "environment").ToUpperInvariant();
            if (!string.Equals(environment, "TEST", StringComparison.Ordinal))
            {
                Console.Error.WriteLine("DATABASE_RELEASE_QUALIFICATION_FAILED:ENVIRONMENT_NOT_ALLOWED");
                return Task.FromResult(3);
            }

            var release = new ReleaseDescriptor
            {
                ReleaseId = SafeReleaseId(options),
                Environment = environment,
                SourceKind = SafeToken(options, "source-kind"),
                Scenario = SafeToken(options, "scenario"),
                DatabaseLifecycle = SafeToken(options, "database-lifecycle")
            };
            var discovery = new DiscoveryGate
            {
                ConsistencyStatus = SafeToken(options, "discovery-status"),
                ConsistencyReason = SafeToken(options, "discovery-reason")
            };

            var forward = ReleaseScript.FromFile("forward", RequiredFile(options, "forward"));
            var rollback = ReleaseScript.FromFile("rollback", RequiredFile(options, "rollback"));
            if (forward.Length == 0 || rollback.Length == 0)
            {
                throw new InvalidOperationException("RELEASE_SCRIPT_EMPTY");
            }

            var schemaPath = RequiredFile(options, "schema");
            var snapshot = JsonSerializer.Deserialize<SchemaSnapshot>(File.ReadAllText(schemaPath), JsonDefaults.Compact)
                ?? throw new InvalidOperationException("SCHEMA_SNAPSHOT_INVALID");

            var analyzer = new SqlScriptAnalyzer();
            var dependencyAnalysis = new DependencyAnalysisReport
            {
                Forward = analyzer.Analyze("forward", forward.Text, snapshot),
                Rollback = analyzer.Analyze("rollback", rollback.Text, snapshot)
            };
            var riskAnalysis = new RiskEngine().Evaluate(dependencyAnalysis, snapshot);
            var pre = SchemaCanonicalizer.Canonicalize(snapshot);
            var dataRelevant = dependencyAnalysis.Forward.Operations.Any(operation => operation.IsDataMutation || operation.HasPotentialDataLoss)
                || dependencyAnalysis.Rollback.Operations.Any(operation => operation.IsDataMutation);
            var qualificationStatus = !discovery.IsConsistent
                ? "BLOCKED_DISCOVERY"
                : riskAnalysis.AutoPromotionBlocked
                    ? "BLOCKED_ANALYSIS_CONFIDENCE"
                    : "ANALYZED_NOT_REHEARSED";
            var result = new RehearsalResult
            {
                QualificationStatus = qualificationStatus,
                SchemaRollbackValidity = SchemaRollbackValidity.NotTested,
                DataRollbackValidity = dataRelevant ? DataRollbackValidity.NotTested : DataRollbackValidity.NotApplicable,
                RollbackCapability = RollbackCapability.Unknown,
                ForwardCertified = false,
                RollbackCertified = false,
                ReapplyCertified = false,
                Pre = pre,
                ExecutionAudit = discovery.IsConsistent
                    ? ["REHEARSAL:NOT_EXECUTED_IN_V1_ACTION"]
                    : [$"DISCOVERY:{discovery.ConsistencyReason}"]
            };

            var package = new ReleasePackageWriter().Write(
                Required(options, "output"),
                SafeSegment(options, "attestation-id"),
                release,
                forward,
                rollback,
                snapshot,
                dependencyAnalysis,
                riskAnalysis,
                result,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["engineMode"] = "ANALYZE_ONLY",
                    ["parser"] = dependencyAnalysis.Forward.Parser
                });

            var cliResult = new
            {
                qualificationStatus = result.QualificationStatus,
                packageDirectory = package.ReleaseDirectory,
                payloadDirectory = package.PayloadDirectory,
                attestationDirectory = package.AttestationDirectory,
                payloadHash = package.PayloadHash,
                forwardHash = forward.Sha256,
                rollbackHash = rollback.Sha256,
                finalRisk = riskAnalysis.FinalRisk,
                requiresDbaApproval = riskAnalysis.RequiresDbaApproval,
                analysisConfidence = riskAnalysis.AnalysisConfidence
            };
            var resultPath = Path.GetFullPath(Required(options, "result"));
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(cliResult, JsonDefaults.Indented) + Environment.NewLine);
            Console.WriteLine("Database Release Qualification package preparado sin ejecutar SQL.");
            return Task.FromResult(!discovery.IsConsistent ? 4 : riskAnalysis.AutoPromotionBlocked ? 5 : 0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"DATABASE_RELEASE_QUALIFICATION_FAILED:{exception.GetType().Name}");
            return Task.FromResult(1);
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("INVALID_ARGUMENTS");
            }
            result.Add(args[index][2..], args[index + 1]);
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key)
    {
        if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"MISSING_{key.ToUpperInvariant()}");
        }
        return value;
    }

    private static string RequiredFile(IReadOnlyDictionary<string, string> options, string key)
    {
        var path = Path.GetFullPath(Required(options, key));
        return File.Exists(path) ? path : throw new FileNotFoundException($"MISSING_{key.ToUpperInvariant()}_FILE");
    }

    private static string SafeReleaseId(IReadOnlyDictionary<string, string> options)
    {
        var value = Required(options, "release-id");
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("INVALID_RELEASE_ID");
        }
        return value;
    }

    private static string SafeSegment(IReadOnlyDictionary<string, string> options, string key)
    {
        var value = Required(options, key);
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException($"INVALID_{key.ToUpperInvariant()}");
        }
        return value;
    }

    private static string SafeToken(IReadOnlyDictionary<string, string> options, string key)
    {
        var value = Required(options, key).ToUpperInvariant();
        if (!Regex.IsMatch(value, @"\A[A-Z0-9_:-]{1,128}\z", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException($"INVALID_{key.ToUpperInvariant()}");
        }
        return value;
    }
}
