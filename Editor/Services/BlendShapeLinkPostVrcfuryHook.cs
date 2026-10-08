#if UNITY_EDITOR
using Orbiters.Toolkit.Editor.VRChat.BlendShapes;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

public class BlendShapeLinkPostVrcfuryHook : IVRCSDKPreprocessAvatarCallback
{
    // VRCFury uses -10000; this runs after generated controllers are assigned, before the mode locks (-8970) and before
    // Orbiters Toolkit links refitted blendshapes (-8960), which then also copy the curves these correctives add.
    public int callbackOrder => -9000;

    public bool OnPreprocessAvatar(GameObject avatarRoot)
    {
        BlendShapeLinkEngine.BeginBuild();
        var versionResult = BlendShapeLinkService.Instance.ApplyActiveVersionFactorLinks(avatarRoot);
        if (versionResult.Success)
        {
            MCBLogger.Log("[MCB] " + versionResult.Message);
        }
        else
        {
            MCBLogger.Log("[MCB] Version BlendShape links skipped: " + versionResult.Message);
        }

        var manualResult = BlendShapeLinkService.Instance.ApplyConfiguredFactorLinks(avatarRoot);
        if (manualResult.Success)
        {
            MCBLogger.Log("[MCB] " + manualResult.Message);
        }
        else
        {
            MCBLogger.Log("[MCB] Manual BlendShape links skipped: " + manualResult.Message);
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
