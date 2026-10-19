#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

/// <summary>The manifest of a Blender export as MCB reads it: models, shape keys per mesh, XMuscles correctives, protocol.</summary>
public class BlenderExportManifestTests
{
    private static readonly string[] BicepKeys = { "XMSL_BAKE_Bicep_01", "XMSL_BAKE_Bicep_02", "XMSL_BAKE_Bicep_03" };

    // The xmuscle block written by XMuscle Orbit Helper API 2: a stretch sensor, then a twist sensor on a pivot.
    private const string XMuscleV2 = @"{
    ""apiVersion"": 2,
    ""muscles"": [{
      ""name"": ""Bicep"",
      ""sensors"": [
        { ""receiver"": { ""bone"": ""UpperArm.L"", ""position"": [-0.0057, 0.0279, 0.0419] },
          ""sender"": { ""bone"": ""LowerArm.L"", ""position"": [0.0011, 0.0814, 0.0178] } },
        { ""receiver"": { ""bone"": ""UpperArm.L"", ""position"": [0.1437, 0.0, 0.0] },
          ""sender"": { ""bone"": ""LowerArm.L"", ""position"": [0.0, 0.0, 0.1437] },
          ""aim"": { ""bone"": ""LowerArm.L"" } }
      ],
      ""samples"": [
        { ""shapeKey"": ""XMSL_BAKE_Bicep_01"", ""mesh"": ""Body"", ""distances"": [0.4378, 0.2032] },
        { ""shapeKey"": ""XMSL_BAKE_Bicep_02"", ""mesh"": ""Body"", ""distances"": [0.384, 0.2032] },
        { ""shapeKey"": ""XMSL_BAKE_Bicep_03"", ""mesh"": ""Body"", ""distances"": [0.2877, 0.2032] }
      ]
    }],
    ""warnings"": [""Triceps: Choose a valid pose bone on the selected armature""]
  }";

    // An export as a Blender-side MCB exporter writes it.
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

    private string folder;

    [SetUp]
    public void SetUp()
    {
        folder = Path.Combine(Path.GetTempPath(), "MCB_BlenderExportManifest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(folder, true);
    }

    private static string Manifest(int protocolVersion = BlenderExportImport.ProtocolVersion, string xmuscle = XMuscleV2) =>
        ManifestTemplate.Replace("{protocolVersion}", protocolVersion.ToString()).Replace("{xmuscle}", xmuscle);

    private string WriteFile(string name, string contents)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private BlenderExportImport.BlenderExportManifest ReadManifest(string json) =>
        BlenderExportImport.ReadExportManifest(WriteFile("manifest.json", json));

    private static void AssertBicep(MuscleCorrectiveSet set)
    {
        Assert.AreEqual(2, set.apiVersion);
        var muscle = set.muscles.Single();
        Assert.AreEqual("Bicep", muscle.name);
        Assert.AreEqual(2, muscle.sensors.Count);
        Assert.AreEqual("UpperArm.L", muscle.sensors[0].receiver.bone);
        CollectionAssert.AreEqual(new[] { 0.0011f, 0.0814f, 0.0178f }, muscle.sensors[0].sender.position);
        Assert.IsFalse(muscle.sensors[0].HasAim);
        Assert.IsTrue(muscle.sensors[1].HasAim);
        Assert.AreEqual("LowerArm.L", muscle.sensors[1].aim.bone);
        CollectionAssert.AreEqual(BicepKeys, muscle.samples.Select(sample => sample.shapeKey));
        CollectionAssert.AreEqual(new[] { 0.384f, 0.2032f }, muscle.samples[1].distances);
        CollectionAssert.AreEqual(new[] { "Body", "Body", "Body" }, muscle.samples.Select(sample => sample.mesh));
        CollectionAssert.AreEqual(new[] { "Triceps: Choose a valid pose bone on the selected armature" }, set.warnings);
    }

    [Test]
    public void ProtocolTwoManifestReadsShapeKeysByMeshAndMuscleCorrectives()
    {
        var manifest = ReadManifest(Manifest());

        var model = manifest.models.Single();
        Assert.AreEqual("CUSTOM_BASE", model.role);
        Assert.AreEqual("models/01_Avatar.fbx", model.path);
        Assert.AreEqual("Assets/Avatar/Avatar.fbx", model.targetFbxPath);
        CollectionAssert.AreEqual(new[] { "Body", "Shirt" }, model.meshNames);
        CollectionAssert.AreEqual(new[] { "Smile" }.Concat(BicepKeys), model.shapeKeysByMesh["Body"]);
        CollectionAssert.IsEmpty(model.shapeKeysByMesh["Shirt"]);
        Assert.IsFalse(manifest.advancedMeshBlenderLink);
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

        var error = Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ReadExportManifest(path));

        StringAssert.Contains("protocol " + protocolVersion, error.Message);
        StringAssert.Contains(protocolVersion > BlenderExportImport.ProtocolVersion ? "the MCB Unity package" : "the Blender side", error.Message);
    }

    [Test]
    public void AFileThatIsNotAnExportIsRefused()
    {
        string path = WriteFile("manifest.json", "{ \"kind\": \"something.else\", \"protocolVersion\": 2 }");

        var error = Assert.Throws<InvalidOperationException>(() => BlenderExportImport.ReadExportManifest(path));

        StringAssert.Contains("not a Blender MCB export", error.Message);
    }

    [Test]
    public void MuscleCorrectiveSetKeepsItsDataThroughNewtonsoftAndUnitySerialization()
    {
        var set = ReadManifest(Manifest()).xmuscle;

        // Newtonsoft writes the manifest's field names, so a stored set reads back like a manifest block.
        var jsonConvert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", true);
        string json = (string)jsonConvert.GetMethod("SerializeObject", new[] { typeof(object) }).Invoke(null, new object[] { set });
        StringAssert.Contains("\"shapeKey\":\"XMSL_BAKE_Bicep_02\",\"mesh\":\"Body\",\"distances\":[0.384,0.2032]", json);
        AssertBicep(ReadManifest(Manifest(xmuscle: json)).xmuscle);

        AssertBicep(JsonUtility.FromJson<MuscleCorrectiveSet>(JsonUtility.ToJson(set)));
    }

    [Test]
    public void FileNamesAreSanitized()
    {
        Assert.AreEqual("Body_Base", BlenderExportImport.SanitizeFileName("Body/Base"));
        Assert.AreEqual("mcb", BlenderExportImport.SanitizeFileName("  "));
    }
}
#endif
