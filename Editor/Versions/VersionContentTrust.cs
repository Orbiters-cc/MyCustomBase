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
    /// carries (listed as "package.unitypackage/Assets/...").
    /// </summary>
    public static List<string> ListCode(Stream zipStream)
    {
        var code = new List<string>();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, true))
        {
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
                        source.CopyTo(file);
                    code.AddRange(FileManagerService.LogicPackageImportEntries(UnityPackageIndex.Read(temp))
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
