using System.Collections.Generic;
using NUnit.Framework;

public sealed class ExtraCustomizationUtilsTests
{
    [Test]
    public void GetFlagsReturnsEveryStringFlagAndIgnoresConfigurationObjects()
    {
        var entries = new object[]
        {
            "advancedMeshReplacement",
            "dynamicNormalBody",
            new Dictionary<string, object>
            {
                ["suggestRealistic"] = new[] { "Body" }
            },
            " customVeins "
        };

        Assert.That(
            ExtraCustomizationUtils.GetFlags(entries),
            Is.EqualTo(new[]
            {
                "advancedMeshReplacement",
                "dynamicNormalBody",
                "customVeins"
            }));
    }

    [Test]
    public void GetFlagsDropsBlankAndCaseInsensitiveDuplicates()
    {
        var entries = new List<object>
        {
            "advancedMeshReplacement",
            "ADVANCEDMESHREPLACEMENT",
            " ",
            null
        };

        Assert.That(
            ExtraCustomizationUtils.GetFlags(entries),
            Is.EqualTo(new[] { "advancedMeshReplacement" }));
    }
}
