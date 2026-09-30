using System.Text;
using System.Diagnostics;
using DatabaseReleaseQualification;

public static class LegacyArtifactTests
{
    public static ILegacyGitTransport Transport(byte[] forward, byte[] rollback) =>
        new FakeGit(forward, rollback);
    public static (string Name, Func<Task> Run)[] Cases = [
        ("legacy manifest acepta forma exacta", ManifestValid),
        ("legacy manifest rechaza propiedades desconocidas y duplicadas", ManifestClosed),
        ("legacy manifest rechaza versión y target equivocados", ManifestVersionAndTarget),
        ("legacy manifest exige metadata DBA", ManifestDba),
        ("legacy paths bloquean traversal y aliases", ManifestPaths),
        ("legacy discovery liga bytes Git exactos", ExactGitBytes),
        ("legacy discovery lee commit y blobs reales sin checkout", RealGitBlobs),
        ("legacy discovery no acepta ref mutable", MutableRef),
        ("legacy artifact BLOCKED conserva schema cerrado", BlockedEvidence),
        ("legacy discovery bloquea LFS y blobs idénticos", IdenticalAndLfs)
    ];

    private static readonly string Commit = new('a', 40);
    private static readonly string Tree = new('b', 40);
    private static readonly string ManifestOid = new('c', 40);
    private static readonly string ForwardOid = new('d', 40);
    private static readonly string RollbackOid = new('e', 40);
    private static readonly LegacyRepositoryV1 Repository = new("github.com", "1234", "team/repo");
    private static readonly LegacyArtifactSelectionV1 Selection =
        new(1, "db/releases/m.json", Commit, Repository);
    private static readonly byte[] ManifestBytes = Encoding.UTF8.GetBytes(
        "{\"contractVersion\":1,\"releaseId\":\"r1\",\"targetId\":\"target1\",\"forwardPath\":\"db/releases/f.sql\",\"rollbackPath\":\"db/releases/r.sql\",\"changeOrigin\":\"APPLICATION\"}");

    private static Task ManifestValid()
    {
        var parsed = LegacyManifest.Parse(ManifestBytes, Selection.ManifestPath, "target1");
        Equal("r1", parsed.ReleaseId);
        Equal("db/releases/f.sql", parsed.ForwardPath);
        return Task.CompletedTask;
    }

