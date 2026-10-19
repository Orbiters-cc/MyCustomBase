#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine.UIElements;

/// <summary>
/// The Blender card of MCB's creator mode and what optional packages add to it: Orbiters Mixer registers the live part
/// (the link's state, Open scene in Blender, the base's meshes as Blender or Mixer edits them) when it is installed.
/// </summary>
public static class McbBlenderCard
{
    /// <summary>Builds Mixer's part of the card into the given panel; null while Orbiters Mixer is not installed.</summary>
    public static Action<VisualElement, MCBEditor, Action> Mixer;

    /// <summary>Shows a message under the card and logs it.</summary>
    public static void Say(string message, MessageType type) => BlenderExportImport.SetStatus(message, type);
}
#endif
