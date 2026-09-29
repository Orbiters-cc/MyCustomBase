#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orbiters.Toolkit.Editor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Explicitly invoked integration probe, separate from NUnit: native package import completes on later editor updates.
/// Exports only two owned TextAssets, then imports packages with identical basenames through the real shared queue.
/// </summary>
public static class MCBPackageImportStressRunner
{
    [Serializable]
    public sealed class Result
    {
        public string runId, stage, startedUtc, finishedUtc, assetRoot, tempRoot, packageName, error;
        public bool importsQueuedTogether, secondWaitedForFirstCompletion, firstContentVerified, secondContentVerified;
        public bool firstPackagePreflightPassed, secondPackagePreflightPassed;
        public bool cleanupComplete, controllerAlive;
        public int nativeCompletions;
        public List<string> events = new List<string>();
        public List<string> rawNativeCompletions = new List<string>();
    }

    private static Task running;
    private static Result current;
    private static string StatusPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "MCB", "ImportQueueStress", "last-run.json");

    /// <summary>Call on the editor thread, then poll Status(). Does not open/focus any window.</summary>
    public static string Begin()
    {
        if (running != null && !running.IsCompleted) return Status();
        var previous = LoadResult();
        if (previous != null && !previous.cleanupComplete)
            throw new InvalidOperationException("The previous import probe still owns artifacts. Read Status() and clean it up before starting another probe.");
        string id = Guid.NewGuid().ToString("N");
        current = new Result
        {
            runId = id, stage = "preparing", startedUtc = DateTime.UtcNow.ToString("O"),
            assetRoot = "Assets/MCB_ImportQueueStress_" + id,
            tempRoot = Path.Combine(Path.GetTempPath(), "MCB_ImportQueueStress_" + id),
            packageName = "MCBImportQueueProbe_" + id
        };
        Save(current);
        running = RunAsync(current);
        return Status();
    }

    /// <summary>Durable status survives assembly reload; an active stage with controllerAlive=false was interrupted.</summary>
    public static string Status()
    {
        var result = LoadResult();
        if (result == null) return "{\"stage\":\"not-started\"}";
        result.controllerAlive = running != null && !running.IsCompleted;
        return JsonUtility.ToJson(result, true);
    }

    /// <summary>
    /// Explicit recovery after interruption, only after Unity's native importer is known to have finished.
    /// Root must not call this while another import is in flight.
    /// </summary>
    public static string CleanupInterruptedRun()
    {
        if (running != null && !running.IsCompleted || EditorApplication.isUpdating || EditorApplication.isCompiling)
            throw new InvalidOperationException("Wait for the running import and Unity asset updates before cleanup.");
        var result = LoadResult();
        if (result == null) return Status();
        Cleanup(result);
        result.stage = "interrupted-cleaned";
        result.finishedUtc = DateTime.UtcNow.ToString("O");
        Save(result);
        return Status();
    }

    private static async Task RunAsync(Result result)
    {
        string firstAsset = result.assetRoot + "/First/probe.txt";
        string secondAsset = result.assetRoot + "/Second/probe.txt";
        string firstText = "First harmless import probe " + result.runId;
        string secondText = "Second harmless import probe " + result.runId;
        bool secondStarted = false;
        Task first = null, second = null;
        string firstPackage = null, secondPackage = null;
        void Completed(string name)
        {
            // Keep the unmodified callback in JSON: Unity versions can report a path rather than just a basename.
            result.rawNativeCompletions.Add(name);
            if (!MatchesCallback(name, result.nativeCompletions == 0 ? firstPackage : secondPackage))
            {
                Record(result, "unmatched native completion basename: " + Path.GetFileName(name));
                return;
            }
            result.nativeCompletions++;
            if (result.nativeCompletions == 1)
                result.secondWaitedForFirstCompletion = !secondStarted && second != null && !second.IsCompleted;
            Record(result, "native completed #" + result.nativeCompletions + ": " + name);
        }
        void Failed(string name, string error)
        {
            if (MatchesCallback(name, result.nativeCompletions == 0 ? firstPackage : secondPackage))
                Record(result, "native failed (raw package: " + name + "): " + error);
        }
        void Cancelled(string name)
        {
            if (MatchesCallback(name, result.nativeCompletions == 0 ? firstPackage : secondPackage))
                Record(result, "native cancelled (raw package: " + name + ")");
        }
        try
        {
            CheckOwnedPaths(result);
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(result.assetRoot));
            AssetDatabase.CreateFolder(result.assetRoot, "First");
            AssetDatabase.CreateFolder(result.assetRoot, "Second");
            File.WriteAllText(firstAsset, firstText);
            File.WriteAllText(secondAsset, secondText);
            AssetDatabase.ImportAsset(firstAsset, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(secondAsset, ImportAssetOptions.ForceSynchronousImport);
            string firstGuid = AssetDatabase.AssetPathToGUID(firstAsset);
            string secondGuid = AssetDatabase.AssetPathToGUID(secondAsset);
            Require(!string.IsNullOrEmpty(firstGuid) && !string.IsNullOrEmpty(secondGuid) && firstGuid != secondGuid,
                "The source TextAssets must have distinct valid GUIDs.");
            firstPackage = Export(result, "First", firstAsset);
            secondPackage = Export(result, "Second", secondAsset);
            Require(Path.GetFileName(firstPackage) == Path.GetFileName(secondPackage), "Package basenames must match.");
            Require(AssetDatabase.DeleteAsset(firstAsset) && AssetDatabase.DeleteAsset(secondAsset), "Could not remove the owned source TextAssets before import.");
            Require(!File.Exists(firstAsset) && !File.Exists(secondAsset), "Source assets still exist before import.");
            AssetDatabase.importPackageCompleted += Completed;
            AssetDatabase.importPackageFailed += Failed;
            AssetDatabase.importPackageCancelled += Cancelled;
            result.stage = "importing";
            first = UnityPackageImport.ImportAsync(firstPackage, starting: () => Record(result, "first native import starting"));
            second = UnityPackageImport.ImportAsync(secondPackage, starting: () =>
            {
                secondStarted = true;
                Require(result.nativeCompletions == 1, "Second native import started before the first completion, or consumed a wrong completion event.");
                Record(result, "second native import starting");
            });
            result.importsQueuedTogether = !first.IsCompleted && !second.IsCompleted;
            Record(result, "both import tasks queued without awaiting either");
            await Task.WhenAll(first, second);
            Require(result.importsQueuedTogether, "The first import finished before both tasks were queued; concurrency was not exercised.");
            Require(result.nativeCompletions == 2 && result.secondWaitedForFirstCompletion,
                "The two identical package names did not produce isolated serialized completions.");
            Require(AssetDatabase.AssetPathToGUID(firstAsset) == firstGuid && AssetDatabase.AssetPathToGUID(secondAsset) == secondGuid,
                "An imported TextAsset has the wrong GUID.");
            result.firstContentVerified = AssetDatabase.LoadAssetAtPath<TextAsset>(firstAsset)?.text == firstText;
            result.secondContentVerified = AssetDatabase.LoadAssetAtPath<TextAsset>(secondAsset)?.text == secondText;
            Require(result.firstContentVerified && result.secondContentVerified, "One import completed without its expected TextAsset content.");
            Record(result, "both TextAsset contents and GUIDs verified after separate native completions");
            result.stage = "passed";
        }
        catch (Exception error)
        {
            result.stage = "failed";
            result.error = error.GetBaseException().ToString();
            // Even a reporting/assertion error must not delete a package while the native importer is reading it.
            var pending = new[] { first, second }.Where(task => task != null && !task.IsCompleted).ToArray();
            if (pending.Length > 0)
            {
                try { await Task.WhenAll(pending); }
                catch (Exception importError) { result.error += "\nImport: " + importError.GetBaseException().Message; }
            }
        }
        finally
        {
            AssetDatabase.importPackageCompleted -= Completed;
            AssetDatabase.importPackageFailed -= Failed;
            AssetDatabase.importPackageCancelled -= Cancelled;
            try { Cleanup(result); }
            catch (Exception error)
            {
                result.stage = "failed";
                result.error = (result.error ?? "") + "\nCleanup: " + error.GetBaseException().Message;
            }
            result.finishedUtc = DateTime.UtcNow.ToString("O");
            Save(result);
        }
    }

    private static string Export(Result result, string subfolder, string asset)
    {
        string directory = Path.Combine(result.tempRoot, subfolder);
        Directory.CreateDirectory(directory);
        string package = Path.Combine(directory, result.packageName + ".unitypackage");
        AssetDatabase.ExportPackage(asset, package, ExportPackageOptions.Default);
        var index = UnityPackageIndex.Read(package);
        Require(index.CodeFiles.Count == 0 && index.UnsafePaths.Count == 0 &&
                index.Paths.All(path => path == result.assetRoot || path.StartsWith(result.assetRoot + "/", StringComparison.Ordinal)),
            "The exported package must contain only this probe's harmless owned assets.");
        Require(index.Paths.Contains(asset), "The native export is missing the selected TextAsset.");
        if (subfolder == "First") result.firstPackagePreflightPassed = true;
        else result.secondPackagePreflightPassed = true;
        Record(result, "exported genuine Unity package and strict reader verified safe paths/no code: " + subfolder);
        return package;
    }

    private static void Cleanup(Result result)
    {
        CheckOwnedPaths(result);
        if (AssetDatabase.IsValidFolder(result.assetRoot))
            Require(AssetDatabase.DeleteAsset(result.assetRoot), "Could not delete the owned probe asset folder.");
        else if (Directory.Exists(result.assetRoot))
        {
            Directory.Delete(Path.GetFullPath(result.assetRoot), true);
            if (File.Exists(result.assetRoot + ".meta")) File.Delete(result.assetRoot + ".meta");
        }
        if (Directory.Exists(result.tempRoot)) Directory.Delete(result.tempRoot, true);
        result.cleanupComplete = !Directory.Exists(result.assetRoot) && !Directory.Exists(result.tempRoot);
        Require(result.cleanupComplete, "Probe artifacts remain after cleanup.");
    }

    private static void CheckOwnedPaths(Result result)
    {
        Require(Guid.TryParseExact(result.runId, "N", out _), "Invalid probe identity.");
        string asset = "Assets/MCB_ImportQueueStress_" + result.runId;
        string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MCB_ImportQueueStress_" + result.runId));
        Require(result.assetRoot == asset && string.Equals(Path.GetFullPath(result.tempRoot), temp, StringComparison.OrdinalIgnoreCase),
            "Refusing to operate outside this probe's owned paths.");
        string assetFull = Path.GetFullPath(asset);
        Require(assetFull.StartsWith(Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase), "Probe asset path escapes Assets.");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    // Independent observer normalization: accept Unity's path or filename, with or without its specific suffix.
    // GetFileNameWithoutExtension would incorrectly truncate extensionless package names such as tool.1.8.
    private static bool MatchesCallback(string value, string expectedPath)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(expectedPath)) return false;
        string normalized = value.Replace('\\', '/');
        if (!normalized.Contains("/")) return WithoutPackageSuffix(normalized) == WithoutPackageSuffix(Path.GetFileName(expectedPath));
        return string.Equals(WithoutPackageSuffix(Path.GetFullPath(normalized)), WithoutPackageSuffix(Path.GetFullPath(expectedPath)),
            Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string WithoutPackageSuffix(string value)
    {
        const string suffix = ".unitypackage";
        return value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value.Substring(0, value.Length - suffix.Length) : value;
    }

    private static void Record(Result result, string message)
    {
        result.events.Add(DateTime.UtcNow.ToString("O") + " " + message);
        Save(result);
    }

    private static void Save(Result result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));
        File.WriteAllText(StatusPath, JsonUtility.ToJson(result, true));
    }

    private static Result LoadResult() => File.Exists(StatusPath) ? JsonUtility.FromJson<Result>(File.ReadAllText(StatusPath)) : null;
}
#endif
