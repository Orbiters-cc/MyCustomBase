#if UNITY_EDITOR
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

public class BlendShapeLinkPostVrcfuryHook : IVRCSDKPreprocessAvatarCallback
{
    // VRCFury uses -10000; this runs after generated controllers are assigned.
    public int callbackOrder => -9000;

    public bool OnPreprocessAvatar(GameObject avatarRoot)
    {
        BlendShapeLinkService.Instance.ClearSessionTracking();
        var versionResult = BlendShapeLinkService.Instance.ApplyActiveVersionFactorLinks(avatarRoot);
        if (versionResult.success)
        {
            MCBLogger.Log("[MCB] " + versionResult.message);
        }
        else
        {
            MCBLogger.Log("[MCB] Version BlendShape links skipped: " + versionResult.message);
        }

        var manualResult = BlendShapeLinkService.Instance.ApplyConfiguredFactorLinks(avatarRoot);
        if (manualResult.success)
        {
            MCBLogger.Log("[MCB] " + manualResult.message);
        }
        else
        {
            MCBLogger.Log("[MCB] Manual BlendShape links skipped: " + manualResult.message);
        }

        var animationOffsetResult = AnimationPositionOffsetService.Instance.ApplyActiveVersionOffsets(avatarRoot);
        if (animationOffsetResult.success)
        {
            MCBLogger.Log("[MCB] " + animationOffsetResult.message);
        }
        else
        {
            MCBLogger.Log("[MCB] Bone animation offset fix skipped: " + animationOffsetResult.message);
        }

        return true;
    }
}
#endif
