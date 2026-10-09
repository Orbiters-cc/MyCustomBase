using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Typed authoring operations used by MCP; all packaging/apply logic stays in the creator services.</summary>
public static class MCBAuthoringService
{
    public sealed class Draft
    {
        public AvatarDiscoveredAsset asset;
        public string version = "1.0.0", title, changelog, logicPrefab;
        public Scope scope = Scope.BETA;
        public CustomBaseVersion parent;
        public List<string> sourceModels = new List<string>();
        public List<string> customModels = new List<string>();
        public List<CreatorBlendshapeEntry> blendshapes = new List<CreatorBlendshapeEntry>();
        public VersionCustomization customization = new VersionCustomization();
        public bool advancedMesh = true;
        // Normal map for the veins detail layer (an asset path), or null.
        public string customVeins;
        // Renderer paths whose materials MCB suggests switching to Realistic lighting.
        public List<string> suggestRealistic = new List<string>();
    }
    public sealed class Registration
    {
        public string name, description, avatarBaseName, requestId;
        public int avatarBaseId;
        public int existingAssetId;
        public ModelFileData[] sourceFiles;
        public OriginalBaseVersionData[] originalBaseVersions;
        public string[] localSourceModels;
        // Trusted creators: verify a Discord role and, with it, publish versions without XOR protection.
        public VersionProtection protection;
    }
    private sealed class Session : IDisposable
    {
        public readonly MCBEditor Editor;
        private readonly bool owns;
        public Session(MyCustomBase owner)
        {
            Editor = Resources.FindObjectsOfTypeAll<MCBEditor>().FirstOrDefault(e => e.customBaseTarget == owner);
            if (Editor == null) { Editor = (MCBEditor)UnityEditor.Editor.CreateEditor(owner, typeof(MCBEditor)); owns = true; }
        }
        public void Dispose() { if (owns && Editor != null) UnityEngine.Object.DestroyImmediate(Editor); }
    }

