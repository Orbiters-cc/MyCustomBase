#if UNITY_EDITOR
using System;
using Orbiters.Toolkit.Editor.Vpm;
using UnityEditor;

/// <summary>MCB's VPM dependencies (declared in its package.json) and the local-development bypass for ReFit/Unit Git.</summary>
public static class McbDependencies
{
    public const string PackageId = "orbiters.mcb";
    public const string ReFitPackageId = "orbiters.refit";
    public const string UnitGitPackageId = "orbiters.unitgit";
    public const string PoiyomiPackageId = "com.poiyomi.toon";
    private const string AssumeLocalPrefKey = "MCB.AssumeLocalOptionalIntegrationsInstalled";

    public static VpmDependencies Vpm => VpmDependencies.For(PackageId);

    [InitializeOnLoadMethod]
    private static void Configure() => Vpm.AssumeOptionalInstalled = IsOptionalDependencyAssumedInstalled;

    /// <summary>Treat ReFit and Unit Git as installed, for developing them as local packages outside VPM.</summary>
    public static bool AssumeLocalOptionalIntegrationsInstalled
    {
        get => EditorPrefs.GetBool(AssumeLocalPrefKey, false);
        set
        {
            if (value == AssumeLocalOptionalIntegrationsInstalled) return;
            EditorPrefs.SetBool(AssumeLocalPrefKey, value);
            VpmDependencies.Invalidate();
        }
    }

    public static bool IsOptionalDependencyAssumedInstalled(string packageId) =>
        AssumeLocalOptionalIntegrationsInstalled &&
        (string.Equals(packageId, ReFitPackageId, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(packageId, UnitGitPackageId, StringComparison.OrdinalIgnoreCase));
}
#endif
