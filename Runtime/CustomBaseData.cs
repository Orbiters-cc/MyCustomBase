using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

// Represents a custom blendshape with a name and default value.
[JsonObject(MemberSerialization.OptIn)]
public class CorrectiveBlendshapeEntry
{
    [JsonProperty] public CorrectiveActivationType toFixType = CorrectiveActivationType.Blendshape;
    [JsonProperty] public string toFix;
    [JsonProperty] public CorrectiveActivationType fixedByType = CorrectiveActivationType.Blendshape;
    [JsonProperty] public string fixedBy;
}

[JsonObject(MemberSerialization.OptIn)]
public class CustomBlendshapeEntry
{
    [JsonProperty] public string name;
    [JsonProperty] public string defaultValue;
    [JsonProperty] public bool isSlider;
    [JsonProperty] public bool isSliderDefault;
    [JsonProperty("correctives", NullValueHandling = NullValueHandling.Ignore)] public CorrectiveBlendshapeEntry[] correctiveBlendshapes;
}

[JsonConverter(typeof(StringEnumConverter))]
public enum CorrectiveActivationType
{
    [EnumMember(Value = "Blendshape")] Blendshape,
    [EnumMember(Value = "Animation")] Animation
}

// This response object maps to the JSON from the server's version endpoint.

[JsonObject(MemberSerialization.OptIn)]
public class CustomBaseVersionResponse
{
#if UNITY_EDITOR
    [JsonProperty] public string recommendedVersion;
    [JsonProperty] public List<CustomBaseVersion> versions;
#endif
}

[JsonObject(MemberSerialization.OptIn)]
public class ModelFileData
{
    [JsonProperty] public int id;
    [JsonProperty] public string path;
    [JsonProperty] public string hash;
    [JsonProperty] public string type;
    [JsonProperty] public string role;
    [JsonProperty] public string transform;
    [JsonProperty] public string compression;
    [JsonProperty] public string outputHash;
    [JsonProperty] public int? customBaseAssetId;
    [JsonProperty] public int? avatarVersionId;
    [JsonProperty] public int? sourceModelFileId;
    [JsonProperty] public int? storageFileId;
    [JsonProperty] public List<Dictionary<string, string>> metas;
    [JsonProperty] public List<ModelFileSmrPathData> smrPaths;
    [JsonProperty] public Dictionary<string, object> metadata;
}

[JsonObject(MemberSerialization.OptIn)]
public class ModelFileSmrPathData
{
    [JsonProperty] public string avatarPath;
    [JsonProperty] public string fbxMeshPath;
    [JsonProperty] public string meshName;
    [JsonProperty] public string rendererName;
}

[JsonObject(MemberSerialization.OptIn)]
public class VersionProtection
{
    [JsonProperty] public bool xor = true;
    [JsonProperty] public bool discordRole;
    // Server-provided for the signed-in member when the version verifies a Discord role.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? discordRoleGranted;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string[] discordRoles;

    public static bool IsPlain(CustomBaseVersion version) => version?.protection != null && !version.protection.xor;
    public static bool IsDiscordRoleDenied(CustomBaseVersion version) => version?.protection?.discordRoleGranted == false;
}

[JsonObject(MemberSerialization.OptIn)]
public class OriginalBaseVersionData
{
    [JsonProperty] public string key;
    [JsonProperty] public string label;
    [JsonProperty] public ModelFileData[] sourceFiles;
    [JsonProperty] public ModelFileData[] versionFiles;
    [JsonProperty] public MCBDeliveryVariant[] deliveryVariants;
    [JsonProperty] public int meshDelivery;
}

