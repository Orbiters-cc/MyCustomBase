#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

// About MCB: its version and license, then the third-party software it ships with and their licenses. The list is read
// from THIRD_PARTY_NOTICES.md, the file shipped next to the native libraries, so the window and the notices never differ.
public sealed class MCBAboutWindow : EditorWindow
{
    internal const string NoticesPath = "Packages/orbiters.mcb/Editor/Plugins/Hdiff/THIRD_PARTY_NOTICES.md";
    private const string LicensePath = "Packages/orbiters.mcb/LICENSE.md";

    internal sealed class Notice
    {
        public string title, intro, url, license, text;
    }

    public static void Open()
    {
        var window = GetWindow<MCBAboutWindow>(true, "About My Custom Base", true);
        window.minSize = new Vector2(420f, 460f);
        window.Show();
    }

    public void CreateGUI()
    {
        var root = rootVisualElement;
        foreach (var path in new[] { "Packages/orbiters.toolkit/Runtime/EditorServices/theme.uss", "Packages/orbiters.mcb/Editor/Styles/mcb-about.uss" })
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (sheet) root.styleSheets.Add(sheet);
        }
        root.AddToClassList("mcb-about");

        var scrollView = new ScrollView(); scrollView.AddToClassList("mcb-about__scroll"); root.Add(scrollView);
        var scroll = new VisualElement(); scroll.AddToClassList("mcb-about__content"); scrollView.Add(scroll);

        var header = new VisualElement(); header.AddToClassList("mcb-about__header"); scroll.Add(header);
        var logo = new MCBLogoElement(drawLogo: true, drawGlows: false); logo.AddToClassList("mcb-about__logo"); header.Add(logo);
        var name = new Label("My Custom Base (MCB)"); name.AddToClassList("mcb-about__name"); header.Add(name);
        string version = PackageInfo.FindForAssembly(typeof(MyCustomBase).Assembly)?.version;
        var meta = new Label((string.IsNullOrEmpty(version) ? "" : "Version " + version + " · ") + "by Enzo DUTRA / blackorbit");
        meta.AddToClassList("mcb-about__meta"); header.Add(meta);
        var license = Button("License", "Open MCB's license.", () => OpenFile(LicensePath));
        license.AddToClassList("mcb-about__license"); header.Add(license);

        var section = new Label("Third-party software"); section.AddToClassList("mcb-about__section"); scroll.Add(section);
        var caption = new Label("MCB ships with these libraries. Click one to read its license.");
        caption.AddToClassList("mcb-about__caption"); scroll.Add(caption);

        var notices = Read(AssetDatabase.LoadAssetAtPath<TextAsset>(NoticesPath)?.text);
        if (notices.Count == 0)
        {
            var missing = new Label("The third-party notices file is missing from this installation.");
            missing.AddToClassList("mcb-about__caption"); scroll.Add(missing);
        }
        foreach (var notice in notices) scroll.Add(Card(notice));

