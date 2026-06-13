#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class HdiffHealthCheck
{
    [MenuItem("Tools/My Custom Base (MCB)/Health Checks/HDiff")]
    public static void RunFromMenu()
    {
        try
        {
            RunOrThrow();
            Debug.Log("[HdiffHealthCheck] Passed.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[HdiffHealthCheck] Failed: " + ex);
        }
    }

    public static void RunOrThrow()
    {
        if (!HdiffService.IsAvailable(out string unavailableReason))
        {
#if UNITY_EDITOR_WIN
            throw new InvalidOperationException("HDiff is unavailable: " + unavailableReason);
#else
            Debug.LogWarning("[HdiffHealthCheck] Skipped: " + unavailableReason);
            return;
#endif
        }

        string folder = Path.Combine(Path.GetTempPath(), "mcb_hdiff_health_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string basePath = Path.Combine(folder, "base.fbx");
            string modifiedPath = Path.Combine(folder, "modified.fbx");
            string hdiffPath = Path.Combine(folder, "patch.hdiff");
            string binPath = Path.Combine(folder, "patch.bin");
            string outputPath = Path.Combine(folder, "output.fbx");

            byte[] baseBytes = BuildDeterministicBytes(1024 * 1024, 17);
            byte[] modifiedBytes = (byte[])baseBytes.Clone();
            for (int i = 8192; i < modifiedBytes.Length; i += 32768)
            {
                modifiedBytes[i] ^= 0x7f;
                modifiedBytes[i + 1] ^= 0x31;
            }

            File.WriteAllBytes(basePath, baseBytes);
            File.WriteAllBytes(modifiedPath, modifiedBytes);

            HdiffDiffResult diffResult = MCBHdiffPatchWrapper.CreateDiff(basePath, modifiedPath, hdiffPath);
            ThrowIf(diffResult != HdiffDiffResult.HDIFF_SUCCESS, "Synthetic HDiff creation failed: " + diffResult);

            var fileManager = new FileManagerService();
            File.WriteAllBytes(binPath, fileManager.XorTransform(baseBytes, File.ReadAllBytes(hdiffPath)));
            HdiffService.ApplyXorEncryptedPatchToTempFbx(basePath, binPath, outputPath, fileManager);

            byte[] outputBytes = File.ReadAllBytes(outputPath);
            ThrowIf(outputBytes.Length != modifiedBytes.Length, "Synthetic HDiff output length mismatch.");
            for (int i = 0; i < outputBytes.Length; i++)
            {
                if (outputBytes[i] != modifiedBytes[i])
                {
                    throw new InvalidDataException("Synthetic HDiff output byte mismatch at " + i + ".");
                }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[HdiffHealthCheck] Failed to delete temp folder '" + folder + "': " + ex.Message);
            }
        }
    }

    private static byte[] BuildDeterministicBytes(int length, int seed)
    {
        var bytes = new byte[length];
        unchecked
        {
            uint state = (uint)seed;
            for (int i = 0; i < bytes.Length; i++)
            {
                state = state * 1664525u + 1013904223u;
                bytes[i] = (byte)(state >> 24);
            }
        }

        return bytes;
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
