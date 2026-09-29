#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class BlenderSyncSessionTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Service = typeof(BlenderSyncService);
    private Scene scene;
    private string folder;
    private string previousStatus;

    [SetUp]
    public void SetUp()
    {
        scene = EditorSceneManager.NewPreviewScene();
        folder = Path.Combine(Path.GetTempPath(), "MCB_BlenderSyncSession_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        previousStatus = (string)Service.GetField("lastStatus", Private).GetValue(null);
    }

    [TearDown]
    public void TearDown()
    {
        EditorSceneManager.ClosePreviewScene(scene);
        Service.GetField("lastStatus", Private).SetValue(null, previousStatus);
        Directory.Delete(folder, true);
    }

    private MyCustomBase CreateAvatar(string name)
    {
        var root = new GameObject(name);
        SceneManager.MoveGameObjectToScene(root, scene);
        return root.AddComponent<MyCustomBase>();
    }

    private static object NewSession(params (string field, object value)[] fields)
    {
        var type = Service.GetNestedType("ActiveSession", BindingFlags.NonPublic);
        var session = Activator.CreateInstance(type, true);
        foreach (var (field, value) in fields) type.GetField(field).SetValue(session, value);
        return session;
    }

    private static object Invoke(string method, params object[] args)
    {
        try { return Service.GetMethod(method, Private).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static string Status => (string)Service.GetField("lastStatus", Private).GetValue(null);

    [Test]
    public void SessionNeverAttachesToAnotherAvatarWithTheSameName()
    {
        CreateAvatar("Shared Avatar");
        var session = NewSession(
            ("customBaseName", "Shared Avatar"),
            ("customBaseGlobalId", $"GlobalObjectId_V1-2-{Guid.NewGuid():N}-4242-0"));

        Invoke("ResolveCustomBase", session);

        Assert.IsNull(session.GetType().GetField("customBase").GetValue(session));
    }

    [Test]
    public void ExportForAvatarInAClosedSceneWaitsWithAnActionableStatus()
    {
        string exportDir = Directory.CreateDirectory(Path.Combine(folder, "export_1")).FullName;
        File.WriteAllText(Path.Combine(exportDir, "ready.json"), "{}");
        var session = NewSession(("customBaseName", "Closed Avatar"), ("inboxPath", folder));

        Invoke("ReportExportWaitingForScene", session);

        StringAssert.Contains("in another scene", Status);
        Assert.IsTrue(File.Exists(Path.Combine(exportDir, "ready.json")));
    }

    [Test]
    public void FailedMultiModelExportChangesNothingAndRetriesOnceItsFilesChange()
    {
        string target = Path.Combine(folder, "Target.fbx");
        File.WriteAllText(target, "original");
        var targetInfo = Activator.CreateInstance(Service.GetNestedType("TargetFbxInfo", BindingFlags.NonPublic), true);
        targetInfo.GetType().GetField("unityPath").SetValue(targetInfo, target);
        var session = NewSession(("sessionId", "session"), ("token", "token"), ("inboxPath", folder),
            ("customBase", CreateAvatar("Avatar")), ("customBaseName", "Avatar"));
        ((IList)session.GetType().GetField("targetFbxFiles").GetValue(session)).Add(targetInfo);

        string exportDir = Directory.CreateDirectory(Path.Combine(folder, "export_1")).FullName;
        File.WriteAllText(Path.Combine(exportDir, "01.fbx"), "model 1");
        File.WriteAllText(Path.Combine(exportDir, "manifest.json"),
            "{\"kind\":\"orbiters.mcb.blenderExport\",\"protocolVersion\":1,\"sessionId\":\"session\",\"token\":\"token\"," +
            "\"models\":[{\"role\":\"CUSTOM_BASE\",\"path\":\"01.fbx\"},{\"role\":\"CUSTOM_BASE\",\"path\":\"02.fbx\"}]}");
        string ready = Path.Combine(exportDir, "ready.json");
        File.WriteAllText(ready, "{\"kind\":\"orbiters.mcb.blenderExportReady\",\"protocolVersion\":1,\"sessionId\":\"session\",\"token\":\"token\",\"manifestPath\":\"manifest.json\"}");

        var failure = Assert.Throws<FileNotFoundException>(() => Invoke("ProcessReadyFile", session, ready));
        StringAssert.EndsWith("02.fbx", failure.FileName);
        Assert.AreEqual("original", File.ReadAllText(target));
        Assert.IsFalse(File.Exists(target + FileManagerService.OriginalSuffix));

        Invoke("MarkReadyFileFailed", ready, failure);
        Assert.IsTrue(File.Exists(ready));
        Assert.IsTrue((bool)Invoke("IsFailedExportUnchanged", ready));

        File.WriteAllText(Path.Combine(exportDir, "02.fbx"), "model 2");
        Assert.IsFalse((bool)Invoke("IsFailedExportUnchanged", ready));
    }

    [Test]
    public void ExportRollbackRestoresOverwrittenFilesAndRemovesCreatedOnes()
    {
        string existing = Path.Combine(folder, "Existing.fbx");
        string created = Path.Combine(folder, "Created.fbx");
        File.WriteAllText(existing, "before");
        File.WriteAllText(existing + ".meta", "meta before");
        var type = Service.GetNestedType("ExportFileRollback", BindingFlags.NonPublic);
        var rollback = Activator.CreateInstance(type, true);
        string backups = (string)type.GetField("folder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rollback);
        using ((IDisposable)rollback)
        {
            type.GetMethod("Track").Invoke(rollback, new object[] { existing });
            type.GetMethod("Track").Invoke(rollback, new object[] { created });
            File.WriteAllText(existing, "after");
            File.WriteAllText(existing + ".meta", "meta after");
            File.WriteAllText(created, "new");
            type.GetMethod("Restore").Invoke(rollback, null);
        }

        Assert.AreEqual("before", File.ReadAllText(existing));
        Assert.AreEqual("meta before", File.ReadAllText(existing + ".meta"));
        Assert.IsFalse(File.Exists(created));
        Assert.IsFalse(Directory.Exists(backups));
    }
    [Test]
    public void FailedExportRestorationRetainsItsRecoveryCopy()
    {
        string file = Path.Combine(folder, "Locked.fbx");
        File.WriteAllText(file, "before");
        var type = Service.GetNestedType("ExportFileRollback", BindingFlags.NonPublic);
        var rollback = Activator.CreateInstance(type, true);
        string backups = (string)type.GetField("folder", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rollback);
        type.GetMethod("Track").Invoke(rollback, new object[] { file });
        File.WriteAllText(file, "after");
        bool wasLogging = MCBLogger.IsEnabled();
        try
        {
            // A directory at the file target deterministically prevents File.Copy on all test platforms.
            File.Delete(file);
            Directory.CreateDirectory(file);
            MCBLogger.SetEnabled(false);
            type.GetMethod("Restore").Invoke(rollback, null);
            ((IDisposable)rollback).Dispose();
            Assert.IsTrue(Directory.Exists(backups));
            Assert.AreEqual("before", File.ReadAllText(Path.Combine(backups, "0")));
        }
        finally
        {
            MCBLogger.SetEnabled(wasLogging);
            if (Directory.Exists(backups)) Directory.Delete(backups, true);
        }
    }

}
#endif
