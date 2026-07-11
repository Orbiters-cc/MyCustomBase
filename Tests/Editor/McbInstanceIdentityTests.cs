#if UNITY_EDITOR
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class McbInstanceIdentityTests
{
    [Test]
    public void EnsureIdentityCreatesValidLineageAndComponentIds()
    {
        var avatar = new GameObject("MCB identity test");
        try
        {
            var target = avatar.AddComponent<MyCustomBase>();
            target.mcbInstanceId = "invalid";
            target.mcbComponentId = string.Empty;

            McbInstanceIdentityService.EnsureIdentity(target);

            Assert.That(McbInstanceIdentityService.IsClientId(target.mcbInstanceId), Is.True);
            Assert.That(McbInstanceIdentityService.IsClientId(target.mcbComponentId), Is.True);
            Assert.That(target.mcbInstanceId, Is.Not.EqualTo(target.mcbComponentId));
        }
        finally
        {
            Object.DestroyImmediate(avatar);
        }
    }

    [Test]
    public void EnsureIdentityRotatesDuplicateComponentCopyButPreservesLineageAndClaim()
    {
        var firstAvatar = new GameObject("MCB first identity test");
        var copiedAvatar = new GameObject("MCB copied identity test");
        try
        {
            var first = firstAvatar.AddComponent<MyCustomBase>();
            McbInstanceIdentityService.EnsureIdentity(first);
            var copied = copiedAvatar.AddComponent<MyCustomBase>();
            copied.mcbInstanceId = first.mcbInstanceId;
            copied.mcbComponentId = first.mcbComponentId;
            copied.mcbServerInstanceId = "0ca6f357-125d-4ea6-b27d-03328c1f0937";

            McbInstanceIdentityService.EnsureIdentity(first);

            Assert.That(copied.mcbInstanceId, Is.EqualTo(first.mcbInstanceId));
            Assert.That(copied.mcbComponentId, Is.Not.EqualTo(first.mcbComponentId));
            Assert.That(copied.mcbServerInstanceId, Is.EqualTo("0ca6f357-125d-4ea6-b27d-03328c1f0937"));
        }
        finally
        {
            Object.DestroyImmediate(firstAvatar);
            Object.DestroyImmediate(copiedAvatar);
        }
    }

    [Test]
    public void RecoveryValidationRejectsIncompleteHistory()
    {
        var avatar = new GameObject("MCB recovery validation test");
        try
        {
            var target = avatar.AddComponent<MyCustomBase>();
            var sources = new[]
            {
                new ModelFileData
                {
                    id = 1,
                    path = "Assets/Base/body.fbx",
                    hash = new string('a', 64),
                    type = "FBX",
                    role = "SOURCE"
                }
            };

            List<McbInstanceHistoryClient.BindingPayload> result = McbInstanceHistoryClient.ValidateCompleteRecovery(
                target,
                sources,
                new[] { "Assets/Avatar/body.fbx" },
                new McbInstanceHistoryClient.RecoverySuggestion[0]);

            Assert.That(result, Is.Empty);
        }
        finally
        {
            Object.DestroyImmediate(avatar);
        }
    }
}
#endif
