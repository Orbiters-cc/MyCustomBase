#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// A version change removes only what MCB installed (a version's logic prefab, the armature debug prefab), never another
/// tool's "debug" object, a creator's own "mcb logic" or the avatar root.
/// </summary>
public class MCBInstalledObjectsTests
{
    [Test]
    public void OnlyMcbPrefabSourcesAreInstalledObjects()
    {
        Assert.That(FileManagerService.IsInstalledSource("debug", FileManagerService.DebugPrefabPath, false), Is.True);
        Assert.That(FileManagerService.IsInstalledSource("Armature debug", FileManagerService.DebugPrefabPath, false), Is.True);
        Assert.That(FileManagerService.IsInstalledSource("mcb logic", "Assets/MCB/assets/9/versions/u1.0.0d1.0.0/mcb logic.prefab", false), Is.True);
        Assert.That(FileManagerService.IsInstalledSource("mcb logic", "", true), Is.True, "Its version folder was removed.");

        Assert.That(FileManagerService.IsInstalledSource("debug", "Assets/OtherTool/debug.prefab", false), Is.False);
        Assert.That(FileManagerService.IsInstalledSource("debug", "", true), Is.False);
        Assert.That(FileManagerService.IsInstalledSource("mcb logic", "Assets/Creator/mcb logic.prefab", false), Is.False);
        Assert.That(FileManagerService.IsInstalledSource("Toggles", "Assets/MCB/assets/9/versions/u1.0.0d1.0.0/mcb logic.prefab", false), Is.False);
        Assert.That(FileManagerService.IsInstalledSource("mcb logic", "Assets/MCBX/mcb logic.prefab", false), Is.False);
    }

    [Test]
    public void PlainObjectsNamedLikeMcbObjectsStay()
    {
        var scene = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("debug");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            var toolDebug = new GameObject("Debug").transform; toolDebug.SetParent(root.transform, false);
            var nested = new GameObject("debug").transform; nested.SetParent(toolDebug, false);
            var creatorLogic = new GameObject("mcb logic").transform; creatorLogic.SetParent(root.transform, false);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FileManagerService.DebugPrefabPath);
            Assert.That(prefab, Is.Not.Null, "MCB's debug prefab is missing.");
            var installed = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            installed.transform.SetParent(root.transform, false);
            installed.name = "debug";

            Assert.That(FileManagerService.InstalledObjects(root.transform), Is.EqualTo(new[] { installed }));
            Assert.That(FileManagerService.InstalledObjects(installed.transform), Is.Empty, "The root itself is never removed.");
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(scene);
        }
    }
}
#endif
