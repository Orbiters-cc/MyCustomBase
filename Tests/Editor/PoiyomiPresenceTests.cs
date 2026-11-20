using NUnit.Framework;

// The custom veins card offered to install Poiyomi Toon 9 in a project that had Poiyomi Toon 10: the newer one keeps its
// editor in a package of its own, so the project ended with two copies of it and compiled nothing (a buyer's report,
// 2026-11-20). The offer is for a project without Poiyomi only.
public sealed class PoiyomiPresenceTests
{
    private static readonly string[] NoAssemblies = { "mcb.Editor", "UnityEditor" };

    [Test] public void AnyPoiyomiShaderCountsWhateverItsVersionOrPackage()
    {
        foreach (string shader in new[] { ".poiyomi/Poiyomi Toon", ".poiyomi/Poiyomi Pro", ".poiyomi/Old Versions/9.3/Poiyomi Toon" })
            Assert.That(McbDependencies.HasPoiyomi(new[] { "Standard", shader }, NoAssemblies), Is.True, shader);
    }

    [Test] public void ItsEditorAloneCountsToo()
    {
        Assert.That(McbDependencies.HasPoiyomi(new[] { "Standard" }, new[] { "ThryAssemblyDefinition" }), Is.True);
        Assert.That(McbDependencies.HasPoiyomi(new[] { "Standard" }, new[] { "ThryExternal" }), Is.True);
    }

    [Test] public void ALockedMaterialsOwnShaderIsNotPoiyomiInTheProject()
    {
        Assert.That(McbDependencies.HasPoiyomi(new[] { "Standard", "Hidden/Locked/.poiyomi/Old Versions/9.3/Poiyomi Toon/abc123" }, NoAssemblies), Is.False);
        Assert.That(McbDependencies.HasPoiyomi(new string[0], new string[0]), Is.False);
    }
}
