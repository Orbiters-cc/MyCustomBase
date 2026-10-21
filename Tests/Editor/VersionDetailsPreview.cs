using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// A version as the version list draws it, expanded (its details with See differences and Export FBX), in MCB's styles:
/// a test window for screenshots of the version row, never opened by MCB itself.
/// </summary>
internal sealed class VersionDetailsPreview : EditorWindow
{
    private static readonly string[] Sheets =
    {
        "Packages/orbiters.mcb/Editor/Styles/mcb-account.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-gallery.uss",
        "Packages/orbiters.toolkit/Editor/UI/gallery-card.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-creator.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-sections.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-version.uss",
        "Packages/orbiters.mcb/Editor/Styles/mcb-avatar-options.uss"
    };

    public void CreateGUI()
    {
        var root = rootVisualElement;
        foreach (string path in Sheets)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet != null) root.styleSheets.Add(sheet);
        }
        root.style.backgroundColor = new Color(0.19f, 0.19f, 0.19f);
        root.style.paddingLeft = root.style.paddingRight = 16;
        root.style.paddingTop = 16;

        var version = new CustomBaseVersion
        {
            version = "1.4.0",
            changelog = "Smoother shoulders and a new jaw shape.",
            versionFiles = new[] { new ModelFileData { role = "PATCH", transform = ModelFileTransforms.XorBinToFbx, path = "Body.bin" } },
        };
        var drawer = (VersionListDrawer)FormatterServices.GetUninitializedObject(typeof(VersionListDrawer));
        var details = (VisualElement)typeof(VersionListDrawer).GetMethod("CreateVersionDetails", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(drawer, new object[] { version });

        // The list item's frame around it, as an expanded version shows.
        VisualElement Part(VisualElement parent, string className)
        {
            var part = new VisualElement();
            part.AddToClassList(className);
            parent.Add(part);
            return part;
        }
        var item = Part(root, "mcb-version-item");
        item.AddToClassList("mcb-version-item--expanded");
        var shell = Part(Part(Part(item, "mcb-version-item__body"), "mcb-version-item__card"), "mcb-version-item__shell");
        var block = Part(shell, "mcb-version-item__version-block");
        var label = new Label(version.version);
        label.AddToClassList("mcb-version-item__version-label");
        block.Add(label);
        var main = Part(shell, "mcb-version-item__main");
        var title = Part(main, "mcb-version-item__title-row");
        var name = new Label("Shape update");
        name.AddToClassList("mcb-version-item__title");
        title.Add(name);
        main.Add(details);
    }
}