// Represents a single available version of an custom base modification.
[JsonObject(MemberSerialization.OptIn)]
#if UNITY_EDITOR
public class CustomBaseVersion : IEquatable<CustomBaseVersion>, Orbiters.Toolkit.Versions.IVersionRecord
#else
public class CustomBaseVersion
#endif
{
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int id;
    [JsonProperty] public string version;
    [JsonProperty] public string title;
    [JsonProperty] public string defaultAviVersion;
    [JsonProperty] public Scope scope;
    [JsonProperty] public string date;
    [JsonProperty] public string changelog;
    [JsonProperty] public string customAviHash;
    [JsonProperty] public string appliedCustomAviHash;
    [JsonProperty] public string[] defaultAviHash;
    [JsonProperty] public CustomBlendshapeEntry[] customBlendshapes;
    [JsonProperty] public object[] extraCustomization;
    [JsonProperty] public Dictionary<string, string> dependencies;
    [JsonProperty] public ModelFileData[] sourceFiles;
    [JsonProperty] public ModelFileData[] versionFiles;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public MCBDeliveryVariant[] deliveryVariants;
    [JsonProperty] public int meshDelivery;
    [JsonProperty] public OriginalBaseVersionData[] originalBaseVersions;
    [JsonProperty] public string sourceVersionKey;
    // Unprotected versions ship one plain package for every original; Discord-role verification gates its download.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public VersionProtection protection;
    // Bone names an unprotected version binds to on the user's avatar (skeleton compatibility).
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string[] skeleton;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int uploaderId;
    // Server-provided display metadata. Code installation verifies current creator trust separately.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? creatorTrusted;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string creatorName;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string parentVersion;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int assetId;
    
    // --- Fields for local unsubmitted versions ---
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string baseFbxHash;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string customFbxPath;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string customBaseAvatarPath;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string logicPrefabPath;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? includeCustomVeins;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string customVeinsTexturePath;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? includeDynamicNormalsBody;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public bool? includeDynamicNormalsFlexing;

    [JsonIgnore] public bool isUnsubmitted; // Runtime flag, not saved to JSON
    [JsonIgnore] public bool isImported; // Runtime flag for offline imported versions, not saved to JSON
    // A local artifact can contain payloads for several originals. Its on-disk identity
    // stays fixed while the selected view uses another source version's files.
    [JsonIgnore] public string localArtifactSourceVersionKey;

#if UNITY_EDITOR
    // What the shared version timeline shows (Orbiters Toolkit), whatever else an MCB version carries.
    int Orbiters.Toolkit.Versions.IVersionRecord.Id => id;
    string Orbiters.Toolkit.Versions.IVersionRecord.Version => version;
    string Orbiters.Toolkit.Versions.IVersionRecord.Title => title;
    string Orbiters.Toolkit.Versions.IVersionRecord.Scope => scope.ToString().ToLowerInvariant();
    string Orbiters.Toolkit.Versions.IVersionRecord.Date => date;
    string Orbiters.Toolkit.Versions.IVersionRecord.Changelog => changelog;
    int Orbiters.Toolkit.Versions.IVersionRecord.UploaderId => uploaderId;
    string Orbiters.Toolkit.Versions.IVersionRecord.CreatorName => creatorName;
    bool? Orbiters.Toolkit.Versions.IVersionRecord.CreatorTrusted => creatorTrusted;
#endif

    public bool Equals(CustomBaseVersion other)
    {
        if (other == null) return false;
        return assetId == other.assetId && version == other.version && defaultAviVersion == other.defaultAviVersion && sourceVersionKey == other.sourceVersionKey;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(assetId, version, defaultAviVersion, sourceVersionKey);
    }

    public override bool Equals(object obj)
    {
        return Equals(obj as CustomBaseVersion);
    }
}

// Defines the release scope of a version (e.g., public, beta).
[JsonConverter(typeof(StringEnumConverter))]
public enum Scope
{
#if UNITY_EDITOR
    [EnumMember(Value = "public")] PUBLIC,
    [EnumMember(Value = "beta")] BETA,
    [EnumMember(Value = "alpha")] ALPHA,
    [EnumMember(Value = "unknown")] UNKNOWN
#endif
}
