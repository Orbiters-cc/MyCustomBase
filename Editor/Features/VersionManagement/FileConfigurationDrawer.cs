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
            actions.UpdateCurrentBaseFbxHash();
            actions.StartVersionFetch();
            editor.Repaint();
        }

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    private void AutoDetectBaseFbxViaHierarchy()
    {
        var root = editor.customBaseTarget.transform.root;
        var bodySmr = MeshFinder.FindMeshPrioritizingRoot(root, "Body");
        
        if (bodySmr?.sharedMesh == null) return;
        
        string meshPath = AssetDatabase.GetAssetPath(bodySmr.sharedMesh);
        if (string.IsNullOrEmpty(meshPath)) return;
        
        var fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
        if (fbxAsset != null && AssetImporter.GetAtPath(meshPath) is ModelImporter)
        {
            editor.baseFbxFilesProp.ClearArray();
            editor.baseFbxFilesProp.InsertArrayElementAtIndex(0);
            editor.baseFbxFilesProp.GetArrayElementAtIndex(0).objectReferenceValue = fbxAsset;
            editor.serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif