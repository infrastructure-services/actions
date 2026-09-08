using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DatabaseReleaseQualification;

return await QualificationCli.RunAsync(args);

public static class QualificationCli
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("DATABASE_RELEASE_QUALIFICATION_FAILED:INVALID_COMMAND");
            return Task.FromResult(2);
        }

        return args[0].ToLowerInvariant() switch
        {
            "analyze" => AnalyzeAsync(args.Skip(1).ToArray()),
            "capture-schema" => CaptureSchemaAsync(args.Skip(1).ToArray()),
            "compare-schema-captures" => CompareSchemaCapturesAsync(args.Skip(1).ToArray()),
            "evaluate-database-state" => EvaluateDatabaseStateAsync(args.Skip(1).ToArray()),
            _ => InvalidCommand()
        };
    }

    private static Task<int> InvalidCommand()
    {
        Console.Error.WriteLine("DATABASE_RELEASE_QUALIFICATION_FAILED:INVALID_COMMAND");
        return Task.FromResult(2);
    }

    private static Task<int> AnalyzeAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
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

    private static async Task<int> CaptureSchemaAsync(string[] args)
    {
        string? resultPath = null;
        try
        {
            var options = Parse(args);
            RequireTestEnvironment(options);
            resultPath = Path.GetFullPath(Required(options, "result"));
            var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new SchemaCaptureException(SchemaCaptureErrorClassifier.MissingConnection());
            }

            var source = await new SqlServerSchemaReader().CaptureWithMetadataAsync(connectionString);
            var artifact = new SchemaCaptureArtifactWriter().WriteCapture(
                Path.GetFullPath(Required(options, "output")),
                SafeSegment(options, "capture-id"),
                source);
            WriteJsonResult(resultPath, new
            {
                status = SchemaCaptureStatuses.Success,
                diagnosticCode = "SCHEMA_CAPTURE_COMPLETE",
                artifact.CaptureDirectory,
                artifact.SchemaHash,
                artifact.Metadata.DatabaseName,
                artifact.Metadata.ServerVersion,
                artifact.Metadata.ServerMajorVersion,
                artifact.Metadata.SchemaCoverage,
                artifact.Metadata.MetricsAvailability,
                artifact.Metadata.MetricsDiagnosticCode,
                artifact.Metadata.ObjectCounts,
                artifact.Metadata.UnsupportedSchemaFeatures
            });
            Console.WriteLine("Schema capture read-only completado.");
            return 0;
        }
        catch (SchemaCaptureException exception)
        {
            WriteFailureResult(resultPath, exception.Failure.Status, exception.Failure.DiagnosticCode);
            Console.Error.WriteLine($"SCHEMA_CAPTURE_FAILED:{exception.Failure.Status}:{exception.Failure.DiagnosticCode}");
            return exception.Failure.ExitCode;
        }
        catch (Exception exception)
        {
            var diagnostic = SchemaCaptureErrorClassifier.SafeDiagnostic(exception);
            WriteFailureResult(resultPath, SchemaCaptureStatuses.CaptureFailed, diagnostic);
            Console.Error.WriteLine($"SCHEMA_CAPTURE_FAILED:{SchemaCaptureStatuses.CaptureFailed}:{diagnostic}");
            return 6;
        }
    }

    private static Task<int> CompareSchemaCapturesAsync(string[] args)
    {
        string? resultPath = null;
        try
        {
            var options = Parse(args);
            RequireTestEnvironment(options);
            resultPath = Path.GetFullPath(Required(options, "result"));
            var first = SchemaCaptureArtifactWriter.ReadArtifact(RequiredDirectory(options, "capture-1"));
            var second = SchemaCaptureArtifactWriter.ReadArtifact(RequiredDirectory(options, "capture-2"));
            var comparison = new SchemaCaptureArtifactWriter().CompareAndWrite(
                first, second, Path.GetFullPath(Required(options, "output")));
            WriteJsonResult(resultPath, new
            {
                comparison.Status,
                comparison.DiagnosticCode,
                comparison.Deterministic,
                comparison.Capture1SchemaHash,
                comparison.Capture2SchemaHash
            });
            Console.WriteLine($"Schema capture determinism: {comparison.Deterministic.ToString().ToLowerInvariant()}.");
            return Task.FromResult(comparison.Deterministic ? 0 : 7);
        }
        catch (Exception exception)
        {
            var diagnostic = SchemaCaptureErrorClassifier.SafeDiagnostic(exception);
            WriteFailureResult(resultPath, SchemaCaptureStatuses.CaptureFailed, diagnostic);
            Console.Error.WriteLine($"SCHEMA_CAPTURE_FAILED:{SchemaCaptureStatuses.CaptureFailed}:{diagnostic}");
            return Task.FromResult(6);
        }
    }

    private static Task<int> EvaluateDatabaseStateAsync(string[] args)
    {
        string? resultPath = null;
        try
        {
            var options = Parse(args);
            RequireTestEnvironment(options);
            resultPath = Path.GetFullPath(Required(options, "result"));
            var capture = SchemaCaptureArtifactWriter.ReadArtifact(RequiredDirectory(options, "capture"));
            if (!DateTimeOffset.TryParse(
                    Required(options, "capture-timestamp-utc"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var captureTimestampUtc))
            {
                throw new ArgumentException("INVALID_CAPTURE_TIMESTAMP_UTC");
            }

            var observation = new DatabaseStateObservation
            {
                ApplicationId = SafeSegment(options, "application-id"),
                Environment = Required(options, "environment").ToUpperInvariant(),
                DatabaseName = capture.Metadata.DatabaseName,
                ObservedSchemaHash = capture.SchemaHash,
                SchemaCoverage = capture.Metadata.SchemaCoverage.ToString().ToUpperInvariant(),
                UnsupportedSchemaFeatures = capture.Metadata.UnsupportedSchemaFeatures,
                CaptureTimestampUtc = captureTimestampUtc,
                RunId = SafeSegment(options, "run-id"),
                RunAttempt = SafeSegment(options, "run-attempt")
            };
            var provenance = new RegistryProvenance
            {
                RegistryRepository = SafeRegistryRepository(options),
                RegistryRef = SafeRegistryRef(options),
                RegistryCommitSha = SafeCommitSha(options),
                RegistryFilePath = SafeRegistryFilePath(options),
                RegistryFileSha256 = SafeSha256(options, "registry-file-sha256")
            };
            var registry = DatabaseRegistryLoader.Load(
                Path.GetFullPath(Required(options, "registry")), provenance);
            var evaluation = new DatabaseStateEvaluator().Evaluate(registry, observation);
            var artifact = new DatabaseStateArtifactWriter().Write(
                Path.GetFullPath(Required(options, "output")), observation, evaluation);

            WriteJsonResult(resultPath, new
            {
                status = evaluation.DriftStatus == DatabaseDriftStatuses.InvalidRegistry
                    ? "FAIL_INVALID_REGISTRY"
                    : "SUCCESS",
                evaluation.ObservedSchemaHash,
                evaluation.CertifiedSchemaHash,
                evaluation.RegistryStatus,
                evaluation.DriftStatus,
                evaluation.GateStatus,
                evaluation.Reason,
                evaluation.RegistryFormatVersion,
                evaluation.RegistryProvenance,
                evaluation.BaselineCandidate,
                evaluation.DriftDetected,
                evaluation.DriftEvidenceKind,
                evaluation.StructuralDiffAvailable,
                artifact.TargetPath,
                artifact.RegistryEvaluationPath,
                artifact.BaselineCandidatePath,
                artifact.DriftAnalysisPath
            });
            Console.WriteLine($"Database state evaluation completada: gate={evaluation.GateStatus}.");
            return Task.FromResult(evaluation.DriftStatus == DatabaseDriftStatuses.InvalidRegistry ? 8 : 0);
        }
        catch (Exception exception)
        {
            var diagnostic = exception.GetType().Name;
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                try
                {
                    WriteJsonResult(resultPath, new
                    {
                        status = "FAIL_DATABASE_STATE_EVALUATION",
                        diagnosticCode = diagnostic
                    });
                }
                catch
                {
                    // The primary sanitized failure remains authoritative.
                }
            }
            Console.Error.WriteLine($"DATABASE_STATE_EVALUATION_FAILED:{diagnostic}");
            return Task.FromResult(8);
        }
    }

    private static void RequireTestEnvironment(IReadOnlyDictionary<string, string> options)
    {
        if (!string.Equals(Required(options, "environment"), "TEST", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ENVIRONMENT_NOT_ALLOWED");
        }
    }

    private static void WriteFailureResult(string? path, string status, string diagnosticCode)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            WriteJsonResult(path, new { status, diagnosticCode });
        }
        catch
        {
            // The primary sanitized failure remains authoritative.
        }
    }

    private static void WriteJsonResult<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonDefaults.Indented) + Environment.NewLine);
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

    private static string RequiredDirectory(IReadOnlyDictionary<string, string> options, string key)
    {
        var path = Path.GetFullPath(Required(options, key));
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"MISSING_{key.ToUpperInvariant()}_DIRECTORY");
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

    private static string SafeRegistryRepository(IReadOnlyDictionary<string, string> options)
    {
        var value = Required(options, "registry-repository");
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("INVALID_REGISTRY_REPOSITORY");
        return value;
    }

    private static string SafeRegistryRef(IReadOnlyDictionary<string, string> options)
    {
        var value = Required(options, "registry-ref");
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,255}\z", RegexOptions.CultureInvariant)
            || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("INVALID_REGISTRY_REF");
        return value;
    }

    private static string SafeCommitSha(IReadOnlyDictionary<string, string> options)
    {
        var value = Required(options, "registry-commit-sha");
        if (!Regex.IsMatch(value, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("INVALID_REGISTRY_COMMIT_SHA");
        return value.ToLowerInvariant();
    }

    private static string SafeRegistryFilePath(IReadOnlyDictionary<string, string> options)
    {
        var value = Required(options, "registry-file-path");
        if (!Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z", RegexOptions.CultureInvariant)
            || value.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("INVALID_REGISTRY_FILE_PATH");
        return value;
    }

    private static string SafeSha256(IReadOnlyDictionary<string, string> options, string key)
    {
        var value = Required(options, key);
        if (!Regex.IsMatch(value, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant))
            throw new ArgumentException($"INVALID_{key.ToUpperInvariant()}");
        return value.ToLowerInvariant();
    }
}
