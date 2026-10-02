#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

public class BlenderSyncProtocolTests
{
    private const BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Service = typeof(BlenderSyncService);
    private static readonly string[] BicepKeys = { "XMSL_BAKE_Bicep_01", "XMSL_BAKE_Bicep_02", "XMSL_BAKE_Bicep_03" };

    // The xmuscle block written by XMuscle Orbit Helper API 1.
    private const string XMuscleV1 = @"{
    ""apiVersion"": 1,
    ""muscles"": [{
      ""name"": ""Bicep"",
      ""bone"": ""LowerArm.L"",
      ""axis"": ""X"",
      ""samples"": [
        { ""shapeKey"": ""XMSL_BAKE_Bicep_01"", ""mesh"": ""Body"", ""angleDeg"": 0.0 },
        { ""shapeKey"": ""XMSL_BAKE_Bicep_02"", ""mesh"": ""Body"", ""angleDeg"": 45.0 },
        { ""shapeKey"": ""XMSL_BAKE_Bicep_03"", ""mesh"": ""Body"", ""angleDeg"": 90.0 }
      ]
    }],
    ""warnings"": [""Triceps: Choose a valid pose bone on the selected armature""]
  }";

    // An export as the MCB Blender extension 0.1.0 writes it.
    private const string ManifestTemplate = @"{
  ""kind"": ""orbiters.mcb.blenderExport"",
  ""protocolVersion"": {protocolVersion},
  ""sessionId"": ""session"",
  ""token"": ""token"",
  ""createdAtUtc"": ""2026-10-01T00:52:04.416549Z"",
  ""source"": { ""addon"": ""MCB"", ""addonVersion"": ""0.1.0"", ""blendFile"": """" },
  ""target"": { ""customBaseName"": ""Test Base"", ""targetFbxPath"": ""Assets/Avatar/Avatar.fbx"", ""unityProjectPath"": """" },
  ""models"": [{
    ""role"": ""CUSTOM_BASE"",
    ""path"": ""models/01_Avatar.fbx"",
    ""targetFbxPath"": ""Assets/Avatar/Avatar.fbx"",
    ""transportFormat"": ""FBX"",
    ""unityImportMode"": ""fbxReplace"",
    ""payloadSourceFormat"": ""FBX"",
    ""primaryBodyObject"": ""Body"",
    ""armatureObject"": ""Armature"",
    ""meshNames"": [""Body"", ""Shirt""],
    ""missingUnityTargetMeshNames"": [],
    ""shapeKeysByMesh"": { ""Body"": [""Smile"", ""XMSL_BAKE_Bicep_01"", ""XMSL_BAKE_Bicep_02"", ""XMSL_BAKE_Bicep_03""], ""Shirt"": [] },
    ""armatureBones"": [""UpperArm.L"", ""LowerArm.L""],
    ""fbxSettings"": { ""applyScaleOptions"": ""FBX_SCALE_ALL"", ""addLeafBones"": false, ""useMeshModifiers"": false }
  }],
  ""blendshapes"": [],
  ""xmuscle"": {xmuscle},
  ""advancedMeshBlenderLink"": false,
  ""transport"": { ""format"": ""FBX"", ""unityImportMode"": ""fbxReplace"", ""payloadSourceFormat"": ""FBX"" }
}";

    private const string HeartbeatTemplate = @"{
  ""kind"": ""orbiters.mcb.blenderHeartbeat"",
  ""protocolVersion"": {protocolVersion},
  ""sessionId"": ""session"",
  ""token"": ""{token}"",
  ""updatedAtUtc"": ""2026-10-01T00:50:41.879890Z"",
  ""blendFile"": ""C:/Avatar.blend"",
  ""blenderVersion"": ""5.2.2 LTS"",
  ""addonVersion"": ""0.1.0"",
  ""xmuscles"": { ""installed"": true, ""enabled"": true, ""version"": ""4.9.7"" },
  ""xmuscleToolkit"": { ""installed"": true, ""enabled"": false, ""version"": ""0.9.0"" }
}";

    private string folder;

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "MCB_BlenderSyncProtocol_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(folder, true);
    }

    private static string Manifest(int protocolVersion = 2, string xmuscle = XMuscleV1) =>
        ManifestTemplate.Replace("{protocolVersion}", protocolVersion.ToString()).Replace("{xmuscle}", xmuscle);

    private static string Heartbeat(int protocolVersion = 2, string token = "token") =>
        HeartbeatTemplate.Replace("{protocolVersion}", protocolVersion.ToString()).Replace("{token}", token);

    private string WriteFile(string name, string contents)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private BlenderSyncService.BlenderExportManifest ReadManifest(string json) =>
        BlenderSyncService.ReadExportManifest(WriteFile("manifest.json", json));

    private static object NewSession(string heartbeatPath)
    {
        var type = Service.GetNestedType("ActiveSession", BindingFlags.NonPublic);
        var session = Activator.CreateInstance(type, true);
        type.GetField("sessionId").SetValue(session, "session");
        type.GetField("token").SetValue(session, "token");
        type.GetField("heartbeatPath").SetValue(session, heartbeatPath);
        return session;
    }

    private static BlenderSyncService.BlenderEnvironment ReadEnvironment(object session)
    {
        Service.GetMethod("UpdateConnectionState", Private).Invoke(null, new[] { session });
        return (BlenderSyncService.BlenderEnvironment)session.GetType().GetField("blenderEnvironment").GetValue(session);
    }

    private static object Property(object source, string name) => source.GetType().GetProperty(name).GetValue(source);

    private static void AssertBicep(MuscleCorrectiveSet set)
    {
        Assert.AreEqual(1, set.apiVersion);
        var muscle = set.muscles.Single();
        Assert.AreEqual("Bicep", muscle.name);
        Assert.AreEqual("LowerArm.L", muscle.bone);
        Assert.AreEqual("X", muscle.axis);
        CollectionAssert.AreEqual(BicepKeys, muscle.samples.Select(sample => sample.shapeKey));
        CollectionAssert.AreEqual(new[] { 0f, 45f, 90f }, muscle.samples.Select(sample => sample.angleDeg));
        CollectionAssert.AreEqual(new[] { "Body", "Body", "Body" }, muscle.samples.Select(sample => sample.mesh));
        CollectionAssert.AreEqual(new[] { "Triceps: Choose a valid pose bone on the selected armature" }, set.warnings);
    }

    [Test]
    public void ProtocolTwoManifestReadsShapeKeysByMeshAndMuscleCorrectives()
    {
        var manifest = ReadManifest(Manifest());

        var model = manifest.models.Single();
        CollectionAssert.AreEqual(new[] { "Smile" }.Concat(BicepKeys), model.shapeKeysByMesh["Body"]);
        CollectionAssert.IsEmpty(model.shapeKeysByMesh["Shirt"]);
        AssertBicep(manifest.xmuscle);
    }

    [Test]
    public void ManifestWithoutXMuscleOrbitHelperHasNoCorrectiveSet()
    {
        Assert.IsNull(ReadManifest(Manifest(xmuscle: "null")).xmuscle);
    }

    [TestCase(1)]
    [TestCase(3)]
    public void ManifestOfAnotherProtocolIsRejected(int protocolVersion)
    {
        string path = WriteFile("manifest.json", Manifest(protocolVersion));

        var error = Assert.Throws<InvalidOperationException>(() => BlenderSyncService.ReadExportManifest(path));

        StringAssert.Contains("protocol " + protocolVersion, error.Message);
    }

    [Test]
    public void ReadyMarkerOfProtocolOneIsRejectedWithTheSideToUpdate()
    {
        string ready = WriteFile("ready.json",
            "{\"kind\":\"orbiters.mcb.blenderExportReady\",\"protocolVersion\":1,\"sessionId\":\"session\",\"token\":\"token\",\"manifestPath\":\"manifest.json\"}");
        WriteFile("manifest.json", Manifest());

        var error = Assert.Throws<TargetInvocationException>(() =>
            Service.GetMethod("ProcessReadyFile", Private).Invoke(null, new[] { NewSession(null), ready })).InnerException;

        Assert.IsInstanceOf<InvalidOperationException>(error);
        StringAssert.Contains("update the MCB Blender extension", error.Message);
    }

    [Test]
    public void MuscleCorrectiveSetKeepsItsDataThroughNewtonsoftAndUnitySerialization()
    {
        var set = ReadManifest(Manifest()).xmuscle;

        // Newtonsoft writes the manifest's field names, so a stored set reads back like a manifest block.
        var jsonConvert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", true);
        string json = (string)jsonConvert.GetMethod("SerializeObject", new[] { typeof(object) }).Invoke(null, new object[] { set });
        StringAssert.Contains("\"shapeKey\":\"XMSL_BAKE_Bicep_02\",\"mesh\":\"Body\",\"angleDeg\":45.0", json);
        AssertBicep(ReadManifest(Manifest(xmuscle: json)).xmuscle);

        AssertBicep(JsonUtility.FromJson<MuscleCorrectiveSet>(JsonUtility.ToJson(set)));
    }

    [Test]
    public void HeartbeatOfTheSessionReportsTheXMuscleExtensions()
    {
        var session = NewSession(WriteFile("blender_heartbeat.json", Heartbeat()));

        var environment = ReadEnvironment(session);

        Assert.AreEqual("connected", session.GetType().GetField("connectionState").GetValue(session));
        Assert.AreEqual("5.2.2 LTS", environment.blenderVersion);
        Assert.AreEqual("0.1.0", environment.addonVersion);
        Assert.AreEqual((true, true, "4.9.7"), (environment.xmuscles.installed, environment.xmuscles.enabled, environment.xmuscles.version));
        Assert.AreEqual((true, false, "0.9.0"),
            (environment.xmuscleToolkit.installed, environment.xmuscleToolkit.enabled, environment.xmuscleToolkit.version));
    }

    [TestCase(1, "token")]
    [TestCase(2, "another session's token")]
    public void HeartbeatOfAnotherProtocolOrSessionIsIgnored(int protocolVersion, string token)
    {
        var session = NewSession(WriteFile("blender_heartbeat.json", Heartbeat(protocolVersion, token)));

        Assert.IsNull(ReadEnvironment(session));
        Assert.AreEqual("connected", session.GetType().GetField("connectionState").GetValue(session));
    }

    [Test]
    public void LaunchConfigInstallsTheReleasedExtensionsWithTheExtensionCommand()
    {
        object extensions = BlenderAddonService.CreateLaunchPayload();
        string mcbVersion = BlenderAddonService.BlenderAddonVersion;
        string toolkitVersion = BlenderAddonService.XMuscleToolkitVersion;

        Assert.AreEqual("user_default", Property(extensions, "repository"));
        object mcb = Property(extensions, "mcb");
        Assert.AreEqual("mcb_blender", Property(mcb, "id"));
        Assert.AreEqual(mcbVersion, Property(mcb, "version"));
        string releaseUrl = $"https://github.com/Orbiters-cc/MCB-Blender-Add-on/releases/download/v{mcbVersion}/mcb_blender-{mcbVersion}.zip";
        Assert.AreEqual(EditorPrefs.GetString("MCB_BlenderAddonDownloadUrl", releaseUrl), Property(mcb, "downloadUrl"));
        object toolkit = ((IEnumerable)Property(extensions, "optional")).Cast<object>().Single();
        Assert.AreEqual("xmuscle_orbit_helper", Property(toolkit, "id"));
        Assert.AreEqual(toolkitVersion, Property(toolkit, "version"));
        Assert.AreEqual("xmusclesystem", Property(toolkit, "whenInstalled"));
        Assert.AreEqual($"https://github.com/Orbiters-cc/XMuscle-orbit-helper/releases/download/v{toolkitVersion}/xmuscle_orbit_helper-{toolkitVersion}.zip",
            Property(toolkit, "downloadUrl"));

        string configPath = Path.Combine(folder, "avatar's launch.json");
        string script = Path.Combine(folder, "launch_blender.py");
        BlenderAddonService.WriteBootstrapScript(script, configPath);
        string text = File.ReadAllText(script);
        StringAssert.StartsWith("CONFIG_PATH = \"" + configPath.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n", text);
        StringAssert.Contains("'--command', 'extension', 'install-file', '--repo', repository, '--enable'", text);
    }
}
#endif
