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

        panel.Add(new OrbitersSignInElement(() =>
        {
            editor.CheckAuthentication();
            RefreshUIToolkit();
            editor.Repaint();
        }, "Connect your Orbiters account to publish and download your bases.", "mcb"));
    }

    // Authentication logic moved to AuthenticationService. This class now only provides UI helpers.
}
#endif
