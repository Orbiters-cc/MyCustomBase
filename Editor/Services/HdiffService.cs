#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// MCB HDiffPatch integration.
/// Adapted from YUCP DevTools' MIT-licensed HDiffPatch wrapper, CocoTools,
/// and Sisong's HDiffPatch native library.
/// </summary>
public static class HdiffService
{
    public const double MaxUsefulPatchRatio = 0.90d;
    public const string DeltaFormat = "HDIFF";
    public const string DeltaFormatMetadataKey = "deltaFormat";
    public const string DeltaCompressionMetadataKey = "deltaCompression";
    public const string DeltaOriginalSizeMetadataKey = "deltaOriginalSize";
    public const string DeltaOutputSizeMetadataKey = "deltaOutputSize";
    public const string DeltaPatchSizeMetadataKey = "deltaPatchSize";
    public const string DeltaPatchRatioMetadataKey = "deltaPatchRatio";

    public sealed class BuildInfo
    {
        public long baseBytes;
        public long outputBytes;
        public long patchBytes;
        public double patchRatio;
        public string compressionType;
    }

    public sealed class PatchInfo
    {
        public ulong baseBytes;
        public ulong outputBytes;
        public long patchBytes;
        public string compressionType;
    }

    public static bool IsAvailable(out string reason)
    {
#if UNITY_EDITOR_WIN
        try
        {
            MCBHdiffPatchWrapper.EnsureAvailable();
            reason = null;
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
#else
        reason = "HDiff FBX deltas require the Windows Editor native HDiffPatch DLLs.";
        return false;
#endif
    }

    public static bool TryWriteXorEncryptedDiffBin(
        string baseFbxPath,
        string modifiedFbxPath,
        string outputBinPath,
        FileManagerService fileManagerService,
        out BuildInfo info,
        out string failureReason)
    {
        info = null;
        failureReason = null;

        if (fileManagerService == null)
        {
            failureReason = "File manager service is not available.";
            return false;
        }

        if (!File.Exists(baseFbxPath))
        {
            failureReason = $"Base FBX not found: {baseFbxPath}";
            return false;
        }

        if (!File.Exists(modifiedFbxPath))
        {
            failureReason = $"Modified FBX not found: {modifiedFbxPath}";
            return false;
        }

        if (!IsAvailable(out string unavailableReason))
        {
            failureReason = unavailableReason;
            return false;
        }

        string tempHdiffPath = CreateTempWorkPath(".hdiff");
        try
        {
            var nativeErrors = new StringBuilder();
            HdiffDiffResult diffResult = MCBHdiffPatchWrapper.CreateDiff(
                baseFbxPath,
                modifiedFbxPath,
                tempHdiffPath,
                null,
                message => AppendNativeMessage(nativeErrors, message));

            if (diffResult != HdiffDiffResult.HDIFF_SUCCESS)
            {
                failureReason = $"HDiff creation failed: {diffResult}{FormatNativeMessages(nativeErrors)}";
                return false;
            }

            var baseInfo = new FileInfo(baseFbxPath);
            var outputInfo = new FileInfo(modifiedFbxPath);
            var patchInfo = new FileInfo(tempHdiffPath);
            if (!patchInfo.Exists || patchInfo.Length <= 0)
            {
                failureReason = "HDiff creation produced an empty patch.";
                return false;
            }

            double ratio = outputInfo.Length > 0
                ? patchInfo.Length / (double)outputInfo.Length
                : 1d;
            if (ratio > MaxUsefulPatchRatio)
            {
                failureReason = $"HDiff patch was {ratio:P1} of the full FBX, above the {MaxUsefulPatchRatio:P0} fallback threshold.";
                return false;
            }

            string compressionType = null;
            if (!MCBHdiffPatchWrapper.TryGetDiffInfo(tempHdiffPath, out ulong oldSize, out ulong newSize, out compressionType))
            {
                oldSize = (ulong)Math.Max(0, baseInfo.Length);
                newSize = (ulong)Math.Max(0, outputInfo.Length);
            }

            byte[] baseData = File.ReadAllBytes(baseFbxPath);
            byte[] hdiffData = File.ReadAllBytes(tempHdiffPath);
            byte[] encryptedData = fileManagerService.XorTransform(baseData, hdiffData);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputBinPath)));
            File.WriteAllBytes(outputBinPath, encryptedData);

