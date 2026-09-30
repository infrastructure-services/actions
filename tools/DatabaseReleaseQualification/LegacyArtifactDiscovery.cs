using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyGitResult(int ExitCode, byte[] Output);

public interface ILegacyGitTransport
{
    Task<LegacyGitResult> RunAsync(IReadOnlyList<string> arguments, int maximumOutputBytes,
        CancellationToken cancellationToken);
}

public sealed class ProcessLegacyGitTransport(string repositoryDirectory) : ILegacyGitTransport
{
    public async Task<LegacyGitResult> RunAsync(IReadOnlyList<string> arguments, int maximumOutputBytes,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo("git") {
            WorkingDirectory = repositoryDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new LegacyContractException("TECHNICAL_ERROR");
            var errorDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null, deadline.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token);
                if (read == 0) break;
                if (output.Length + read > maximumOutputBytes)
                    throw new LegacyContractException("RESOURCE_LIMIT");
                output.Write(buffer, 0, read);
            }
            await errorDrain;
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, output.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new LegacyContractException("TIMEOUT"); }
        catch (LegacyContractException) { throw; }
        catch { throw new LegacyContractException("TECHNICAL_ERROR"); }
        finally
        {
            if (started && !process.HasExited)
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
        }
    }
}

public sealed class LegacyArtifactDiscovery(ILegacyGitTransport git)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Sha40 = new(@"\A[0-9a-f]{40}\z");
    private static readonly Regex Sha64 = new(@"\A[0-9a-f]{64}\z");
    private const int SqlMaximum = 8 * 1024 * 1024;
    private static readonly HashSet<string> ArtifactReasons = new(StringComparer.Ordinal) {
        "MANIFEST_MISSING", "MANIFEST_MALFORMED", "MANIFEST_DUPLICATE_PROPERTY",
        "MANIFEST_UNKNOWN_PROPERTY", "MANIFEST_VERSION_UNSUPPORTED", "TARGET_ID_MISMATCH",
        "RELEASE_ID_INVALID", "ARTIFACT_PATH_INVALID", "ARTIFACT_PATH_TRAVERSAL",
        "ARTIFACT_PATH_ALIAS", "ARTIFACT_NOT_REGULAR_BLOB", "ARTIFACT_SUBMODULE",
        "ARTIFACT_LFS_POINTER", "ARTIFACT_BLOB_MISSING", "ARTIFACT_IDENTICAL",
        "SOURCE_REPOSITORY_MISMATCH", "SOURCE_COMMIT_MISMATCH",
        "SOURCE_REVISION_NOT_IMMUTABLE", "SOURCE_AUTHORITY_UNVERIFIED",
        "ARTIFACT_ENCODING_INVALID", "ARTIFACT_HASH_MISMATCH", "ARTIFACT_MUTATED",
        "RESOURCE_LIMIT", "TIMEOUT", "CANCELLED", "TECHNICAL_ERROR"
    };

    public async Task<LegacyArtifactDiscoveryOutcomeV1> DiscoverEvidenceAsync(
        LegacyArtifactSelectionV1 selection, string governedTargetId,
        CancellationToken token = default)
    {
        try
        {
            var package = await DiscoverAsync(selection, governedTargetId, token);
            return new(package.Evidence, null, package);
        }
        catch (LegacyContractException exception)
        {
            var reason = ArtifactReasons.Contains(exception.Code) ? exception.Code : "TECHNICAL_ERROR";
            return new(null, new(1, "BLOCKED", [reason]), null);
        }
        catch
        {
            return new(null, new(1, "BLOCKED", ["TECHNICAL_ERROR"]), null);
        }
    }

    public async Task<LegacyFrozenPackage> DiscoverAsync(
        LegacyArtifactSelectionV1 selection, string governedTargetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        var token = deadline.Token;
        try
        {
            if (selection.ContractVersion != 1) throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
            selection.ExpectedRepository.Validate();
            LegacyPortablePath.Validate(selection.ManifestPath);
            var format = Text(await Run(["rev-parse", "--show-object-format"], 128, token));
            if (format is not ("sha1" or "sha256"))
                throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
            var validOid = format == "sha1" ? Sha40 : Sha64;
            if (!validOid.IsMatch(selection.ExpectedCommit))
                throw new LegacyContractException("SOURCE_REVISION_NOT_IMMUTABLE");
            if (Text(await Run(["cat-file", "-t", selection.ExpectedCommit], 128, token)) != "commit")
                throw new LegacyContractException("SOURCE_COMMIT_MISMATCH");
            var tree = Text(await Run(["rev-parse", selection.ExpectedCommit + "^{tree}"], 128, token));
            if (!validOid.IsMatch(tree)) throw new LegacyContractException("SOURCE_COMMIT_MISMATCH");

            (string Oid, string Mode) manifestBlob;
            try { manifestBlob = await ResolveBlob(tree, selection.ManifestPath, validOid, token); }
            catch (LegacyContractException exception) when (exception.Code == "ARTIFACT_BLOB_MISSING")
            { throw new LegacyContractException("MANIFEST_MISSING"); }
            var manifestBytes = await ReadBlob(manifestBlob.Oid, 16384, token);
            var parsed = LegacyManifest.Parse(manifestBytes, selection.ManifestPath, governedTargetId);
            var forwardBlob = await ResolveBlob(tree, parsed.ForwardPath, validOid, token);
            var rollbackBlob = await ResolveBlob(tree, parsed.RollbackPath, validOid, token);
            var forwardBytes = await ReadBlob(forwardBlob.Oid, SqlMaximum, token);
            var rollbackBytes = await ReadBlob(rollbackBlob.Oid, SqlMaximum, token);
            if (forwardBlob.Oid == rollbackBlob.Oid
                || Hashing.Sha256(forwardBytes) == Hashing.Sha256(rollbackBytes))
                throw new LegacyContractException("ARTIFACT_IDENTICAL");
            CheckSqlEncoding(forwardBytes);
            CheckSqlEncoding(rollbackBytes);

            var evidence = new LegacyArtifactValidV1(
                1, "VALID", selection.ExpectedRepository, format, selection.ExpectedCommit,
                parsed.TargetId, parsed.ReleaseId,
                Blob(selection.ManifestPath, manifestBlob.Oid, manifestBytes),
                Blob(parsed.ForwardPath, forwardBlob.Oid, forwardBytes),
                Blob(parsed.RollbackPath, rollbackBlob.Oid, rollbackBytes), "", []);
            evidence = evidence with { PackageIdentity = LegacyPackageIdentity.Calculate(evidence) };
            var package = new LegacyFrozenPackage(evidence, parsed, manifestBytes, forwardBytes, rollbackBytes);
            package.Verify();
            return package;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new LegacyContractException("CANCELLED"); }
        catch (OperationCanceledException)
        { throw new LegacyContractException("TIMEOUT"); }
    }

    private static LegacyBlobEvidenceV1 Blob(string path, string oid, byte[] bytes) =>
        new(path, oid, bytes.Length, Hashing.Sha256(bytes));

    private static void CheckSqlEncoding(byte[] bytes)
    {
        if (bytes.Length == 0) throw new LegacyContractException("ARTIFACT_ENCODING_INVALID");
        var content = bytes.AsSpan();
        if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
        try
        {
            if (Utf8.GetString(content).Contains('\0'))
                throw new LegacyContractException("ARTIFACT_ENCODING_INVALID");
        }
        catch (DecoderFallbackException)
        { throw new LegacyContractException("ARTIFACT_ENCODING_INVALID"); }
    }

    private async Task<(string Oid, string Mode)> ResolveBlob(
        string rootTree, string path, Regex validOid, CancellationToken token)
    {
        var currentTree = rootTree;
        var components = path.Split('/');
        foreach (var (component, index) in components.Select((value, index) => (value, index)))
        {
            var rows = await Run(["ls-tree", "-z", currentTree], 32 * 1024 * 1024, token);
            var siblings = DecodeTree(rows, validOid);
            if (siblings.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
                throw new LegacyContractException("ARTIFACT_PATH_ALIAS");
            var match = siblings.SingleOrDefault(x => x.Name == component);
            if (match is null)
                throw new LegacyContractException(siblings.Any(x => x.Name.Equals(component, StringComparison.OrdinalIgnoreCase))
                    ? "ARTIFACT_PATH_ALIAS" : "ARTIFACT_BLOB_MISSING");
            if (index < components.Length - 1)
            {
                if (match.Mode != "040000" || match.Type != "tree")
                    throw new LegacyContractException("ARTIFACT_NOT_REGULAR_BLOB");
                currentTree = match.Oid;
                continue;
            }
            if (match.Mode == "160000") throw new LegacyContractException("ARTIFACT_SUBMODULE");
            if (match.Mode is not ("100644" or "100755") || match.Type != "blob")
                throw new LegacyContractException("ARTIFACT_NOT_REGULAR_BLOB");
            return (match.Oid, match.Mode);
        }
        throw new LegacyContractException("ARTIFACT_PATH_INVALID");
    }

    private sealed record TreeEntry(string Mode, string Type, string Oid, string Name);

    private static List<TreeEntry> DecodeTree(byte[] rows, Regex oidPattern)
    {
        var result = new List<TreeEntry>();
        var start = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i] != 0) continue;
            var row = rows.AsSpan(start, i - start);
            start = i + 1;
            var tab = row.IndexOf((byte)'\t');
            if (tab < 0) throw new LegacyContractException("TECHNICAL_ERROR");
            string header, name;
            try
            {
                header = Utf8.GetString(row[..tab]);
                name = Utf8.GetString(row[(tab + 1)..]);
            }
            catch (DecoderFallbackException) { throw new LegacyContractException("TECHNICAL_ERROR"); }
            var parts = header.Split(' ');
            if (parts.Length != 3 || !oidPattern.IsMatch(parts[2]))
                throw new LegacyContractException("TECHNICAL_ERROR");
            result.Add(new(parts[0], parts[1], parts[2], name));
        }
        if (start != rows.Length) throw new LegacyContractException("TECHNICAL_ERROR");
        return result;
    }

    private async Task<byte[]> ReadBlob(string oid, int maximum, CancellationToken token)
    {
        var sizeText = Text(await Run(["cat-file", "-s", oid], 128, token));
        if (!int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            || size is < 1 || size > maximum)
            throw new LegacyContractException("RESOURCE_LIMIT");
        var bytes = await Run(["cat-file", "blob", oid], size + 1, token);
        if (bytes.Length != size) throw new LegacyContractException("ARTIFACT_HASH_MISMATCH");
        if (bytes.AsSpan().StartsWith(Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/v1")))
            throw new LegacyContractException("ARTIFACT_LFS_POINTER");
        return bytes;
    }

    private async Task<byte[]> Run(string[] args, int limit, CancellationToken token)
    {
        var result = await git.RunAsync(args, limit, token);
        if (result.ExitCode != 0)
            throw new LegacyContractException(args[0] == "cat-file"
                ? "ARTIFACT_BLOB_MISSING" : "TECHNICAL_ERROR");
        return result.Output;
    }

    private static string Text(byte[] bytes)
    {
        try { return Utf8.GetString(bytes).TrimEnd('\n', '\r'); }
        catch (DecoderFallbackException) { throw new LegacyContractException("TECHNICAL_ERROR"); }
    }
}
