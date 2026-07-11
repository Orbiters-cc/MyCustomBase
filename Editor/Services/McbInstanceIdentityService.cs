#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class McbInstanceIdentityService
{
    private const string InstallationIdKey = "Orbiters.MCB.InstallationId";
    private const string ProjectIdPath = "ProjectSettings/OrbitersMCBProjectId.txt";
    private const string WorkspaceIdPath = "Library/OrbitersMCB/workspace-id";

    public static string InstallationId => GetOrCreateEditorPreferenceId(InstallationIdKey);
    public static string ProjectId => GetOrCreateFileId(ProjectIdPath);
    public static string WorkspaceId => GetOrCreateFileId(WorkspaceIdPath);

    public static bool EnsureIdentity(MyCustomBase target)
    {
        if (target == null) return false;

        bool changed = EnsureClientId(ref target.mcbInstanceId);
        changed |= EnsureClientId(ref target.mcbComponentId);
        if (changed) EditorUtility.SetDirty(target);

        RotateDuplicateComponentIds(target.mcbComponentId);
        return changed;
    }

    public static void RotateComponentId(MyCustomBase target)
    {
        if (target == null) return;
        target.mcbComponentId = NewClientId();
        EditorUtility.SetDirty(target);
    }

    public static bool IsClientId(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length == 32 &&
               value.All(character => Uri.IsHexDigit(character));
    }

    private static void RotateDuplicateComponentIds(string componentId)
    {
        if (!IsClientId(componentId)) return;

        var duplicates = Resources.FindObjectsOfTypeAll<MyCustomBase>()
            .Where(candidate => candidate != null &&
                                candidate.gameObject.scene.IsValid() &&
                                string.Equals(candidate.mcbComponentId, componentId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => GlobalObjectId.GetGlobalObjectIdSlow(candidate).ToString(), StringComparer.Ordinal)
            .ToList();
        for (int index = 1; index < duplicates.Count; index++)
        {
            duplicates[index].mcbComponentId = NewClientId();
            EditorUtility.SetDirty(duplicates[index]);
        }
    }

    private static bool EnsureClientId(ref string value)
    {
        if (IsClientId(value))
        {
            string normalized = value.ToLowerInvariant();
            bool changed = !string.Equals(value, normalized, StringComparison.Ordinal);
            value = normalized;
            return changed;
        }

        value = NewClientId();
        return true;
    }

    private static string GetOrCreateEditorPreferenceId(string key)
    {
        string value = EditorPrefs.GetString(key, string.Empty);
        if (IsClientId(value)) return value.ToLowerInvariant();

        value = NewClientId();
        EditorPrefs.SetString(key, value);
        return value;
    }

    private static string GetOrCreateFileId(string relativePath)
    {
        string fullPath = Path.GetFullPath(relativePath);
        if (File.Exists(fullPath))
        {
            string existing = File.ReadAllText(fullPath).Trim();
            if (IsClientId(existing)) return existing.ToLowerInvariant();
        }

        string value = NewClientId();
        string directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, value);
        if (File.Exists(fullPath)) File.Delete(fullPath);
        File.Move(temporaryPath, fullPath);
        return value;
    }

    private static string NewClientId() => Guid.NewGuid().ToString("N");
}
#endif