            info = new BuildInfo
            {
                baseBytes = (long)oldSize,
                outputBytes = (long)newSize,
                patchBytes = patchInfo.Length,
                patchRatio = ratio,
                compressionType = compressionType
            };
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
        finally
        {
            DeleteTempFile(tempHdiffPath);
        }
    }

    public static PatchInfo ApplyXorEncryptedPatchToTempFbx(
        string baseFbxPath,
        string binPath,
        string outputFbxPath,
        FileManagerService fileManagerService)
    {
        if (fileManagerService == null)
        {
            throw new ArgumentNullException(nameof(fileManagerService));
        }

        if (string.IsNullOrWhiteSpace(baseFbxPath) || !File.Exists(baseFbxPath))
        {
            throw new FileNotFoundException("HDiff apply failed: base FBX file not found.", baseFbxPath);
        }

        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
        {
            throw new FileNotFoundException("HDiff apply failed: .bin patch file not found.", binPath);
        }

        if (!IsAvailable(out string unavailableReason))
        {
            throw new PlatformNotSupportedException(unavailableReason);
        }

        string tempHdiffPath = CreateTempWorkPath(".hdiff");
        try
        {
            byte[] baseData = File.ReadAllBytes(baseFbxPath);
            byte[] binData = File.ReadAllBytes(binPath);
            byte[] hdiffData = fileManagerService.XorTransform(baseData, binData);
            File.WriteAllBytes(tempHdiffPath, hdiffData);

            string compressionType = null;
            ulong oldSize = 0;
            ulong newSize = 0;
            MCBHdiffPatchWrapper.TryGetDiffInfo(tempHdiffPath, out oldSize, out newSize, out compressionType);

            string outputFullPath = Path.GetFullPath(outputFbxPath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath));
            if (File.Exists(outputFullPath))
            {
                File.Delete(outputFullPath);
            }

            var nativeErrors = new StringBuilder();
            HdiffPatchResult patchResult = MCBHdiffPatchWrapper.ApplyPatch(
                baseFbxPath,
                tempHdiffPath,
                outputFullPath,
                null,
                message => AppendNativeMessage(nativeErrors, message));

            if (patchResult != HdiffPatchResult.HPATCH_SUCCESS)
            {
                throw new InvalidDataException($"HDiff patch apply failed: {patchResult}{FormatNativeMessages(nativeErrors)}");
            }

            return new PatchInfo
            {
                baseBytes = oldSize,
                outputBytes = newSize,
                patchBytes = binData.LongLength,
                compressionType = compressionType
            };
        }
        finally
        {
            DeleteTempFile(tempHdiffPath);
        }
    }

    public static string CreateTempWorkPath(string extension)
    {
        string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string tempFolder = Path.Combine(projectPath, "Library", "MCB", "HdiffTemp");
        Directory.CreateDirectory(tempFolder);
        string safeExtension = string.IsNullOrWhiteSpace(extension) ? ".tmp" : extension;
        if (!safeExtension.StartsWith(".", StringComparison.Ordinal))
        {
            safeExtension = "." + safeExtension;
        }

        return Path.Combine(tempFolder, "mcb_hdiff_" + Guid.NewGuid().ToString("N") + safeExtension);
    }

    private static void AppendNativeMessage(StringBuilder builder, string message)
    {
        if (builder == null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(message.Trim());
    }

    private static string FormatNativeMessages(StringBuilder builder)
    {
        if (builder == null || builder.Length == 0)
        {
            return string.Empty;
        }

        return " - " + builder;
    }

    private static void DeleteTempFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[HdiffService] Failed to delete temp file '" + path + "': " + ex.Message);
        }
    }
}

internal static class MCBHdiffPatchTrust
{
#if UNITY_EDITOR_WIN
    private const string HdiffzSha256 = "11493E1D8947E6DA3CCA4A6C4D03AD55DC973D8766CEB25ECCDCA4811B8C0235";
    private const string HpatchzSha256 = "DBCD3320D0889CA894F1C404097A0B73918E13771A3902BC7F7CB99EAD47400E";
    private const string HdiffinfoSha256 = "28A2785870938EE45F98D5BBE6CFD0F0689980BCF3FFCE703E777FCBD5F51294";

