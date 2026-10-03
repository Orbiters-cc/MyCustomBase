using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
using Microsoft.CSharp;
using NUnit.Framework;

public sealed class BlenderAddonCompilationTests
{
    [Test]
    public void McbEditorAssemblyIsExcludedFromPlayerBuilds()
    {
        Assert.That(UnityEditor.Compilation.CompilationPipeline
            .GetAssemblies(UnityEditor.Compilation.AssembliesType.Player)
            .Any(assembly => assembly.name == "mcb.Editor"), Is.False);
        Assert.That(UnityEditor.Compilation.CompilationPipeline
            .GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor)
            .Any(assembly => assembly.name == "mcb.Editor"), Is.True);
    }

    [Test]
    public void BootstrapServiceCompilesWhenEditorCodeIsExcluded()
    {
        // Player compilation still preprocesses excluded C# text. In particular, line-leading
        // Python hash comments inside verbatim strings must not become invalid C# directives.
        var source = File.ReadAllText("Packages/orbiters.mcb/Editor/Services/BlenderAddonService.cs");
        using (var compiler = new CSharpCodeProvider())
        {
            var options = new CompilerParameters { GenerateInMemory = true, GenerateExecutable = false };
            var result = compiler.CompileAssemblyFromSource(options, source);
            Assert.IsFalse(result.Errors.HasErrors,
                string.Join("\n", result.Errors.Cast<CompilerError>().Select(e => e.ToString())));
        }
    }
}
