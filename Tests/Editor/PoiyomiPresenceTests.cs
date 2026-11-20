using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Orbiters.Toolkit.Editor.Vpm;

// The custom veins card offered to install Poiyomi Toon 9 in a project that had Poiyomi Toon 10: the newer one keeps its
// editor in a package of its own, so the project ended with two copies of it and compiled nothing (a buyer's report,
// 2026-11-20). The offer is for a project without Poiyomi only, and it installs Poiyomi Toon 10 with that editor.
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

    // Poiyomi's listing as it stood on 2026-11-20, in a project that has none of it.
    private sealed class EmptyProject : IVpmCatalog
    {
        private readonly List<VpmCandidate> offered = new List<VpmCandidate>();
        public EmptyProject Offer(string id, string version, params (string id, string range)[] needs)
        {
            offered.Add(new VpmCandidate { Id = id, Version = version, DisplayName = id, RepositoryUrl = "https://poiyomi.github.io/vpm/index.json",
                Dependencies = needs.ToDictionary(n => n.id, n => n.range) });
            return this;
        }
        public string Installed(string id) => null;
        public bool Developed(string id) => false;
        public IEnumerable<VpmCandidate> Versions(string id) => VpmCandidates.Newest(offered.Where(c => c.Id == id));
        public string DisplayName(string id) => id;
        public IReadOnlyDictionary<string, string> InstalledDependencies(string id) => new Dictionary<string, string>();
        public IEnumerable<string> InstalledPackages() => Enumerable.Empty<string>();
    }

    [Test] public void TheOfferInstallsPoiyomiToon10WithItsEditor()
    {
        var listing = new EmptyProject()
            .Offer("com.poiyomi.toon", "9.2.71").Offer("com.poiyomi.toon", "9.3.64")
            .Offer("com.poiyomi.toon", "10.0.28", ("com.poiyomi.thryeditor", ">=2.74.3"))
            .Offer("com.poiyomi.toon", "10.0.29", ("com.poiyomi.thryeditor", ">=2.74.3"))
            .Offer("com.poiyomi.thryeditor", "2.74.2").Offer("com.poiyomi.thryeditor", "2.74.4")
            .Offer("com.poiyomi.pro", "11.0.50");
        string range = McbDependencies.Vpm.OptionalStatus(McbDependencies.PoiyomiPackageId).VersionRange;

        var plan = VpmDependencyPlan.Resolve(new[] { new VpmDependencyPlan.Requirement(McbDependencies.PoiyomiPackageId, range, "MCB") }, listing);

        Assert.That(plan.CanApply, Is.True, plan.Blocker?.Reason);
        Assert.That(plan.Changes.Select(s => s.Action + " " + s.Id + " " + s.To),
            Is.EquivalentTo(new[] { "Install com.poiyomi.toon 10.0.29", "Install com.poiyomi.thryeditor 2.74.4" }));
    }
}