        var footer = new VisualElement(); footer.AddToClassList("mcb-about__footer"); scroll.Add(footer);
        footer.Add(Button("Open notices file", "Open THIRD_PARTY_NOTICES.md.", () => OpenFile(NoticesPath)));
    }

    // One card per library: name, what MCB uses it for, a licence chip; the full text slides open below.
    private static VisualElement Card(Notice notice)
    {
        var card = new VisualElement(); card.AddToClassList("mcb-about__card");
        var head = new VisualElement(); head.AddToClassList("mcb-about__card-head"); card.Add(head);
        var texts = new VisualElement(); texts.AddToClassList("mcb-about__card-texts"); head.Add(texts);
        var title = new Label(notice.title); title.AddToClassList("mcb-about__card-title"); texts.Add(title);
        if (!string.IsNullOrEmpty(notice.intro)) { var intro = new Label(notice.intro); intro.AddToClassList("mcb-about__card-intro"); texts.Add(intro); }
        var chip = new Label(notice.license); chip.AddToClassList("mcb-about__chip"); head.Add(chip);
        if (!string.IsNullOrEmpty(notice.url))
        {
            var link = Button("↗", notice.url, () => Application.OpenURL(notice.url));
            link.AddToClassList("mcb-about__link"); head.Add(link);
        }
        if (string.IsNullOrEmpty(notice.text)) return card;

        // Paragraphs reflow to the window width; the file keeps the licence's original line breaks.
        string reflowed = Regex.Replace(Regex.Replace(notice.text, @"(?<!\n)\n(?!\n)", " "), @"[ \t]{2,}", " ");
        var body = new Label(reflowed); body.AddToClassList("mcb-about__license-text");
        body.selection.isSelectable = true;
        card.Add(body);
        body.style.display = DisplayStyle.None;
        // Opens on press, and the text fades in.
        head.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button != 0 || evt.target is Button) return;
            bool open = !card.ClassListContains("mcb-about__card--open");
            card.EnableInClassList("mcb-about__card--open", open);
            body.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            body.RemoveFromClassList("mcb-about__license-text--shown");
            if (open) body.schedule.Execute(() => body.AddToClassList("mcb-about__license-text--shown"));
        });
        return card;
    }

    // "## Title", an intro paragraph (its last line may be the project URL), then the licence in a ``` block.
    internal static List<Notice> Read(string markdown)
    {
        var notices = new List<Notice>();
        if (string.IsNullOrEmpty(markdown)) return notices;
        var parts = Regex.Split(markdown.Replace("\r\n", "\n"), @"^## ", RegexOptions.Multiline);
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            int newline = part.IndexOf('\n');
            var notice = new Notice { title = (newline < 0 ? part : part.Substring(0, newline)).Trim() };
            string rest = newline < 0 ? "" : part.Substring(newline + 1);
            var code = Regex.Match(rest, "```\\n(.*?)\\n```", RegexOptions.Singleline);
            notice.text = code.Success ? code.Groups[1].Value.Trim('\n') : null;
            var introLines = new List<string>();
            foreach (string line in (code.Success ? rest.Substring(0, code.Index) : rest).Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase)) notice.url = trimmed;
                else if (trimmed.Length > 0) introLines.Add(trimmed.Replace("`", ""));
            }
            notice.intro = string.Join(" ", introLines);
            notice.license = LicenseName(notice.text, notice.intro);
            notices.Add(notice);
        }
        return notices;
    }

    private static string LicenseName(string text, string intro)
    {
        string all = (text ?? "") + " " + (intro ?? "");
        if (all.IndexOf("public domain", StringComparison.OrdinalIgnoreCase) >= 0) return "Public domain";
        if (all.IndexOf("MIT License", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf("Permission is hereby granted, free of charge", StringComparison.Ordinal) >= 0) return "MIT";
        if (all.IndexOf("BSD", StringComparison.Ordinal) >= 0 || all.IndexOf("Redistribution and use in source and binary forms", StringComparison.Ordinal) >= 0) return "BSD";
        if (all.IndexOf("provided 'as-is'", StringComparison.OrdinalIgnoreCase) >= 0) return "zlib";
        return "License";
    }

    private static void OpenFile(string assetPath)
    {
        var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (asset != null) AssetDatabase.OpenAsset(asset);
        else EditorUtility.RevealInFinder(Path.GetFullPath(assetPath));
    }

    private static Button Button(string text, string tooltip, Action action)
    {
        var button = new Button(action) { text = text, tooltip = tooltip };
        button.AddToClassList("mcb-about__button");
        // Immediate feedback on press; the click itself still follows Unity's normal release behaviour.
        button.RegisterCallback<PointerDownEvent>(_ => button.AddToClassList("mcb-about__button--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerUpEvent>(_ => button.RemoveFromClassList("mcb-about__button--pressed"), TrickleDown.TrickleDown);
        button.RegisterCallback<PointerLeaveEvent>(_ => button.RemoveFromClassList("mcb-about__button--pressed"));
        return button;
    }
}
#endif
