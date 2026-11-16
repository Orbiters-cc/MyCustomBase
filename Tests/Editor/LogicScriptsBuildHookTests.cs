using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class LogicScriptsBuildHookTests
{
    const string Folder = "Assets/__McbLogicScriptsTest";

    // A logic prefab whose root and one child each hold a component of a script no project has.
    const string Prefab = @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &100
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 400}
  - component: {fileID: 11400}
  m_Layer: 0
  m_Name: mcb logic
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &400
Transform:
  m_GameObject: {fileID: 100}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children:
  - {fileID: 401}
  m_Father: {fileID: 0}
--- !u!114 &11400
MonoBehaviour:
  m_GameObject: {fileID: 100}
  m_Enabled: 1
  m_Script: {fileID: 11500000, guid: 0f0e0d0c0b0a09080706050403020100, type: 3}
--- !u!1 &101
GameObject:
  m_ObjectHideFlags: 0
  serializedVersion: 6
  m_Component:
  - component: {fileID: 401}
  - component: {fileID: 11401}
  m_Layer: 0
  m_Name: Part
  m_TagString: Untagged
  m_IsActive: 1
--- !u!4 &401
Transform:
  m_GameObject: {fileID: 101}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Children: []
  m_Father: {fileID: 400}
--- !u!114 &11401
MonoBehaviour:
  m_GameObject: {fileID: 101}
  m_Enabled: 1
  m_Script: {fileID: 11500000, guid: 0f0e0d0c0b0a09080706050403020100, type: 3}
";

    GameObject avatar, copy;

    [TearDown]
    public void Clean()
    {
        if (avatar != null) Object.DestroyImmediate(avatar);
        if (copy != null) Object.DestroyImmediate(copy);
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        AssetDatabase.DeleteAsset(Folder);
    }

    static int Unknown(GameObject root) =>
        root.GetComponentsInChildren<Transform>(true).Sum(t => GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));

    [Test]
    public void TheUploadCopyLosesTheLogicsUnknownScriptsAndCanBeSavedWhileTheSceneKeepsThem()
    {
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
        File.WriteAllText(Folder + "/mcb logic.prefab", Prefab);
        AssetDatabase.ImportAsset(Folder + "/mcb logic.prefab", ImportAssetOptions.ForceSynchronousImport);
        var logic = AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/mcb logic.prefab");
        Assert.NotNull(logic);

        avatar = new GameObject("Avatar");
        PrefabUtility.InstantiatePrefab(logic, avatar.transform);
        Assert.AreEqual(2, Unknown(avatar));

        copy = Object.Instantiate(avatar);
        // What stops an upload today: Unity does not save an avatar holding such a script.
        // Unity logs one error for each object that holds one.
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
        PrefabUtility.SaveAsPrefabAsset(copy, Folder + "/before.prefab", out bool savedBefore);
        UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
        Assert.IsFalse(savedBefore);

        // Not through the build step itself: its warning would be sent to Orbiters as a report from this test run.
        CollectionAssert.AreEqual(new[] { "mcb logic", "mcb logic/Part" }, LogicScriptsBuildHook.DropUnknownScripts(copy.transform));

        Assert.AreEqual(0, Unknown(copy));
        PrefabUtility.SaveAsPrefabAsset(copy, Folder + "/after.prefab", out bool savedAfter);
        Assert.IsTrue(savedAfter);
        Assert.AreEqual(2, Unknown(avatar), "the avatar in the scene is not touched");
    }

    [Test]
    public void AnAvatarWithoutMcbIsLeftAlone()
    {
        AssetDatabase.CreateFolder("Assets", Path.GetFileName(Folder));
        File.WriteAllText(Folder + "/mcb logic.prefab", Prefab);
        AssetDatabase.ImportAsset(Folder + "/mcb logic.prefab", ImportAssetOptions.ForceSynchronousImport);
        avatar = new GameObject("Avatar");
        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "/mcb logic.prefab"), avatar.transform);
        copy = Object.Instantiate(avatar);

        Assert.IsTrue(new LogicScriptsBuildHook().OnPreprocessAvatar(copy));

        Assert.AreEqual(2, Unknown(copy));
    }
}
