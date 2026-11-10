using System.IO;
using System.Linq;
using NUnit.Framework;

// The version switch finds the avatar through AvatarPaths.Root (its descriptor, then the scene root). A view that used
// transform.root worked on another object than the switch for an avatar under an organiser object (MCBF-14).
public sealed class AvatarRootUsageTests
{
    [Test]
    public void TheInspectorAndItsFeaturesFindTheAvatarThroughAvatarPaths()
    {
        string editor = Path.GetFullPath("Packages/orbiters.mcb/Editor");
        var files = Directory.GetFiles(Path.Combine(editor, "Features"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(editor, "components"), "*.cs"))
            .Append(Path.Combine(editor, "MCBEditor.cs"));
        var offenders = files.Where(file => File.ReadAllText(file).Contains("transform.root"))
            .Select(file => file.Substring(editor.Length + 1)).ToList();
        Assert.That(offenders, Is.Empty);
    }
}
