#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class WarningsModule
{
    public class WarningEntry
    {
        public MessageType type;
        public string title;
        public string message;
    }

    private readonly List<WarningEntry> warnings = new List<WarningEntry>();

    public event System.Action Changed;

    public void AddWarning(string message, MessageType type = MessageType.Warning, string title = null)
    {
        if (string.IsNullOrEmpty(message)) return;
        
        // Avoid duplicates
        foreach (var w in warnings)
        {
            if (w.message == message && w.type == type && w.title == title) return;
        }
        
        warnings.Add(new WarningEntry { message = message, type = type, title = title });
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (warnings.Count == 0) return;
        warnings.Clear();
        Changed?.Invoke();
    }
    
    public void Clear(string message)
    {
        if (warnings.RemoveAll(w => w.message == message) > 0)
        {
            Changed?.Invoke();
        }
    }

    public bool BuildUIToolkit(VisualElement root)
    {
        if (root == null || warnings.Count == 0)
        {
            return false;
        }

        for (int i = warnings.Count - 1; i >= 0; i--)
        {
            int warningIndex = i;
            root.Add(CreateWarningElement(warnings[i], () =>
            {
                if (warningIndex >= 0 && warningIndex < warnings.Count)
                {
                    warnings.RemoveAt(warningIndex);
                    Changed?.Invoke();
                }
            }));
        }

        return true;
    }

    private static VisualElement CreateWarningElement(WarningEntry warning, System.Action dismiss)
    {
        var box = new VisualElement();
        box.AddToClassList("mcb-avatar-helpbox");
        box.AddToClassList(GetHelpBoxClass(warning?.type ?? MessageType.Warning));
        box.AddToClassList("mcb-main-warning");

        var icon = AvatarOptionsModule.CreateOptionLabel(GetIconText(warning?.type ?? MessageType.Warning), 14, FontStyle.Bold, Color.white);
        icon.AddToClassList("mcb-avatar-helpbox__icon");
        box.Add(icon);

        var content = new VisualElement();
        content.AddToClassList("mcb-avatar-helpbox__content");

        if (!string.IsNullOrWhiteSpace(warning?.title))
        {
            var title = AvatarOptionsModule.CreateOptionLabel(warning.title, 12, FontStyle.Bold, Color.white);
            title.AddToClassList("mcb-main-warning__title");
            content.Add(title);
        }

        var message = AvatarOptionsModule.CreateOptionLabel(warning?.message ?? string.Empty, 12, FontStyle.Normal, new Color(0.82f, 0.82f, 0.82f));
        message.AddToClassList("mcb-avatar-helpbox__text");
        content.Add(message);
        box.Add(content);

        var button = AvatarOptionsModule.CreateOptionButton("OK", dismiss);
        button.AddToClassList("mcb-main-warning__dismiss");
        box.Add(button);

        return box;
    }

    private static string GetHelpBoxClass(MessageType messageType)
    {
        switch (messageType)
        {
            case MessageType.Error:
                return "mcb-avatar-helpbox--error";
            case MessageType.Info:
                return "mcb-avatar-helpbox--info";
            case MessageType.None:
                return "mcb-avatar-helpbox--none";
            default:
                return "mcb-avatar-helpbox--warning";
        }
    }

    private static string GetIconText(MessageType messageType)
    {
        switch (messageType)
        {
            case MessageType.Error:
                return "x";
            case MessageType.Info:
                return "i";
            case MessageType.None:
                return string.Empty;
            default:
                return "!";
        }
    }
}
#endif
