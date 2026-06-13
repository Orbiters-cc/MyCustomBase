#if UNITY_EDITOR
using System;

public static class ModelFileTransforms
{
    public const string XorBinToFbx = "XOR_BIN_TO_FBX";
    public const string HdiffXorBinToFbx = "HDIFF_XOR_BIN_TO_FBX";
    public const string XorBinToUnityAsset = NativeMeshPayloadService.TransformName;
    public const string DirectAsset = "DIRECT_ASSET";

    public static bool IsXorFbxReplacementTransform(string transform)
    {
        return string.Equals(transform, XorBinToFbx, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHdiffFbxReplacementTransform(string transform)
    {
        return string.Equals(transform, HdiffXorBinToFbx, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFbxReplacementTransform(string transform)
    {
        return IsXorFbxReplacementTransform(transform) ||
               IsHdiffFbxReplacementTransform(transform);
    }

    public static bool AffectsFbxPath(string transform)
    {
        return IsFbxReplacementTransform(transform) ||
               NativeMeshPayloadService.IsAdvancedMeshPatchTransform(transform) ||
               string.Equals(transform, DirectAsset, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