    internal static string EnsureTrustedCopy(string projectPath, string libraryDir, string fileName)
    {
        string expectedHash = GetExpectedHash(fileName);
        if (string.IsNullOrEmpty(expectedHash))
        {
            throw new InvalidOperationException("No pinned hash is configured for " + fileName + ".");
        }

        Directory.CreateDirectory(libraryDir);
        string destinationPath = Path.Combine(libraryDir, fileName);
        string sourcePath = GetTrustedSource(projectPath, fileName);

        if (sourcePath == null)
        {
            if (FileMatchesSha256(destinationPath, expectedHash, out _))
            {
                return destinationPath;
            }

            throw new FileNotFoundException("No trusted copy of " + fileName + " was found.");
        }

        if (!FileMatchesSha256(destinationPath, expectedHash, out _))
        {
            File.Copy(sourcePath, destinationPath, true);
        }

        EnsureFileMatchesSha256(destinationPath, expectedHash, fileName);
        return destinationPath;
    }

    private static string GetTrustedSource(string projectPath, string fileName)
    {
        foreach (string candidatePath in GetCandidatePaths(projectPath, fileName))
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            if (IsTrustedNativeLibrary(fileName, candidatePath))
            {
                return candidatePath;
            }

            Debug.LogError("[HdiffService] Refusing to use untrusted " + fileName + " from " + candidatePath);
        }

