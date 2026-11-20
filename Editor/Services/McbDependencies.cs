#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using Orbiters.Toolkit.Editor.Vpm;
using UnityEditor;

/// <summary>MCB's VPM dependencies (declared in its package.json) and the local-development bypass for ReFit and Mixer.</summary>
public static class McbDependencies
{
    public const string PackageId = "orbiters.mcb";
    public const string ReFitPackageId = "orbiters.refit";
    public const string MixerPackageId = "orbiters.mixer";
    public const string PoiyomiPackageId = "com.poiyomi.toon";
    private const string AssumeLocalPrefKey = "MCB.AssumeLocalOptionalIntegrationsInstalled";

    public static VpmDependencies Vpm => VpmDependencies.For(PackageId);

    [InitializeOnLoadMethod]
    private static void Configure() => Vpm.AssumeOptionalInstalled = IsOptionalDependencyAssumedInstalled;

    /// <summary>Treat ReFit and Mixer as installed, for developing them as local packages outside VPM.</summary>
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

    /// <summary>
    /// Whether the project already has Poiyomi in some form: any version of its VCC package (10 and later keep their editor
    /// in a package of its own), Poiyomi Pro, or the files of a .unitypackage under Assets. Installing another Poiyomi
    /// beside any of them can give the project a second copy of its editor (two ThryAssemblyDefinition and ThryExternal
    /// assemblies: Poiyomi Toon 9 beside 10 did), and nothing compiles any more.
    /// </summary>
    public static bool HasPoiyomi() => HasPoiyomi(ShaderUtil.GetAllShaderInfo().Select(s => s.name),
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name));

    internal static bool HasPoiyomi(IEnumerable<string> shaderNames, IEnumerable<string> assemblyNames) =>
        shaderNames.Any(n => n != null && n.StartsWith(".poiyomi/", StringComparison.OrdinalIgnoreCase))
        || assemblyNames.Any(n => n == "ThryAssemblyDefinition" || n == "ThryExternal");

    public static bool IsOptionalDependencyAssumedInstalled(string packageId) =>
        AssumeLocalOptionalIntegrationsInstalled &&
        (string.Equals(packageId, ReFitPackageId, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(packageId, MixerPackageId, StringComparison.OrdinalIgnoreCase));
}
#endif
