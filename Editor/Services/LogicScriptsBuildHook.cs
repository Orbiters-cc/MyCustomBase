#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase.Editor.BuildPipeline;

// A version's logic can carry a script this project does not have (a tool only its creator has). Unity refuses to save
// the upload copy of an avatar that holds one ("You are trying to save a Prefab with a missing script"), so no upload
// goes through and the error names no cause. Such scripts are left out of the logic on that copy; the scene is untouched.
public sealed class LogicScriptsBuildHook : IVRCSDKPreprocessAvatarCallback
{
    // With MCB's first step: before any tool reads or copies the avatar.
    public int callbackOrder => -10200;

    public bool OnPreprocessAvatar(GameObject avatarRoot)
    {
        if (avatarRoot.GetComponentInChildren<MyCustomBase>(true) == null) return true;
        var dropped = DropUnknownScripts(avatarRoot.transform);
        if (dropped.Count > 0)
            MCBLogger.LogWarning($"[MCB] Left {dropped.Count} script(s) this project does not have out of the uploaded avatar: " +
                                 string.Join(", ", dropped.Take(10)) + (dropped.Count > 10 ? ", ..." : "") +
                                 ". They come with the installed version's logic, from a tool its creator has; your scene is unchanged.");
        return true;
    }

    /// <summary>
    /// Removes the components whose script cannot be loaded from every logic object under <paramref name="avatarRoot"/>.
    /// Returns the path of each object that held one, once per component.
    /// </summary>
    internal static List<string> DropUnknownScripts(Transform avatarRoot)
    {
        var dropped = new List<string>();
        var logics = avatarRoot.GetComponentsInChildren<Transform>(true)
            .Where(t => t != avatarRoot && t.name == MCBLogicPrefabService.LogicRootName);
        foreach (var part in logics.SelectMany(logic => logic.GetComponentsInChildren<Transform>(true)).Distinct())
        {
            int count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(part.gameObject);
            if (count > 0) dropped.AddRange(Enumerable.Repeat(AnimationUtility.CalculateTransformPath(part, avatarRoot), count));
        }
        return dropped;
    }
}
#endif