        return null;
    }

    private static bool IsTrustedNativeLibrary(string fileName, string path)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string expectedHash = GetExpectedHash(fileName);
        if (string.IsNullOrEmpty(expectedHash))
        {
            return false;
        }

        string normalizedPath = Path.GetFullPath(path);
        string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        bool isTrustedPath = GetCandidatePaths(projectPath, fileName)
            .Any(candidate => PathsEqual(candidate, normalizedPath));

        return isTrustedPath && FileMatchesSha256(normalizedPath, expectedHash, out _);
    }

    private static string[] GetCandidatePaths(string projectPath, string fileName)
    {
        return new[]
        {
            Path.Combine(projectPath, "Packages", "orbiters.mcb", "Editor", "Plugins", "Hdiff", fileName),
            Path.Combine(projectPath, "Packages", "orbiters.mcb", "Plugins", "Hdiff", fileName)
        };
    }

    private static string GetExpectedHash(string fileName)
    {
        switch (fileName?.ToLowerInvariant())
        {
            case "hdiffz.dll":
                return HdiffzSha256;
            case "hpatchz.dll":
                return HpatchzSha256;
            case "hdiffinfo.dll":
                return HdiffinfoSha256;
            default:
                return null;
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        string normalizedLeft = Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedRight = Path.GetFullPath(right)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileMatchesSha256(string path, string expectedHash, out string actualHash)
    {
        actualHash = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string normalizedExpectedHash = NormalizeHash(expectedHash);
        if (string.IsNullOrEmpty(normalizedExpectedHash))
        {
            return false;
        }

        actualHash = ComputeSha256(path);
        return string.Equals(actualHash, normalizedExpectedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureFileMatchesSha256(string path, string expectedHash, string description)
    {
        if (FileMatchesSha256(path, expectedHash, out string actualHash))
        {
            return;
        }

        throw new InvalidDataException(
            description + " failed pinned SHA-256 validation. Expected " + NormalizeHash(expectedHash) + ", got " + (actualHash ?? "<missing>") + ".");
    }

    private static string NormalizeHash(string hash)
    {
        return string.IsNullOrWhiteSpace(hash)
            ? null
            : hash.Trim().Replace("-", string.Empty).ToUpperInvariant();
    }

    private static string ComputeSha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }
#endif
}

internal static class MCBHdiffPatchWrapper
{
#if UNITY_EDITOR_WIN
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HdiffStringOutput(string str);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void HpatchStringOutput(string str);

    [DllImport("hdiffz", EntryPoint = "RegisterDelegate")]
    private static extern void RegisterDelegateHdiffz(HdiffStringOutput del);

    [DllImport("hdiffz", EntryPoint = "RegisterErrorDelegate")]
    private static extern void RegisterErrorDelegateHdiffz(HdiffStringOutput del);

    [DllImport("hdiffz")]
    private static extern int hdiff_unity(string oldFileName, string newFileName, string outDiffFileName, string[] diffOptions, int diffOptionSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterDelegateHpatchzNative(HpatchStringOutput del);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterErrorDelegateHpatchzNative(HpatchStringOutput del);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate int HPatchUnityNative(
        int optionCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] options,
        string oldPath,
        string diffFileName,
        string outNewPath);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate int HDiffGetInfoNative(
        string diffFileName,
        out ulong oldSize,
        out ulong newSize,
        StringBuilder compressType,
        int compressTypeCap);

    private static bool s_dllsLoaded;
    private static IntPtr s_hdiffzHandle = IntPtr.Zero;
    private static IntPtr s_hpatchzHandle = IntPtr.Zero;
    private static IntPtr s_hdiffinfoHandle = IntPtr.Zero;
    private static RegisterDelegateHpatchzNative s_registerDelegateHpatchz;
    private static RegisterErrorDelegateHpatchzNative s_registerErrorDelegateHpatchz;
    private static HPatchUnityNative s_hpatchUnity;
    private static HDiffGetInfoNative s_hdiffGetInfo;
    private static Action<string> s_hdiffLogCallback;
    private static Action<string> s_hdiffErrorCallback;
    private static Action<string> s_hpatchLogCallback;
    private static Action<string> s_hpatchErrorCallback;

    private const string DefaultDiffOptions = "-m-6 -SD -c-zstd-21-24 -d";
#endif

    public static void EnsureAvailable()
    {
#if UNITY_EDITOR_WIN
        EnsureDllsLoaded();
        EnsurePatchExportsLoaded();
        EnsureDiffInfoExportLoaded();
#else
        throw new PlatformNotSupportedException("HDiff FBX deltas require the Windows Editor native HDiffPatch DLLs.");
#endif
    }

    public static HdiffDiffResult CreateDiff(
        string baseFbxPath,
        string modifiedFbxPath,
        string hdiffOutputPath,
        Action<string> logCallback = null,
        Action<string> errorCallback = null)
    {
#if UNITY_EDITOR_WIN
        EnsureDllsLoaded();

        s_hdiffLogCallback = logCallback;
        s_hdiffErrorCallback = errorCallback;
        try
        {
            if (logCallback != null)
            {
                RegisterDelegateHdiffz(HdiffLogWrapper);
            }

            if (errorCallback != null)
            {
                RegisterErrorDelegateHdiffz(HdiffErrorWrapper);
            }

            string[] options = DefaultDiffOptions.Split(' ');
            return (HdiffDiffResult)hdiff_unity(baseFbxPath, modifiedFbxPath, hdiffOutputPath, options, options.Length);
        }
        finally
        {
            s_hdiffLogCallback = null;
            s_hdiffErrorCallback = null;
        }
#else
        throw new PlatformNotSupportedException("HDiff creation requires the Windows Editor native HDiffPatch DLLs.");
#endif
    }

    public static HdiffPatchResult ApplyPatch(
        string baseFbxPath,
        string hdiffPath,
        string outputFbxPath,
        Action<string> logCallback = null,
        Action<string> errorCallback = null)
    {
#if UNITY_EDITOR_WIN
        EnsurePatchExportsLoaded();

        s_hpatchLogCallback = logCallback;
        s_hpatchErrorCallback = errorCallback;
        try
        {
            if (logCallback != null)
            {
                s_registerDelegateHpatchz(HpatchLogWrapper);
            }

            if (errorCallback != null)
            {
                s_registerErrorDelegateHpatchz(HpatchErrorWrapper);
            }

            return (HdiffPatchResult)s_hpatchUnity(0, new string[0], baseFbxPath, hdiffPath, outputFbxPath);
        }
        finally
        {
            s_hpatchLogCallback = null;
            s_hpatchErrorCallback = null;
        }
#else
        throw new PlatformNotSupportedException("HDiff apply requires the Windows Editor native HDiffPatch DLLs.");
#endif
    }

    public static bool TryGetDiffInfo(string hdiffPath, out ulong oldSize, out ulong newSize, out string compressType)
    {
        oldSize = 0;
        newSize = 0;
        compressType = string.Empty;
#if UNITY_EDITOR_WIN
        EnsureDiffInfoExportLoaded();

        var sb = new StringBuilder(260);
        int result = s_hdiffGetInfo(hdiffPath, out oldSize, out newSize, sb, sb.Capacity);
        if (result != 0)
        {
            return false;
        }

        compressType = sb.ToString();
        return true;
#else
        return false;
#endif
    }

    public static void FreeDlls()
    {
#if UNITY_EDITOR_WIN
        try
        {
            if (s_hdiffzHandle != IntPtr.Zero)
            {
                FreeLibrary(s_hdiffzHandle);
                s_hdiffzHandle = IntPtr.Zero;
            }

            if (s_hpatchzHandle != IntPtr.Zero)
            {
                FreeLibrary(s_hpatchzHandle);
                s_hpatchzHandle = IntPtr.Zero;
            }

            if (s_hdiffinfoHandle != IntPtr.Zero)
            {
                FreeLibrary(s_hdiffinfoHandle);
                s_hdiffinfoHandle = IntPtr.Zero;
            }

            s_registerDelegateHpatchz = null;
            s_registerErrorDelegateHpatchz = null;
            s_hpatchUnity = null;
            s_hdiffGetInfo = null;
            SetDllDirectory(null);
            s_dllsLoaded = false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[HdiffService] Error freeing HDiff DLLs: " + ex.Message);
        }
#endif
    }

#if UNITY_EDITOR_WIN
    private static void EnsureDllsLoaded()
    {
        if (s_dllsLoaded &&
            s_hdiffzHandle != IntPtr.Zero &&
            s_hpatchzHandle != IntPtr.Zero &&
            s_hdiffinfoHandle != IntPtr.Zero)
        {
            return;
        }

        string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        string libraryDir = Path.Combine(projectPath, "Library", "MCB", "Hdiff");
        string hdiffzPath = MCBHdiffPatchTrust.EnsureTrustedCopy(projectPath, libraryDir, "hdiffz.dll");
        string hpatchzPath = MCBHdiffPatchTrust.EnsureTrustedCopy(projectPath, libraryDir, "hpatchz.dll");
        string hdiffinfoPath = MCBHdiffPatchTrust.EnsureTrustedCopy(projectPath, libraryDir, "hdiffinfo.dll");

        SetDllDirectory(libraryDir);

        if (s_hdiffzHandle == IntPtr.Zero)
        {
            s_hdiffzHandle = LoadNativeLibrary(hdiffzPath);
        }

        if (s_hpatchzHandle == IntPtr.Zero)
        {
            s_hpatchzHandle = LoadNativeLibrary(hpatchzPath);
        }

        if (s_hdiffinfoHandle == IntPtr.Zero)
        {
            s_hdiffinfoHandle = LoadNativeLibrary(hdiffinfoPath);
        }

        BindRuntimeExports();
        s_dllsLoaded = true;
    }

    private static IntPtr LoadNativeLibrary(string path)
    {
        string fullPath = Path.GetFullPath(path);
        IntPtr handle = LoadLibrary(fullPath);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new DllNotFoundException("Failed to load HDiff native library '" + fullPath + "'. Win32 error: " + error);
        }

        return handle;
    }

    private static void BindRuntimeExports()
    {
        if (s_registerDelegateHpatchz == null)
        {
            s_registerDelegateHpatchz = GetRequiredExport<RegisterDelegateHpatchzNative>(s_hpatchzHandle, "RegisterDelegate");
        }

        if (s_registerErrorDelegateHpatchz == null)
        {
            s_registerErrorDelegateHpatchz = GetRequiredExport<RegisterErrorDelegateHpatchzNative>(s_hpatchzHandle, "RegisterErrorDelegate");
        }

        if (s_hpatchUnity == null)
        {
            s_hpatchUnity = GetRequiredExport<HPatchUnityNative>(s_hpatchzHandle, "hpatch_unity");
        }

        if (s_hdiffGetInfo == null)
        {
            s_hdiffGetInfo = GetRequiredExport<HDiffGetInfoNative>(s_hdiffinfoHandle, "hdiff_get_info");
        }
    }

    private static T GetRequiredExport<T>(IntPtr moduleHandle, string exportName) where T : class
    {
        if (moduleHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot resolve export '" + exportName + "' because the module handle is not loaded.");
        }

        IntPtr exportHandle = GetProcAddress(moduleHandle, exportName);
        if (exportHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException("Failed to resolve HDiff export '" + exportName + "'. Win32 error: " + error);
        }

        return (T)(object)Marshal.GetDelegateForFunctionPointer(exportHandle, typeof(T));
    }

    private static void EnsurePatchExportsLoaded()
    {
        EnsureDllsLoaded();

        if (s_registerDelegateHpatchz == null || s_registerErrorDelegateHpatchz == null || s_hpatchUnity == null)
        {
            throw new InvalidOperationException("hpatchz exports are not available.");
        }
    }

    private static void EnsureDiffInfoExportLoaded()
    {
        EnsureDllsLoaded();

        if (s_hdiffGetInfo == null)
        {
            throw new InvalidOperationException("hdiffinfo exports are not available.");
        }
    }

    private static void HdiffLogWrapper(string str)
    {
        s_hdiffLogCallback?.Invoke(str);
    }

    private static void HdiffErrorWrapper(string str)
    {
        s_hdiffErrorCallback?.Invoke(str);
    }

    private static void HpatchLogWrapper(string str)
    {
        s_hpatchLogCallback?.Invoke(str);
    }

    private static void HpatchErrorWrapper(string str)
    {
        s_hpatchErrorCallback?.Invoke(str);
    }
#endif
}

public enum HdiffDiffResult
{
    HDIFF_SUCCESS = 0,
    HDIFF_OPTIONS_ERROR,
    HDIFF_OPENREAD_ERROR,
    HDIFF_OPENWRITE_ERROR,
    HDIFF_FILECLOSE_ERROR,
    HDIFF_MEM_ERROR,
    HDIFF_DIFF_ERROR,
    HDIFF_PATCH_ERROR,
    HDIFF_RESAVE_FILEREAD_ERROR,
    HDIFF_RESAVE_DIFFINFO_ERROR,
    HDIFF_RESAVE_COMPRESSTYPE_ERROR,
    HDIFF_RESAVE_ERROR,
    HDIFF_RESAVE_CHECKSUMTYPE_ERROR,
    HDIFF_PATHTYPE_ERROR,
    HDIFF_TEMPPATH_ERROR,
    HDIFF_DELETEPATH_ERROR,
    HDIFF_RENAMEPATH_ERROR,
    DIRDIFF_DIFF_ERROR = 101,
    DIRDIFF_PATCH_ERROR,
    MANIFEST_CREATE_ERROR,
    MANIFEST_TEST_ERROR
}

public enum HdiffPatchResult
{
    HPATCH_SUCCESS = 0,
    HPATCH_OPTIONS_ERROR = 1,
    HPATCH_OPENREAD_ERROR,
    HPATCH_OPENWRITE_ERROR,
    HPATCH_FILEREAD_ERROR,
    HPATCH_FILEWRITE_ERROR,
    HPATCH_FILEDATA_ERROR,
    HPATCH_FILECLOSE_ERROR,
    HPATCH_MEM_ERROR,
    HPATCH_HDIFFINFO_ERROR,
    HPATCH_COMPRESSTYPE_ERROR,
    HPATCH_HPATCH_ERROR,
    HPATCH_PATHTYPE_ERROR,
    HPATCH_TEMPPATH_ERROR,
    HPATCH_DELETEPATH_ERROR,
    HPATCH_RENAMEPATH_ERROR,
    HPATCH_SPATCH_ERROR,
    HPATCH_BSPATCH_ERROR,
    HPATCH_VCPATCH_ERROR,
    HPATCH_DECOMPRESSER_OPEN_ERROR = 20,
    HPATCH_DECOMPRESSER_CLOSE_ERROR,
    HPATCH_DECOMPRESSER_MEM_ERROR,
    HPATCH_DECOMPRESSER_DECOMPRESS_ERROR,
    HPATCH_FILEWRITE_NO_SPACE_ERROR
}
#endif
