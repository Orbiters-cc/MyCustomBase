#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Orbiters.Toolkit.Editor;

/// <summary>
/// A downloaded version is extracted under Assets/, where Unity compiles and runs any script as soon as it is imported.
/// Code from a creator Orbiters has not marked trusted needs the user's consent before anything is written there. The
/// rules are Orbiters Toolkit's (<see cref="ContentTrust"/>, shared with My Avatar's gallery); this matches them to MCB
/// versions and the logic packages MCB imports from them.
/// </summary>
public static class VersionContentTrust
{
    public const string UntrustedCodeMessage = ContentTrust.UntrustedCodeMessage;

    // Ephemeral response from the authenticated model-trust endpoint, never persisted in a version manifest.
    public sealed class CreatorTrustSnapshot
    {
        public int assetId;
        public int versionId;
        public string version;
        public string sourceVersionKey;
        public int? creatorId;
        public string creatorName;
        public bool? creatorTrusted;
    }

    // The snapshot must describe this exact version; then the shared rule decides.
    public static bool IsCreatorTrusted(CustomBaseVersion version, CreatorTrustSnapshot current, int signedInUserId) =>
        version != null && current != null && current.creatorTrusted.HasValue &&
        current.assetId == version.assetId && current.version == version.version &&
        (version.id <= 0 || current.versionId == version.id) &&
        string.Equals(current.sourceVersionKey ?? "", version.sourceVersionKey ?? "", StringComparison.Ordinal) &&
        ContentTrust.IsTrusted(current.creatorTrusted, current.creatorId, signedInUserId);

    public static int SignedInUserId() => ContentTrust.SignedInUserId();

    /// <summary>
    /// Code files the archive would put in the project: its own entries, and what MCB imports from the Unity packages it
    /// carries (listed as "package.unitypackage/Assets/..."). The archive and those packages are read within one
    /// <see cref="VersionArchiveBudget"/>.
    /// </summary>
    public static List<string> ListCode(Stream zipStream, VersionArchiveBudget budget = null) =>
        ContentTrust.ListArchiveCode(zipStream, budget ?? new VersionArchiveBudget(), (path, index) => FileManagerService.LogicPackageImportEntries(path, index));

    /// <summary>
    /// True when the version may be installed: no code (<see cref="ListCode"/>), a trusted creator, or the user chose to
    /// continue.
    /// </summary>
    public static bool ConfirmDownloadedCode(CustomBaseVersion version, AvatarDiscoveredAsset asset, IReadOnlyList<string> code, CreatorTrustSnapshot current = null) =>
        ContentTrust.ConfirmCode(
            $"{(string.IsNullOrWhiteSpace(asset?.name) ? "Custom Base" : asset.name)} {version?.version}",
            !string.IsNullOrWhiteSpace(current?.creatorName) ? current.creatorName : version?.creatorName ?? asset?.ownerUsername,
            code, IsCreatorTrusted(version, current, SignedInUserId()));
}
#endif
