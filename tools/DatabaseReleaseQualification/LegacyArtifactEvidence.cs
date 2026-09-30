using System.Text;
using System.Text.RegularExpressions;

namespace DatabaseReleaseQualification;

public sealed record LegacyRepositoryV1(string Host, string RepositoryId, string FullName)
{
    public void Validate()
    {
        if (Host != "github.com"
            || !Regex.IsMatch(RepositoryId, @"\A[1-9][0-9]{0,19}\z")
            || FullName.Length > 257
            || !Regex.IsMatch(FullName, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z"))
            throw new LegacyContractException("SOURCE_AUTHORITY_UNVERIFIED");
    }
}

public sealed record LegacyArtifactSelectionV1(
    int ContractVersion, string ManifestPath, string ExpectedCommit, LegacyRepositoryV1 ExpectedRepository);

public sealed record LegacyBlobEvidenceV1(string Path, string GitBlobOid, int ByteLength, string Sha256);

public sealed record LegacyArtifactValidV1(
    int ContractVersion, string Status, LegacyRepositoryV1 Repository,
    string GitObjectFormat, string Commit, string TargetId, string ReleaseId,
    LegacyBlobEvidenceV1 Manifest, LegacyBlobEvidenceV1 Forward,
    LegacyBlobEvidenceV1 Rollback, string PackageIdentity, IReadOnlyList<string> ReasonCodes);

public sealed record LegacyArtifactBlockedV1(int ContractVersion, string Status, IReadOnlyList<string> ReasonCodes);
public sealed record LegacyArtifactDiscoveryOutcomeV1(
    LegacyArtifactValidV1? Valid, LegacyArtifactBlockedV1? Blocked, LegacyFrozenPackage? Package);

public sealed class LegacyFrozenPackage
{
    private readonly byte[] manifest;
    private readonly byte[] forward;
    private readonly byte[] rollback;
    public LegacyArtifactValidV1 Evidence { get; }
    public LegacyManifestV1 ParsedManifest { get; }
    public byte[] ManifestBytes => manifest.ToArray();
    public byte[] ForwardBytes => forward.ToArray();
    public byte[] RollbackBytes => rollback.ToArray();

    internal LegacyFrozenPackage(LegacyArtifactValidV1 evidence, LegacyManifestV1 parsed,
        byte[] manifest, byte[] forward, byte[] rollback)
    {
        Evidence = evidence;
        ParsedManifest = parsed;
        this.manifest = manifest.ToArray();
        this.forward = forward.ToArray();
        this.rollback = rollback.ToArray();
    }

    public void Verify()
    {
        if (Hashing.Sha256(manifest) != Evidence.Manifest.Sha256
            || Hashing.Sha256(forward) != Evidence.Forward.Sha256
            || Hashing.Sha256(rollback) != Evidence.Rollback.Sha256
            || manifest.Length != Evidence.Manifest.ByteLength
            || forward.Length != Evidence.Forward.ByteLength
            || rollback.Length != Evidence.Rollback.ByteLength
            || LegacyPackageIdentity.Calculate(Evidence) != Evidence.PackageIdentity)
            throw new LegacyContractException("ARTIFACT_MUTATED");
    }
}

public static class LegacyPackageIdentity
{
    public static string Calculate(LegacyArtifactValidV1 artifact)
    {
        var fields = new[] {
            "LEGACY_PACKAGE_QUALIFICATION_V1",
            artifact.Repository.Host, artifact.Repository.RepositoryId, artifact.Repository.FullName,
            artifact.GitObjectFormat, artifact.Commit, artifact.Manifest.Path, artifact.Manifest.Sha256,
            artifact.TargetId, artifact.ReleaseId, artifact.Forward.Path, artifact.Forward.Sha256,
            artifact.Rollback.Path, artifact.Rollback.Sha256
        };
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            var value = Encoding.UTF8.GetBytes(field);
            var length = Encoding.ASCII.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
            stream.Write(length);
            stream.Write(value);
        }
        return "lpqv1:" + Hashing.Sha256(stream.ToArray());
    }
}
