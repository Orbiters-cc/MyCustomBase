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
            Debug.Log("[MCBEditorHealthChecks] All deterministic health checks passed.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[MCBEditorHealthChecks] Failed: " + ex);
        }
    }

    public static void RunAllOrThrow()
    {
        NativeMeshPayloadHealthCheck.RunOrThrow();
        VersionApplyResetInvariantHealthCheck.RunOrThrow();
    }
}
#endif
