#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Orbiters.Toolkit.Editor;

/// <summary>
/// A downloaded version is extracted under Assets/, where Unity compiles and runs any script as soon as it is imported.
/// Code from a creator Orbiters has not marked trusted needs the user's consent before anything is written there.
/// </summary>
public static class VersionContentTrust
{
    public const string UntrustedCodeMessage =
        "Orbiters does not control the content of the files and scripts of this asset or its author. Unity compiles and runs code as soon as it is imported.";

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

    public static bool IsCreatorTrusted(CustomBaseVersion version, CreatorTrustSnapshot current, int signedInUserId) =>
        version != null && current != null && current.creatorTrusted.HasValue &&
        current.assetId == version.assetId && current.version == version.version &&
        (version.id <= 0 || current.versionId == version.id) &&
        string.Equals(current.sourceVersionKey ?? "", version.sourceVersionKey ?? "", StringComparison.Ordinal) &&
        (current.creatorTrusted.Value || (signedInUserId > 0 && current.creatorId == signedInUserId));

    public static int SignedInUserId()
    {
        var auth = AuthenticationService.GetAuth();
        return auth != null && int.TryParse(auth.user, out int id) ? id : 0;
    }

    /// <summary>
    /// Code files the archive would put in the project: its own entries, and what MCB imports from the Unity packages it
    /// carries (listed as "package.unitypackage/Assets/..."). The archive and those packages are read within one
    /// <see cref="VersionArchiveBudget"/>: a package is copied out only as far as the budget allows, and scanned within what
    /// is left of it.
    /// </summary>
    public static List<string> ListCode(Stream zipStream, VersionArchiveBudget budget = null)
    {
        budget = budget ?? new VersionArchiveBudget();
        var code = new List<string>();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
        {
            budget.AddEntries(archive.Entries.Count);
            foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
            {
                string path = entry.FullName.Replace('\\', '/');
                if (CodeContent.IsCode(path)) code.Add(path);
                if (!path.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) continue;

                string temp = Path.Combine(Path.GetTempPath(), "mcb-code-check-" + Guid.NewGuid().ToString("N") + ".unitypackage");
                try
                {
                    using (var source = entry.Open())
                    using (var file = File.Create(temp))
                        budget.Copy(source, file, path);
                    code.AddRange(FileManagerService.LogicPackageImportEntries(temp, ReadNestedPackage(temp, path, budget))
                        .Where(e => CodeContent.IsCode(e.Path))
                        .Select(e => path + "/" + e.Path));
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
        }

        return code.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // A nested package's expansion is charged to the archive's budget before it is indexed within the rest of it.
    private static UnityPackageIndex ReadNestedPackage(string packagePath, string name, VersionArchiveBudget budget)
    {
        long before = budget.TotalBytes;
        using (var gzip = new GZipStream(File.OpenRead(packagePath), CompressionMode.Decompress))
            budget.Copy(gzip, Stream.Null, name);
        var index = UnityPackageIndex.Read(packagePath, maxExpandedBytes: Math.Max(1024L, budget.TotalBytes - before),
            maxEntries: Math.Max(1, budget.RemainingEntries));
        budget.AddEntries(index.Entries.Count);
        return index;
    }

    /// <summary>
    /// True when the version may be installed: no code (<see cref="ListCode"/>), a trusted creator, or the user chose to
    /// continue.
    /// </summary>
    public static bool ConfirmDownloadedCode(CustomBaseVersion version, AvatarDiscoveredAsset asset, IReadOnlyList<string> code, CreatorTrustSnapshot current = null)
    {
        if (code == null || code.Count == 0 || IsCreatorTrusted(version, current, SignedInUserId())) return true;
        return UntrustedCodeDialog.Confirm(new UntrustedCodeDialog.Request
        {
            Subject = $"{(string.IsNullOrWhiteSpace(asset?.name) ? "Custom Base" : asset.name)} {version.version}",
            Author = !string.IsNullOrWhiteSpace(current?.creatorName) ? current.creatorName : version.creatorName ?? asset?.ownerUsername,
            Message = UntrustedCodeMessage,
            Files = code,
            ConfirmLabel = "Install anyway"
        });
    }
}
#endif
