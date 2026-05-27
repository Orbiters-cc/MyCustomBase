#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class VersionApplyResetInvariantHealthCheck
{
    [MenuItem("Tools/My Custom Base (MCB)/Health Checks/Version Apply Reset Invariants")]
    public static void RunFromMenu()
    {
        try
        {
            RunOrThrow();
            EditorUtility.DisplayDialog("Version Apply Reset Invariants", "Health check passed.", "OK");
        }
        catch (Exception ex)
        {
            Debug.LogError("[VersionApplyResetInvariantHealthCheck] Failed: " + ex);
            EditorUtility.DisplayDialog("Version Apply Reset Invariants", "Health check failed. See Console for details.", "OK");
        }
    }

    public static void RunOrThrow()
    {
        RunDirectCustomCopyBackupInvariantCheck();
        RunXorPatchBackupInvariantCheck();
        RunResetBackupInvariantCheck();
        RunAdvancedMeshOriginalKeyInvariantCheck();
        RunVersionPathResolutionInvariantCheck();
        RunResetRequiresBackupInvariantCheck();
    }

    private static void RunDirectCustomCopyBackupInvariantCheck()
    {
        string folder = CreateTempFolder();
        try
        {
            string targetPath = Path.Combine(folder, "base.fbx");
            string customBPath = Path.Combine(folder, "custom_b.fbx");
            string customCPath = Path.Combine(folder, "custom_c.fbx");
            byte[] original = BuildDeterministicBytes(64, 17);
            byte[] customB = BuildDeterministicBytes(91, 43);
            byte[] customC = BuildDeterministicBytes(128, 71);

            File.WriteAllBytes(targetPath, original);
            File.WriteAllBytes(customBPath, customB);
            File.WriteAllBytes(customCPath, customC);

            var fileManager = new FileManagerService();
            ThrowIf(!fileManager.ReplaceFbxWithCustomCopy(targetPath, customBPath), "First direct custom copy should report a changed target.");
            AssertFileBytes(targetPath + FileManagerService.OriginalSuffix, original, "Direct custom copy did not preserve the original backup.");
            AssertFileBytes(targetPath, customB, "Direct custom copy did not write the custom FBX.");

            ThrowIf(!fileManager.ReplaceFbxWithCustomCopy(targetPath, customCPath), "Second direct custom copy should report a changed target.");
            AssertFileBytes(targetPath + FileManagerService.OriginalSuffix, original, "Second direct custom copy overwrote the original backup.");
            AssertFileBytes(targetPath, customC, "Second direct custom copy did not write the new custom FBX.");
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    private static void RunXorPatchBackupInvariantCheck()
    {
        string folder = CreateTempFolder();
        try
        {
            string targetPath = Path.Combine(folder, "base.fbx");
            string binBPath = Path.Combine(folder, "patch_b.bin");
            string binCPath = Path.Combine(folder, "patch_c.bin");
            byte[] original = BuildDeterministicBytes(97, 11);
            byte[] versionB = BuildDeterministicBytes(144, 29);
            byte[] versionC = BuildDeterministicBytes(53, 193);
            var fileManager = new FileManagerService();
            var actions = new VersionActions(null, null, fileManager);

            File.WriteAllBytes(targetPath, original);
            File.WriteAllBytes(binBPath, fileManager.XorTransform(original, versionB));
            File.WriteAllBytes(binCPath, fileManager.XorTransform(original, versionC));

            InvokePrivate(actions, "ApplyXorBinToFbx", binBPath, targetPath);
            AssertFileBytes(targetPath + FileManagerService.OriginalSuffix, original, "First XOR apply did not preserve the original backup.");
            AssertFileBytes(targetPath, versionB, "First XOR apply did not produce version B bytes.");

            InvokePrivate(actions, "ApplyXorBinToFbx", binCPath, targetPath);
            AssertFileBytes(targetPath + FileManagerService.OriginalSuffix, original, "Second XOR apply overwrote the original backup.");
            AssertFileBytes(targetPath, versionC, "Second XOR apply did not use the original backup as its XOR key.");
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    private static void RunResetBackupInvariantCheck()
    {
        string folder = CreateTempFolder();
        try
        {
            string targetPath = Path.Combine(folder, "base.fbx");
            string backupPath = targetPath + FileManagerService.OriginalSuffix;
            byte[] original = BuildDeterministicBytes(80, 5);
            byte[] applied = BuildDeterministicBytes(80, 37);
            var fileManager = new FileManagerService();

            File.WriteAllBytes(targetPath, applied);
            File.WriteAllBytes(backupPath, original);

            fileManager.RestoreBackup(targetPath);
            AssertFileBytes(targetPath, original, "Reset restore did not copy the backup over the target FBX.");
            AssertFileBytes(backupPath, original, "Reset restore removed or modified the original backup.");

            File.WriteAllBytes(targetPath, applied);
            fileManager.RestoreBackup(targetPath);
            AssertFileBytes(targetPath, original, "Repeated reset restore did not remain deterministic.");
            AssertFileBytes(backupPath, original, "Repeated reset restore modified the original backup.");
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    private static void RunAdvancedMeshOriginalKeyInvariantCheck()
    {
        string folder = CreateTempFolder();
        try
        {
            string targetPath = Path.Combine(folder, "base.fbx");
            string backupPath = targetPath + FileManagerService.OriginalSuffix;
            byte[] original = BuildDeterministicBytes(24, 101);
            byte[] applied = BuildDeterministicBytes(24, 53);

            File.WriteAllBytes(targetPath, applied);
            File.WriteAllBytes(backupPath, original);

            string resolvedWithBackup = (string)InvokeStaticPrivate("ResolveOriginalFbxKeyPath", targetPath);
            ThrowIf(!PathsEqual(resolvedWithBackup, backupPath), "Advanced mesh key resolution did not prefer .fbx.old when present.");

            File.Delete(backupPath);
            string resolvedWithoutBackup = (string)InvokeStaticPrivate("ResolveOriginalFbxKeyPath", targetPath);
            ThrowIf(!PathsEqual(resolvedWithoutBackup, targetPath), "Advanced mesh key resolution did not fall back to the target FBX when untouched.");
        }
        finally
        {
            DeleteTempFolder(folder);
        }
    }

    private static void RunVersionPathResolutionInvariantCheck()
    {
        const string firstFbx = "Assets/MCB/HealthChecks/BaseA.fbx";
        const string secondFbx = "Assets/MCB/HealthChecks/BaseB.fbx";
        var version = new CustomBaseVersion
        {
            assetId = 42,
            version = "health",
            defaultAviVersion = "1.0.0",
            sourceFiles = new[]
            {
                new ModelFileData { id = 1, path = firstFbx, type = "FBX", role = "SOURCE" },
                new ModelFileData { id = 2, path = secondFbx, type = "FBX", role = "SOURCE" }
            },
            versionFiles = new[]
            {
                new ModelFileData
                {
                    id = 10,
                    path = "first.bin",
                    role = "PATCH",
                    transform = "XOR_BIN_TO_FBX",
                    sourceModelFileId = 1
                },
                new ModelFileData
                {
                    id = 11,
                    path = "second.bin",
                    role = "PATCH",
                    transform = NativeMeshPayloadService.TransformName,
                    metadata = new Dictionary<string, object> { { "sourcePath", secondFbx } }
                },
                new ModelFileData
                {
                    id = 12,
                    path = "direct.asset",
                    role = "PATCH",
                    transform = "DIRECT_ASSET",
                    sourceModelFileId = 2
                }
            }
        };

        var actions = new VersionActions(null, null, new FileManagerService());

        var affected = (List<string>)InvokePrivate(actions, "GetAffectedFbxPaths", version, null);
        AssertSamePaths(affected, new[] { firstFbx, secondFbx }, "Affected FBX paths should be resolved from source file ids and metadata.");

        var importPaths = (List<string>)InvokePrivate(actions, "GetFbxImportPaths", version, null);
        AssertSamePaths(importPaths, new[] { firstFbx }, "Only XOR FBX patches should trigger FBX imports.");

        var resetPaths = (List<string>)InvokePrivate(actions, "GetResetAffectedFbxPaths", version, "Assets/MCB/HealthChecks/Fallback.fbx");
        AssertSamePaths(resetPaths, new[] { firstFbx, secondFbx }, "Reset should prefer version source paths over the fallback FBX.");

        var missingSourcePatch = new ModelFileData
        {
            id = 13,
            path = "missing.bin",
            role = "PATCH",
            transform = "XOR_BIN_TO_FBX"
        };
        AssertThrows<InvalidDataException>(
            () => InvokePrivate(actions, "ResolveTargetFbxPath", version, missingSourcePatch, firstFbx),
            "Patch metadata without a source path should fail instead of silently using the fallback FBX.");
    }

    private static void RunResetRequiresBackupInvariantCheck()
    {
        var version = new CustomBaseVersion
        {
            assetId = 43,
            version = "health",
            defaultAviVersion = "1.0.0",
            sourceFiles = new[]
            {
                new ModelFileData { id = 1, path = "Assets/MCB/HealthChecks/MissingBackup.fbx", type = "FBX", role = "SOURCE" }
            },
            versionFiles = new[]
            {
                new ModelFileData
                {
                    id = 20,
                    path = "missing.bin",
                    role = "PATCH",
                    transform = "XOR_BIN_TO_FBX",
                    sourceModelFileId = 1
                }
            }
        };

        var actions = new VersionActions(null, null, new FileManagerService());
        AssertThrows<FileNotFoundException>(
            () => InvokePrivate(actions, "RestoreBackupsForVersion", version, "Assets/MCB/HealthChecks/Fallback.fbx", true),
            "Reset should fail when a backup is required and no affected .fbx.old file exists.");

        InvokePrivate(actions, "RestoreBackupsForVersion", version, "Assets/MCB/HealthChecks/Fallback.fbx", false);
    }

    private static object InvokePrivate(object target, string methodName, params object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new MissingMethodException(target.GetType().FullName, methodName);
        }

        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static object InvokeStaticPrivate(string methodName, params object[] args)
    {
        MethodInfo method = typeof(VersionActions).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        if (method == null)
        {
            throw new MissingMethodException(typeof(VersionActions).FullName, methodName);
        }

        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            throw ex.InnerException ?? ex;
        }
    }

    private static void AssertThrows<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(message + " Unexpected exception type: " + ex.GetType().Name, ex);
        }

        throw new InvalidOperationException(message);
    }

    private static void AssertSamePaths(IReadOnlyList<string> actual, IReadOnlyList<string> expected, string message)
    {
        ThrowIf(actual == null, message + " Actual path list was null.");
        ThrowIf(actual.Count != expected.Count, message + $" Expected {expected.Count} paths, got {actual.Count}.");

        for (int i = 0; i < expected.Count; i++)
        {
            ThrowIf(!PathsEqual(actual[i], expected[i]), message + $" Expected '{expected[i]}' at index {i}, got '{actual[i]}'.");
        }
    }

    private static void AssertFileBytes(string path, byte[] expected, string message)
    {
        ThrowIf(!File.Exists(path), message + " Missing file: " + path);
        byte[] actual = File.ReadAllBytes(path);
        ThrowIf(!actual.SequenceEqual(expected), message);
    }

    private static bool PathsEqual(string first, string second)
    {
        return string.Equals(
            MCBUtils.ToUnityPath(first),
            MCBUtils.ToUnityPath(second),
            StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildDeterministicBytes(int length, int seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((seed + i * 37 + (i % 11) * 13) % 251);
        }

        return bytes;
    }

    private static string CreateTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcb_version_invariant_health_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void DeleteTempFolder(string folder)
    {
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }

    private static void ThrowIf(bool condition, string message)
    {
        if (condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
#endif
