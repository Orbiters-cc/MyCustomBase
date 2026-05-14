#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

public partial class AvatarOptionsModule
{
    private readonly MCBEditor editor;
    private readonly CustomVeinsDrawer customVeinsDrawer;
    private readonly SlidersDrawer slidersDrawer;
    private readonly BlendshapeDrawer blendshapeDrawer;

    public AvatarOptionsModule(MCBEditor editor)
    {
        this.editor = editor;
        customVeinsDrawer = new CustomVeinsDrawer(editor);
        slidersDrawer = new SlidersDrawer(editor);
        blendshapeDrawer = new BlendshapeDrawer(editor, slidersDrawer.RequestApplyDebounced);
    }

    public void Draw()
    {
        // Draw CustomVeinsDrawer first
        customVeinsDrawer.Draw();

        EditorGUILayout.Space(15);
        slidersDrawer.Draw();
        EditorGUILayout.Space(15);
        
        // Then draw BlendshapeDrawer
        blendshapeDrawer.Draw();
    }

    public void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        slidersDrawer.OnPlayModeStateChanged(state);
    }
}
#endif
