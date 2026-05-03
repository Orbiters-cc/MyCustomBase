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
    [JsonProperty] public Dictionary<string, object> metadata;
}

// Represents a single available version of an custom base modification.
[JsonObject(MemberSerialization.OptIn)]
#if UNITY_EDITOR
public class CustomBaseVersion : IEquatable<CustomBaseVersion>
#else
public class CustomBaseVersion
#endif
{
    [JsonProperty] public string version;
    [JsonProperty] public string defaultAviVersion;
    [JsonProperty] public Scope scope;
    [JsonProperty] public string date;
    [JsonProperty] public string changelog;
    [JsonProperty] public string customAviHash;
    [JsonProperty] public string appliedCustomAviHash;
    [JsonProperty] public string[] defaultAviHash;
    [JsonProperty] public CustomBlendshapeEntry[] customBlendshapes;
    [JsonProperty] public string[] extraCustomization;
    [JsonProperty] public Dictionary<string, string> dependencies;
    [JsonProperty] public ModelFileData[] sourceFiles;
    [JsonProperty] public ModelFileData[] versionFiles;
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int uploaderId;
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

    public bool Equals(CustomBaseVersion other)
    {
        if (other == null) return false;
        return assetId == other.assetId && version == other.version && defaultAviVersion == other.defaultAviVersion;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(assetId, version, defaultAviVersion);
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