    private static Task ManifestClosed()
    {
        Blocks("MANIFEST_UNKNOWN_PROPERTY", () => LegacyManifest.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace(
                "\"changeOrigin\":\"APPLICATION\"", "\"changeOrigin\":\"APPLICATION\",\"extra\":true")),
            Selection.ManifestPath, "target1"));
        Blocks("MANIFEST_DUPLICATE_PROPERTY", () => LegacyManifest.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace(
                "\"releaseId\":\"r1\"", "\"releaseId\":\"r1\",\"releaseId\":\"r2\"")),
            Selection.ManifestPath, "target1"));
        return Task.CompletedTask;
    }

    private static Task ManifestVersionAndTarget()
    {
        Blocks("MANIFEST_VERSION_UNSUPPORTED", () => LegacyManifest.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace(
                "\"contractVersion\":1", "\"contractVersion\":2")), Selection.ManifestPath, "target1"));
        Blocks("TARGET_ID_MISMATCH", () => LegacyManifest.Parse(ManifestBytes, Selection.ManifestPath, "TARGET1"));
        return Task.CompletedTask;
    }

    private static Task ManifestDba()
    {
        Blocks("MANIFEST_MALFORMED", () => LegacyManifest.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace(
                "\"APPLICATION\"", "\"DBA\"")), Selection.ManifestPath, "target1"));
        return Task.CompletedTask;
    }

    private static Task ManifestPaths()
    {
        foreach (var path in new[] { "../x", "a//b", "a\\b", "a/.git/b", "a/CON.txt", "a/b." })
            Blocks(null, () => LegacyPortablePath.Validate(path));
        Blocks("ARTIFACT_PATH_ALIAS", () => LegacyManifest.Parse(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(ManifestBytes).Replace(
                "db/releases/r.sql", "db/releases/F.SQL")), Selection.ManifestPath, "target1"));
        return Task.CompletedTask;
    }

    private static async Task ExactGitBytes()
    {
        var forward = Encoding.UTF8.GetBytes("SELECT 1;\r\n");
        var rollback = Encoding.UTF8.GetBytes("SELECT 2;\n");
        var package = await new LegacyArtifactDiscovery(new FakeGit(forward, rollback))
            .DiscoverAsync(Selection, "target1");
        Equal("VALID", package.Evidence.Status);
        Equal(Hashing.Sha256(forward), package.Evidence.Forward.Sha256);
        Equal(Hashing.Sha256(ManifestBytes), package.Evidence.Manifest.Sha256);
        if (!package.ForwardBytes.SequenceEqual(forward)) throw new Exception("Git bytes changed");
        var copy = package.ForwardBytes;
        copy[0] ^= 0xff;
        package.Verify();
        var changed = package.Evidence with { Commit = new string('f', 40) };
        if (LegacyPackageIdentity.Calculate(changed) == package.Evidence.PackageIdentity)
            throw new Exception("Commit not bound");
    }

    private static async Task MutableRef()
    {
        await BlocksAsync("SOURCE_REVISION_NOT_IMMUTABLE", () =>
            new LegacyArtifactDiscovery(new FakeGit([], [])).DiscoverAsync(
                Selection with { ExpectedCommit = "main" }, "target1"));
    }

    private static async Task RealGitBlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "lpqv1-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, ["init", "--quiet"]);
            var forward = Encoding.UTF8.GetBytes("SELECT 1;\r\n");
            var rollback = Encoding.UTF8.GetBytes("SELECT 2;\n");
            var manifestOid = await Git(root, ["hash-object", "-w", "--stdin"], ManifestBytes);
            var forwardOid = await Git(root, ["hash-object", "-w", "--stdin"], forward);
            var rollbackOid = await Git(root, ["hash-object", "-w", "--stdin"], rollback);
            var innerTree = await Git(root, ["mktree", "-z"],
                Encoding.UTF8.GetBytes($"100644 blob {manifestOid}\tm.json\0" +
                    $"100644 blob {forwardOid}\tf.sql\0" +
                    $"100644 blob {rollbackOid}\tr.sql\0"));
            var dbTree = await Git(root, ["mktree", "-z"],
                Encoding.UTF8.GetBytes($"040000 tree {innerTree}\treleases\0"));
            var rootTree = await Git(root, ["mktree", "-z"],
                Encoding.UTF8.GetBytes($"040000 tree {dbTree}\tdb\0"));
            var commit = await Git(root, ["commit-tree", rootTree, "-m", "fixture"]);
            var artifact = await new LegacyArtifactDiscovery(new ProcessLegacyGitTransport(root))
                .DiscoverAsync(Selection with { ExpectedCommit = commit }, "target1");
            if (!artifact.ForwardBytes.SequenceEqual(forward)
                || !artifact.RollbackBytes.SequenceEqual(rollback)
                || artifact.Evidence.Forward.GitBlobOid != forwardOid
                || artifact.Evidence.Manifest.GitBlobOid != manifestOid)
                throw new Exception("Real Git object bytes changed");
        }
        finally
        {
            var full = Path.GetFullPath(root);
            if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("lpqv1-git-", StringComparison.Ordinal))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(full, true);
            }
        }
    }

    private static async Task<string> Git(string directory, string[] arguments, byte[]? input = null)
    {
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = directory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment["GIT_AUTHOR_NAME"] = "Fixture";
        start.Environment["GIT_AUTHOR_EMAIL"] = "fixture@example.invalid";
        start.Environment["GIT_COMMITTER_NAME"] = "Fixture";
        start.Environment["GIT_COMMITTER_EMAIL"] = "fixture@example.invalid";
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Exception("Git unavailable");
        if (input is not null) await process.StandardInput.BaseStream.WriteAsync(input);
        process.StandardInput.Close();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Fixture Git failed: " + error);
        return output.Trim();
    }

    private static async Task BlockedEvidence()
    {
        var result = await new LegacyArtifactDiscovery(new FakeGit([], []))
            .DiscoverEvidenceAsync(Selection with { ExpectedCommit = "main" }, "target1");
        if (result.Valid is not null || result.Package is not null
            || result.Blocked is not { ContractVersion: 1, Status: "BLOCKED" }
            || !result.Blocked.ReasonCodes.SequenceEqual(["SOURCE_REVISION_NOT_IMMUTABLE"]))
            throw new Exception("Invalid BLOCKED evidence");
    }

    private static async Task IdenticalAndLfs()
    {
        await BlocksAsync("ARTIFACT_IDENTICAL", () =>
            new LegacyArtifactDiscovery(new FakeGit(Encoding.UTF8.GetBytes("SELECT 1"), Encoding.UTF8.GetBytes("SELECT 1")))
                .DiscoverAsync(Selection, "target1"));
        await BlocksAsync("ARTIFACT_LFS_POINTER", () =>
            new LegacyArtifactDiscovery(new FakeGit(
                Encoding.UTF8.GetBytes("version https://git-lfs.github.com/spec/v1\n"),
                Encoding.UTF8.GetBytes("SELECT 2")))
                .DiscoverAsync(Selection, "target1"));
    }

    private static void Blocks(string? expected, Action action)
    {
        try { action(); }
        catch (LegacyContractException exception) when (expected is null || exception.Code == expected) { return; }
        throw new Exception("Expected block " + expected);
    }

    private static async Task BlocksAsync(string expected, Func<Task> action)
    {
        try { await action(); }
        catch (LegacyContractException exception) when (exception.Code == expected) { return; }
        throw new Exception("Expected block " + expected);
    }

    private static void Equal(string expected, string actual)
    {
        if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
    }

    private sealed class FakeGit(byte[] forward, byte[] rollback) : ILegacyGitTransport
    {
        public Task<LegacyGitResult> RunAsync(IReadOnlyList<string> args, int maximumOutputBytes, CancellationToken token)
        {
            var key = string.Join(" ", args);
            byte[] result = key switch
            {
                "rev-parse --show-object-format" => Encoding.ASCII.GetBytes("sha1\n"),
                var value when value == "cat-file -t " + Commit => Encoding.ASCII.GetBytes("commit\n"),
                var value when value == "rev-parse " + Commit + "^{tree}" => Encoding.ASCII.GetBytes(Tree + "\n"),
                var value when value == "ls-tree -z " + Tree => Row("040000", "tree", new string('1', 40), "db"),
                var value when value == "ls-tree -z " + new string('1', 40) =>
                    Row("040000", "tree", new string('2', 40), "releases"),
                var value when value == "ls-tree -z " + new string('2', 40) =>
                    Row("100644", "blob", ManifestOid, "m.json")
                        .Concat(Row("100644", "blob", ForwardOid, "f.sql"))
                        .Concat(Row("100644", "blob", RollbackOid, "r.sql")).ToArray(),
                var value when value == "cat-file -s " + ManifestOid =>
                    Encoding.ASCII.GetBytes(ManifestBytes.Length + "\n"),
                var value when value == "cat-file -s " + ForwardOid =>
                    Encoding.ASCII.GetBytes(forward.Length + "\n"),
                var value when value == "cat-file -s " + RollbackOid =>
                    Encoding.ASCII.GetBytes(rollback.Length + "\n"),
                var value when value == "cat-file blob " + ManifestOid => ManifestBytes,
                var value when value == "cat-file blob " + ForwardOid => forward,
                var value when value == "cat-file blob " + RollbackOid => rollback,
                _ => throw new Exception("Unexpected Git call: " + key)
            };
            if (result.Length > maximumOutputBytes) throw new LegacyContractException("RESOURCE_LIMIT");
            return Task.FromResult(new LegacyGitResult(0, result));
        }

        private static byte[] Row(string mode, string type, string oid, string name) =>
            Encoding.UTF8.GetBytes($"{mode} {type} {oid}\t{name}\0");
    }
}