    public static MyCustomBase Target(int instanceId)
    {
        var obj = EditorUtility.InstanceIDToObject(instanceId);
        var owner = obj as MyCustomBase ?? (obj as GameObject)?.GetComponent<MyCustomBase>();
        if (owner == null || EditorUtility.IsPersistent(owner)) throw new ArgumentException("Choose a scene MyCustomBase component or its GameObject from inspect.");
        return owner;
    }
    public static object Inspect(MyCustomBase owner) => new
    {
        instanceId = owner.GetInstanceID(), avatar = AvatarPaths.Root(owner).name,
        draft = string.IsNullOrWhiteSpace(owner.creatorAuthoringDraftJson) ? null : JsonConvert.DeserializeObject<Draft>(owner.creatorAuthoringDraftJson),
        appliedAssetId = owner.appliedCustomBaseAssetId, modes = ModeService.EnabledIds(owner),
        customization = owner.appliedCustomization,
        renderers = AvatarPaths.Root(owner).GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).Select(r => new
        {
            path = AnimationUtility.CalculateTransformPath(r.transform, AvatarPaths.Root(owner)),
            blendshapes = Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.sharedMesh.GetBlendShapeName).ToArray()
        }).ToArray()
    };

    public static void Configure(MyCustomBase owner, Draft draft)
    {
        if (draft?.asset == null || draft.asset.id <= 0) throw new ArgumentException("Choose a registered custom base asset.");
        draft.customization.Validate();
        var (sources, customs) = Models(draft);
        var logic = string.IsNullOrWhiteSpace(draft.logicPrefab) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(draft.logicPrefab);
        var veins = string.IsNullOrWhiteSpace(draft.customVeins) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(draft.customVeins)
            ?? throw new ArgumentException("Custom veins must be a texture asset: " + draft.customVeins);
        if (!string.IsNullOrWhiteSpace(draft.logicPrefab) && (logic == null || !draft.logicPrefab.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Logic must be a saved prefab.");
        using var session = new Session(owner);
        session.Editor.creatorModule.ConfigureVersionMetadata(draft.version, draft.title, draft.changelog, draft.scope, draft.parent);
        Undo.RecordObject(owner, "Configure MCB authoring");
        UseModels(owner, sources, customs);
        owner.avatarLogicPrefab = logic; owner.creatorCustomization = draft.customization.Clone();
        owner.customBlendshapesForCreator = draft.blendshapes;
        owner.useAdvancedMeshReplacementForCreator = draft.advancedMesh;
        owner.includeCustomVeinsForCreator = veins != null; owner.customVeinsNormalMap = veins;
        owner.includeSuggestRealisticForCreator = draft.suggestRealistic.Count > 0; owner.suggestRealisticMeshPathsForCreator = draft.suggestRealistic.ToList();
        owner.includeDynamicNormalsBodyForCreator = owner.includeDynamicNormalsFlexingForCreator = false;
        owner.creatorAuthoringDraftJson = JsonConvert.SerializeObject(draft);
        EditorUtility.SetDirty(owner); session.Editor.serializedObject.Update();
    }
    private static (GameObject[] sources, GameObject[] customs) Models(Draft draft)
    {
        if (draft.sourceModels.Count != draft.customModels.Count) throw new ArgumentException("Map one custom model (or null to keep original) per source model.");
        return (draft.sourceModels.Select(p => Model(p)).ToArray(),
            draft.customModels.Select(p => string.IsNullOrWhiteSpace(p) ? null : Model(p, false)).ToArray());
    }

    private static void UseModels(MyCustomBase owner, GameObject[] sources, GameObject[] customs)
    {
        FileManagerService.SetCreatorSourceFiles(owner, sources.ToList());
        owner.modelFileBuildEntries = customs.Select(model => new CreatorModelFileBuildEntry { customFbx = model }).ToList();
    }

    private static GameObject Model(string path, bool requireFbx = true)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (model == null || (requireFbx && !(AssetImporter.GetAtPath(path) is ModelImporter))
            || (!requireFbx && model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0))
            throw new ArgumentException(requireFbx ? "Import the FBX model first: " + path : "Choose a model or prefab with skinned meshes: " + path);
        return model;
    }

    public static IEnumerator Build(MyCustomBase owner, Action<VersionArtifact> complete)
    {
        var draft = JsonConvert.DeserializeObject<Draft>(owner.creatorAuthoringDraftJson ?? "null") ?? throw new InvalidOperationException("Configure an authoring draft first.");
        // One build or publish at a time across MCP and every creator window (each editor only knows its own isSubmitting).
        using var operation = VersionOperationGuard.Acquire("building version " + draft.version);
        using var session = new Session(owner); var editor = session.Editor;
        if (editor.isSubmitting) throw new InvalidOperationException("This avatar already has an active operation.");
        var previousAsset = editor.GetSelectedAsset();
        editor.isSubmitting = true;
        try
        {
            editor.SetCreatorWindowAsset(draft.asset); editor.serializedObject.Update();
            // Build exactly the saved draft: an open creator form may have loaded another version's settings meanwhile, and
            // opening the editor detects the scene avatar's own models, which differ from the draft's when a version is applied.
            var (sources, customs) = Models(draft);
            Undo.RecordObject(owner, "Build MCB authoring draft");
            UseModels(owner, sources, customs);
            owner.creatorCustomization = draft.customization.Clone();
            owner.useAdvancedMeshReplacementForCreator = draft.advancedMesh;
            owner.customBlendshapesForCreator = draft.blendshapes;
            var veins = string.IsNullOrWhiteSpace(draft.customVeins) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(draft.customVeins);
            owner.includeCustomVeinsForCreator = veins != null; owner.customVeinsNormalMap = veins;
            owner.includeSuggestRealisticForCreator = draft.suggestRealistic.Count > 0; owner.suggestRealisticMeshPathsForCreator = draft.suggestRealistic.ToList();
            editor.serializedObject.Update();
            editor.creatorModule.ConfigureVersionMetadata(draft.version, draft.title, draft.changelog, draft.scope, draft.parent);
            yield return editor.creatorModule.BuildNewVersionCoroutine(complete, operation);
        }
        // The window's asset is restored whatever failed, including a draft model that no longer loads.
        finally { editor.isSubmitting = false; EditorUtility.ClearProgressBar(); editor.SetCreatorWindowAsset(previousAsset); }
    }

    public static IEnumerator Apply(MyCustomBase owner, CustomBaseVersion version)
    {
        using var session = new Session(owner);
        yield return new VersionActions(session.Editor, new NetworkService(), new FileManagerService()).ApplyOrResetCoroutine(version, false);
        if (owner.appliedCustomBaseAssetId != version.assetId || owner.appliedCustomBaseVersionString != version.version)
            throw new InvalidOperationException("Version application did not finish. Inspect MCB warnings and the Console.");
    }

    public static IEnumerator Reset(MyCustomBase owner)
    {
        using var session = new Session(owner);
        yield return new VersionActions(session.Editor, new NetworkService(), new FileManagerService()).ApplyOrResetCoroutine(null, true);
        if (owner.appliedCustomBaseAssetId != 0 || !string.IsNullOrEmpty(owner.appliedCustomBaseVersionString))
            throw new InvalidOperationException("Reset did not finish. Inspect MCB warnings and the Console.");
    }

    public static IEnumerator Publish(MyCustomBase owner, CustomBaseVersion version, Action complete)
    {
        using var session = new Session(owner);
        bool published = false;
        // The release checkpoint names the published asset: the draft's when it is this version's asset.
        var draft = string.IsNullOrWhiteSpace(owner.creatorAuthoringDraftJson) ? null : JsonConvert.DeserializeObject<Draft>(owner.creatorAuthoringDraftJson);
        string assetName = draft?.asset != null && draft.asset.id == version.assetId ? draft.asset.name : null;
        yield return VersionPublisher.PublishCoroutine(session.Editor, new NetworkService(), new FileManagerService(), version, () => published = true,
            interactive: false, assetName: assetName);
        if (!published) throw new InvalidOperationException(string.IsNullOrWhiteSpace(session.Editor.submitError)
            ? "Publication did not finish. Inspect MCB warnings and the Console." : session.Editor.submitError);
        complete();
    }

    /// <summary>Changes an owned asset's protection; its next versions follow it.</summary>
    public static IEnumerator SetAssetProtection(MyCustomBase owner, int assetId, VersionProtection protection, Action<VersionProtection> complete)
    {
        if (assetId <= 0 || protection == null) throw new ArgumentException("Provide assetId and protection {xor, discordRole}.");
        using var session = new Session(owner);
        var save = DiscordAccessService.SaveProtectionAsync(session.Editor.authToken, assetId, protection);
        while (!save.IsCompleted) yield return null;
        complete(save.GetAwaiter().GetResult());
    }

    public static IEnumerator ImportFbx(string externalPath, string destination, Action<string> complete)
    {
        if (!MCBUtils.TryResolveProjectAssetPath(destination, out string unityPath, out string fullPath) || !unityPath.StartsWith("Assets/", StringComparison.Ordinal)
            || !unityPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Destination must be an FBX path inside Assets.");
        if (!externalPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || !File.Exists(externalPath)) throw new ArgumentException("Source FBX does not exist.");
        var copy = Task.Run(() =>
        {
            if (File.Exists(fullPath))
            {
                if (MCBUtils.CalculateFileHash(fullPath) != MCBUtils.CalculateFileHash(externalPath)) throw new IOException("Destination already contains a different FBX. Choose a new path.");
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)); File.Copy(externalPath, fullPath);
                // Reuse the source rig/import settings before Unity's first import, with a new GUID.
                // This avoids a second expensive import just to turn on Read/Write.
                if (File.Exists(externalPath + ".meta") && !File.Exists(fullPath + ".meta"))
                {
                    string meta = File.ReadAllText(externalPath + ".meta");
                    if (meta.Contains("\nModelImporter:"))
                    {
                        meta = System.Text.RegularExpressions.Regex.Replace(meta, "(?m)^guid: [a-fA-F0-9]+", "guid: " + Guid.NewGuid().ToString("N"));
                        meta = System.Text.RegularExpressions.Regex.Replace(meta, "(?m)^(\\s*)isReadable: [01]", "$1isReadable: 1");
                        File.WriteAllText(fullPath + ".meta", meta);
                    }
                }
            }
        });
        while (!copy.IsCompleted) yield return null; copy.GetAwaiter().GetResult();
        yield return null;
        AssetDatabase.ImportAsset(unityPath, ImportAssetOptions.ForceUpdate);
        while (EditorApplication.isUpdating) yield return null;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(unityPath) == null) throw new InvalidOperationException("Unity did not import the FBX.");
        var importer = (ModelImporter)AssetImporter.GetAtPath(unityPath);
        if (!importer.isReadable) { importer.isReadable = true; importer.SaveAndReimport(); yield return null; }
        complete(unityPath);
    }

    public static IEnumerator Register(MyCustomBase owner, Registration registration, Action<AvatarDiscoveredAsset> complete)
    {
        if (registration.existingAssetId <= 0 && (string.IsNullOrWhiteSpace(registration.name) || string.IsNullOrWhiteSpace(registration.requestId))) throw new ArgumentException("Name and a stable requestId are required when creating an asset.");
        if (registration.existingAssetId <= 0 && registration.avatarBaseId <= 0 && string.IsNullOrWhiteSpace(registration.avatarBaseName)) throw new ArgumentException("Choose an existing avatar base or provide its name.");
        if (registration.sourceFiles?.Length == 0 || registration.sourceFiles == null) throw new ArgumentException("Original source metadata is required.");
        foreach (var path in registration.localSourceModels ?? Array.Empty<string>()) Model(path);
        var matches = AvatarBaseSourceMatcher.MatchOneToOne(
            registration.sourceFiles.Select(f => new AvatarBaseSourceMatcher.SourceFile { path = f.path, hash = f.hash }),
            (registration.localSourceModels ?? Array.Empty<string>()).Select(p => new AvatarBaseSourceMatcher.LocalFile { path = p, hash = MCBUtils.CalculateFileHash(p) }));
        if (matches == null) throw new ArgumentException("The scene source models must match the exact original files before registration.");
        foreach (var file in registration.sourceFiles.Concat((registration.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>()).SelectMany(v => v.sourceFiles)))
        { OriginalBaseLibrary.Resolve(file); yield return null; }
        using var session = new Session(owner);
        // Reuse store-linked listings. A name collision is a choice for the creator, never a new duplicate.
        var ownedRequest = new NetworkService().DownloadBytesAsync(MCBUtils.getApiUrl("creator") + "/assets", authToken: session.Editor.authToken);
        while (!ownedRequest.IsCompleted) yield return null;
        var ownedResult = ownedRequest.GetAwaiter().GetResult();
        if (!ownedResult.success) throw new IOException("Could not check existing assets: " + ownedResult.error);
        var ownedAssets = JObject.Parse(System.Text.Encoding.UTF8.GetString(ownedResult.data))["assets"] as JArray
            ?? throw new InvalidDataException("Creator asset response is incomplete.");
        if (registration.existingAssetId > 0)
        {
            var existing = ownedAssets.OfType<JObject>().SingleOrDefault(a => a.Value<int>("id") == registration.existingAssetId)
                ?? throw new ArgumentException("Choose an asset owned by the signed-in creator.");
            var avatarBase = existing["selectedAvatarBase"]?.Type == JTokenType.Object
                ? existing["selectedAvatarBase"].ToObject<AvatarAssetBaseInfo>() : null;
            if (registration.avatarBaseId > 0 && avatarBase?.id != registration.avatarBaseId)
            {
                var assignment = OriginalBaseSupportService.AssignAvatarBase(registration.existingAssetId, registration.avatarBaseId, session.Editor.authToken);
                while (!assignment.IsCompleted) yield return null;
                avatarBase = assignment.GetAwaiter().GetResult();
            }
            var requested = registration.originalBaseVersions?.Length > 0 ? registration.originalBaseVersions
                : new[] { new OriginalBaseVersionData { key = OriginalBaseLibrary.Key(registration.sourceFiles), label = "Original base", sourceFiles = registration.sourceFiles } };
            var linking = OriginalBaseSupportService.Register(registration.existingAssetId, session.Editor.authToken, requested);
            while (!linking.IsCompleted) yield return null;
            var versions = linking.GetAwaiter().GetResult();
            // Originals can share files: bind one source per requested file.
            var sources = CustomBaseSourceSetupTransaction.SelectRequestedSources(versions.SelectMany(v => v.sourceFiles), registration.sourceFiles,
                OriginalBaseLibrary.Key(registration.sourceFiles));
            BindSources(owner, registration, sources);
            int.TryParse(AuthenticationService.GetAuth()?.user, out int ownerId);
            complete(new AvatarDiscoveredAsset { id = registration.existingAssetId, name = existing.Value<string>("name"), ownerId = ownerId,
                sourceFiles = sources, sourceVersions = versions, avatarBase = avatarBase, isCompatible = true });
            yield break;
        }
        var duplicate = ownedAssets.OfType<JObject>().FirstOrDefault(a => string.Equals(a.Value<string>("name")?.Trim(), registration.name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (duplicate != null) throw new InvalidOperationException("An asset named '" + registration.name.Trim() + "' already exists. Set existingAssetId to " + duplicate.Value<int>("id") + " to link its MCB versions.");
        var metadata = new JObject { ["name"] = registration.name.Trim(), ["description"] = registration.description ?? "", ["mcbCreateSceneMode"] = "default-base",
            ["protection"] = registration.protection == null ? null : JObject.FromObject(new { registration.protection.xor, registration.protection.discordRole }),
            ["sourceFiles"] = JArray.FromObject(registration.sourceFiles), ["originalBaseVersions"] = JArray.FromObject(registration.originalBaseVersions ?? Array.Empty<OriginalBaseVersionData>()) };
        if (registration.avatarBaseId > 0) metadata["avatarBaseId"] = registration.avatarBaseId;
        else metadata["otherAvatarBaseName"] = registration.avatarBaseName.Trim();
        using var request = CustomBaseRegistration.Request(CustomBaseRegistration.Form(metadata.ToString(Formatting.None)), session.Editor.authToken, registration.requestId);
        yield return MCBManagedRequest.SendUnityWebRequest(request, CustomBaseRegistration.Url, MCBRequestPolicy.Backend("Create custom base"));
        if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Registration failed: HTTP " + request.responseCode + " " + request.error);
        var result = JsonConvert.DeserializeObject<CreateCustomBaseAssetResponse>(request.downloadHandler.text)?.asset;
        if (result == null || result.id <= 0) throw new InvalidOperationException("Registration returned no asset.");
        // An idempotent retry can return all registered original versions. Bind only the requested scene's originals.
        var sceneSources = CustomBaseSourceSetupTransaction.SelectRequestedSources(result.sourceFiles, registration.sourceFiles,
            OriginalBaseLibrary.Key(registration.sourceFiles));
        BindSources(owner, registration, sceneSources);
        int.TryParse(AuthenticationService.GetAuth()?.user, out int authenticatedUserId);
        complete(new AvatarDiscoveredAsset { id = result.id, name = result.name, ownerId = result.ownerId ?? authenticatedUserId,
            ownerUsername = result.ownerUsername, sourceFiles = sceneSources, sourceVersions = registration.originalBaseVersions,
            avatarBase = result.selectedAvatarBase == null ? null : new AvatarAssetBaseInfo { id = result.selectedAvatarBase.id, name = result.selectedAvatarBase.name }, isCompatible = true });
    }

    private static void BindSources(MyCustomBase owner, Registration registration, ModelFileData[] sources)
    {
        using (var transaction = new CustomBaseSourceSetupTransaction(owner))
        { transaction.CommitDefaultBase(sources, registration.localSourceModels); transaction.Complete(); }
        AvatarPathOverrideService.SyncSourceModelFileIds(owner, sources);
    }
}
