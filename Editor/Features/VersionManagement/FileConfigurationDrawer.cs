#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using MCBEditorUtils;

public class FileConfigurationDrawer
{
    private readonly MCBEditor editor;
    private readonly VersionActions actions;

    public FileConfigurationDrawer(MCBEditor editor, VersionActions actions)
    {
        this.editor = editor;
        this.actions = actions;
    }

    public void OnEnable()
    {
        // If we are already in the middle of a build/submit or another fetch, do NOT start a new one.
        // This prevents the re-import loop.
        if (editor.isSubmitting || editor.isFetching)
        {
            actions.UpdateCurrentBaseFbxHash(); // Still useful to update the hash state
            return;
        }

        if (!editor.specifyCustomBaseFbxProp.boolValue)
        {
            AutoDetectBaseFbxViaHierarchy();
        }
        actions.UpdateCurrentBaseFbxHash();
        
        // Only fetch if we have a hash and haven't tried yet.
        if(!string.IsNullOrEmpty(editor.currentBaseFbxHash) && !editor.fetchAttempted)
        {
            actions.StartVersionFetch();
        }
    }

    public void Draw()
    {
        var previousSources = editor.customBaseTarget.baseFbxFiles.ToList();
        var previousEntries = editor.customBaseTarget.modelFileBuildEntries.ToList();
        EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUI.BeginChangeCheck();
        EditorGUILayout.PropertyField(editor.specifyCustomBaseFbxProp, new GUIContent("Specify Base FBX Manually"));
        bool fbxSpecChanged = EditorGUI.EndChangeCheck();

        bool fbxFieldChanged = false;
        if (editor.specifyCustomBaseFbxProp.boolValue)
        {
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(editor.baseFbxFilesProp, new GUIContent("Base FBX File(s)"), true);
            fbxFieldChanged = EditorGUI.EndChangeCheck();
        }
        else
        {
            if (fbxSpecChanged) AutoDetectBaseFbxViaHierarchy();
            
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.PropertyField(editor.baseFbxFilesProp, new GUIContent("Detected Base FBX"), true);
            }
        }
        
        if (fbxSpecChanged || fbxFieldChanged)
        {
            editor.serializedObject.ApplyModifiedProperties();
            if (fbxFieldChanged)
            {
                var nextSources = editor.customBaseTarget.baseFbxFiles.ToList();
                editor.customBaseTarget.baseFbxFiles = previousSources;
                editor.customBaseTarget.modelFileBuildEntries = previousEntries;
                FileManagerService.SetCreatorSourceFiles(editor.customBaseTarget, nextSources);
                editor.serializedObject.Update();
            }
            actions.UpdateCurrentBaseFbxHash();
            actions.StartVersionFetch();
            editor.Repaint();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    private void AutoDetectBaseFbxViaHierarchy()
    {
        var detectedPaths = editor.GetDetectedAvatarFbxPaths();
        if (detectedPaths.Count == 0) return;

        var sources = detectedPaths.Where(path => AssetImporter.GetAtPath(path) is ModelImporter)
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(asset => asset != null).ToList();
        if (sources.Count == 0) return;
        editor.serializedObject.ApplyModifiedProperties();
        FileManagerService.SetCreatorSourceFiles(editor.customBaseTarget, sources);
        editor.serializedObject.Update();
    }
}
#endif
