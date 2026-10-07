using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

// Adjerry91's face tracking template brings a bone-less debug panel from its own FBX: it came first in the hierarchy and
// MCB took it for the avatar's base model.
public class AvatarBaseModelsTests
{
    private const string BasePath = "Assets/Base/Base.fbx";
    private const string PanelPath = "Packages/vrcft/FT_SRanipal_Debug.fbx";
    private const string OutfitPath = "Assets/Outfit/Outfit.fbx";
    private static readonly string[] Skeleton = { "Hips", "Spine", "Chest", "Head", "Tail" };

    private readonly List<Object> owned = new List<Object>();
    private readonly Dictionary<string, GameObject> models = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, List<SkinnedMeshRenderer>> scene = new Dictionary<string, List<SkinnedMeshRenderer>>();

    [TearDown]
    public void Clean()
    {
        foreach (var o in owned) if (o != null) Object.DestroyImmediate(o);
        owned.Clear();
        models.Clear();
        scene.Clear();
    }

    [Test]
    public void BaseModelComesFirstAndBonelessPropsStayOut()
    {
        var avatar = Avatar();
        Skin(avatar, "Body", BasePath, Chain(avatar.transform, "Armature", Skeleton));
        Skin(avatar, "VF_SRanipal_VRCFT/Window/FT_Debug", PanelPath);
        var outfitArmature = Chain(Child(avatar.transform, "Outfit"), "Armature", "Hips", "Spine");
        Skin(avatar, "Outfit/Hoodie", OutfitPath, outfitArmature);
        models[BasePath] = Model("Base", Skeleton);
        models[PanelPath] = Model("FT_SRanipal_Debug");
        models[OutfitPath] = Model("Outfit", "Hips", "Spine", "Hoodie_Pocket");

        var baseModels = new AvatarBaseModels(avatar.transform);

        Assert.That(Order(baseModels, PanelPath, OutfitPath, BasePath), Is.EqualTo(new[] { BasePath, OutfitPath }));
        Assert.That(baseModels.Carries(models[PanelPath], scene[PanelPath]), Is.False);
        Assert.That(baseModels.Carries(models[BasePath], scene[BasePath]), Is.True);
    }

    [Test]
    public void OutfitExportedWithTheWholeArmatureComesAfterTheModelSkinnedToTheAvatar()
    {
        var avatar = Avatar();
        Skin(avatar, "Body", BasePath, Chain(avatar.transform, "Armature", Skeleton));
        Skin(avatar, "Outfit/Suit", OutfitPath, Chain(Child(avatar.transform, "Outfit"), "Armature", Skeleton));
        models[BasePath] = Model("Base", Skeleton);
        models[OutfitPath] = Model("Outfit", Skeleton);

        Assert.That(Order(new AvatarBaseModels(avatar.transform), OutfitPath, BasePath), Is.EqualTo(new[] { BasePath, OutfitPath }));
    }

    [Test]
    public void RecordedBaseModelWinsOverAPropWhenAppliedMeshesNoLongerNameIt()
    {
        // An applied version's meshes are payload assets without their source FBX: the base model comes from the models
        // recorded at apply time, after the panel the hierarchy found.
        var avatar = Avatar();
        Skin(avatar, "Body", null, Chain(avatar.transform, "Armature", Skeleton));
        Skin(avatar, "VF_SRanipal_VRCFT/Window/FT_Debug", PanelPath);
        models[BasePath] = Model("Base", Skeleton);
        models[PanelPath] = Model("FT_SRanipal_Debug");

        Assert.That(Order(new AvatarBaseModels(avatar.transform), PanelPath, BasePath), Is.EqualTo(new[] { BasePath }));
    }

    [Test]
    public void AvatarWithoutArmatureKeepsEveryModelInHierarchyOrder()
    {
        var avatar = Avatar();
        Skin(avatar, "Panel", PanelPath);
        Skin(avatar, "Body", BasePath);
        models[PanelPath] = Model("FT_SRanipal_Debug");
        models[BasePath] = Model("Base", Skeleton);

        var baseModels = new AvatarBaseModels(avatar.transform);

        Assert.That(Order(baseModels, PanelPath, BasePath), Is.EqualTo(new[] { PanelPath, BasePath }));
        Assert.That(baseModels.Carries(models[PanelPath], scene[PanelPath]), Is.True);
    }

    [Test]
    public void ModelWithTheSkeletonIsPickedAmongListedModels()
    {
        var avatar = Avatar();
        var bones = Chain(avatar.transform, "Armature", Skeleton);
        var panel = Model("FT_SRanipal_Debug");
        var partial = Model("Hair", "Head");
        var model = Model("Base", Skeleton);

        Assert.That(AvatarBaseModels.WithSkeleton(new[] { panel, null, partial, model }, bones), Is.SameAs(model));
        Assert.That(AvatarBaseModels.WithSkeleton(new[] { panel }, bones), Is.Null);
    }

    private List<string> Order(AvatarBaseModels baseModels, params string[] hierarchyOrder) =>
        baseModels.BaseFirst(hierarchyOrder, path => models.TryGetValue(path, out var m) ? m : null,
            path => scene.TryGetValue(path, out var r) ? r : Enumerable.Empty<SkinnedMeshRenderer>());

    private GameObject Avatar()
    {
        var avatar = new GameObject("Avatar");
        owned.Add(avatar);
        return avatar;
    }

    // Stands in for an imported model: a root with an armature chain of the given bone names and a mesh object.
    private GameObject Model(string name, params string[] bones)
    {
        var model = new GameObject(name);
        owned.Add(model);
        Child(model.transform, name + "_Mesh");
        if (bones.Length > 0) Chain(model.transform, "Armature", bones);
        return model;
    }

    private SkinnedMeshRenderer Skin(GameObject avatar, string path, string modelPath, Transform[] bones = null)
    {
        var parent = avatar.transform;
        foreach (string segment in path.Split('/')) parent = Child(parent, segment);
        var renderer = parent.gameObject.AddComponent<SkinnedMeshRenderer>();
        renderer.bones = bones ?? new Transform[0];
        renderer.rootBone = bones != null && bones.Length > 0 ? bones[0] : null;
        if (modelPath == null) return renderer;
        if (!scene.TryGetValue(modelPath, out var renderers)) scene[modelPath] = renderers = new List<SkinnedMeshRenderer>();
        renderers.Add(renderer);
        return renderer;
    }

    private static Transform[] Chain(Transform parent, string armature, params string[] names)
    {
        var current = Child(parent, armature);
        return names.Select(name => current = Child(current, name)).ToArray();
    }

    private static Transform Child(Transform parent, string name)
    {
        var existing = parent.Find(name);
        if (existing != null) return existing;
        var child = new GameObject(name).transform;
        child.SetParent(parent, false);
        return child;
    }
}
