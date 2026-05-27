#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEngine;

public static class MCBEditorHealthChecks
{
    [MenuItem("Tools/My Custom Base (MCB)/Health Checks/All Deterministic")]
    public static void RunAllFromMenu()
    {
        try
        {
            RunAllOrThrow();
            EditorUtility.DisplayDialog("MCB Health Checks", "All deterministic health checks passed.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("[MCBEditorHealthChecks] Failed: " + ex);
            EditorUtility.DisplayDialog("MCB Health Checks", "Health checks failed. See Console for details.", "OK");
        }
    }

    public static void RunAllOrThrow()
    {
        NativeMeshPayloadHealthCheck.RunOrThrow();
        VersionApplyResetInvariantHealthCheck.RunOrThrow();
    }
}
#endif
