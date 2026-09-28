#if UNITY_EDITOR
using Orbiters.Toolkit.Editor;
using UnityEditor;

/// <summary>MCB's experimental features, stored and listed by the Toolkit's <see cref="OrbitersFeatures"/>.</summary>
public static class FeatureFlags
{
    public const string Product = "MCB";
    public const string SUPPORT_USER_UNKNOWN_VERSION = "SUPPORT_USER_UNKNOWN_VERSION";
    public const string ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR = "ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR";
    public const string ALLOW_ADVANCED_MESH_ON_BLENDER_LINK = "ALLOW_ADVANCED_MESH_ON_BLENDER_LINK";

    [InitializeOnLoadMethod]
    private static void Register()
    {
        Add(SUPPORT_USER_UNKNOWN_VERSION, "Support custom (unknown-hash) base",
            "Detect and manage user-custom avatar bases when the current FBX hash is unknown but a .fbx.originalbase exists.");
        Add(ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR, "Allow advanced replacement for creator",
            "Build submitted custom base model patches as encrypted native Unity mesh payloads instead of replacement FBX bytes.");
        Add(ALLOW_ADVANCED_MESH_ON_BLENDER_LINK, "Allow advanced mesh on Blender link",
            "Advertise native mesh transfer support to the Blender connector. Requires advanced replacement for creator.",
            ALLOW_ADVANCED_REPLACEMENT_FOR_CREATOR);
    }

    private static void Add(string key, string label, string description, string requires = null) =>
        OrbitersFeatures.Register(new OrbitersFeature
        {
            Key = key, Product = Product, Label = label, Description = description,
            PrefKey = "MCB_FeatureFlag_" + key, Stage = FeatureStage.Experimental, Requires = requires
        });

    public static bool IsEnabled(string key) => OrbitersFeatures.IsEnabled(key);
    public static void SetEnabled(string key, bool enabled) => OrbitersFeatures.SetEnabled(key, enabled);
}
#endif
