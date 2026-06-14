#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

// Handles the UI and logic for user authentication.
public class AuthenticationModule
{
    private readonly MCBEditor editor;
    private VisualElement authRoot;

    public AuthenticationModule(MCBEditor editor)
    {
        this.editor = editor;
    }

    public void AttachUIToolkit(VisualElement root)
    {
        authRoot = root;
        RefreshUIToolkit();
    }

    public void DetachUIToolkit()
    {
        authRoot = null;
    }

    public void RefreshUIToolkit()
    {
        if (authRoot == null)
        {
            return;
        }

        authRoot.Clear();
        authRoot.AddToClassList("mcb-auth-host");

        if (editor == null || editor.isAuthenticated)
        {
            authRoot.style.display = DisplayStyle.None;
            return;
        }

        authRoot.style.display = DisplayStyle.Flex;

        var panel = new VisualElement();
        panel.AddToClassList("mcb-auth-panel");
        panel.AddToClassList("mcb-form-card");
        authRoot.Add(panel);

        var buttonRow = new VisualElement();
        buttonRow.AddToClassList("mcb-auth-panel__actions");
        panel.Add(buttonRow);

        var button = AvatarOptionsModule.CreateOptionButton("Magic Sync", StartMagicSync);
        button.AddToClassList("mcb-button--primary");
        button.AddToClassList("mcb-auth-panel__button");
        buttonRow.Add(button);

        panel.Add(AvatarOptionsModule.CreateOptionHelpBox(
            "Use Magic Sync to authenticate this tool. Go to the Orbiters website, click 'Magic Sync' to copy your token, then click the button above.",
            HelpBoxMessageType.Info));
    }

    private void StartMagicSync()
    {
        AuthenticationService.RegisterAuth().ContinueWith(task =>
        {
            // Queue the result to be processed on the main thread
            EditorApplication.delayCall += () =>
            {
                if (task.Result)
                {
                    editor.CheckAuthentication(); // Update state in the main editor
                    RefreshUIToolkit();
                    editor.Repaint();
                }
                else
                {
                    EditorUtility.DisplayDialog("Authentication Failed", "Please visit the Orbiters website and click 'Magic Sync' first.", "OK");
                }
            };
        });
    }

    // Authentication logic moved to AuthenticationService. This class now only provides UI helpers.
}
#endif
