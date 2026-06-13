#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unified error pipeline for version operations (build / publish / upload / checkpoint).
/// Replaces the dissolved DiskSpaceService and the per-coroutine ad-hoc error handling:
/// classify an exception once, report it once (console + warnings box + optional
/// actionable dialog).
/// </summary>
public enum OperationErrorCategory
{
    DiskFull,
    ServerDiskFull,
    Auth,
    VersionConflict,
    PackageTooLarge,
    Integrity,
    GitLock,
    Cancelled,
    Network,
    Unknown
}

public class OperationError
{
    public OperationErrorCategory Category;
    /// <summary>Message shown to the user (warnings box / dialog).</summary>
    public string UserMessage;
    /// <summary>Full detail for the console.</summary>
    public string Detail;
}

public static class OperationErrorClassifier
{
    private const int Win32ErrorDiskFull = 112;        // ERROR_DISK_FULL
    private const int Win32ErrorHandleDiskFull = 39;   // ERROR_HANDLE_DISK_FULL
    private const int PosixEnospc = 28;                // ENOSPC

    /// <summary>First matching category wins. <paramref name="operation"/> is a human
    /// description like "Building the version" used in disk-full messaging.</summary>
    public static OperationError Classify(Exception exception, string operation)
    {
        string message = exception?.Message ?? string.Empty;

        if (IsDiskFullError(exception) || IsDiskFullMessage(message))
        {
            return new OperationError
            {
                Category = OperationErrorCategory.DiskFull,
                UserMessage = DiskUtils.BuildDiskFullMessage(operation),
                Detail = exception?.ToString() ?? message
            };
        }

        return ClassifyMessage(message, exception?.ToString());
    }

    public static OperationError ClassifyMessage(string message, string detail = null)
    {
        message = message ?? string.Empty;
        var error = new OperationError { UserMessage = message, Detail = detail ?? message };

        if (Contains(message, "server storage volume"))
        {
            error.Category = OperationErrorCategory.ServerDiskFull;
            error.UserMessage = "The MCB server ran out of disk space while saving the package. " + message;
        }
        else if (IsDiskFullMessage(message))
        {
            error.Category = OperationErrorCategory.DiskFull;
        }
        else if (Contains(message, "[401]") || Contains(message, "Unauthorized") || Contains(message, "ACCESS_DENIED"))
        {
            error.Category = OperationErrorCategory.Auth;
            error.UserMessage = "Authentication failed or expired. Re-connect through Magic Sync and try again.\n" + message;
        }
        else if (Contains(message, "[409]") || Contains(message, "already exists"))
        {
            error.Category = OperationErrorCategory.VersionConflict;
            error.UserMessage = "This version number already exists on the server. Bump the version number and rebuild.\n" + message;
        }
        else if (Contains(message, "[413]") || Contains(message, "too large to upload"))
        {
            error.Category = OperationErrorCategory.PackageTooLarge;
        }
        else if (Contains(message, "index.lock"))
        {
            error.Category = OperationErrorCategory.GitLock;
        }
        else if (Contains(message, "upload was cancelled") || Contains(message, "Request aborted"))
        {
            error.Category = OperationErrorCategory.Cancelled;
        }
        else if (Contains(message, "HDiff") ||
                 Contains(message, "hash mismatch") ||
                 Contains(message, "integrity verification"))
        {
            error.Category = OperationErrorCategory.Integrity;
            error.UserMessage = "Version patch integrity verification failed. Rebuild or re-download the version files and try again.\n" + message;
        }
        else if (Contains(message, "timed out") || Contains(message, "timeout") ||
                 Contains(message, "Cannot connect") || Contains(message, "connection") ||
                 Contains(message, "Cannot resolve"))
        {
            error.Category = OperationErrorCategory.Network;
        }
        else
        {
            error.Category = OperationErrorCategory.Unknown;
        }

        return error;
    }

    public static bool IsDiskFullError(Exception exception)
    {
        for (Exception current = exception; current != null; current = current.InnerException)
        {
            if (current is IOException)
            {
                int code = current.HResult & 0xFFFF;
                if (code == Win32ErrorDiskFull || code == Win32ErrorHandleDiskFull || code == PosixEnospc)
                {
                    return true;
                }
            }

            if (IsDiskFullMessage(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsDiskFullMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return Contains(message, "No space left on device") ||
               Contains(message, "Win32 IO returned 112") ||
               Contains(message, "ENOSPC") ||
               Contains(message, "not enough space on the disk") ||
               Contains(message, "Insufficient disk space") ||
               Contains(message, "out of disk space") ||
               Contains(message, "disk full");
    }

    private static bool Contains(string text, string value)
    {
        return !string.IsNullOrEmpty(text) && text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

public static class OperationErrorReporter
{
    /// <summary>
    /// Single rendering point: console + MCB warnings box, plus the category-specific
    /// action dialog (currently: disk-full offers a one-click flush of removable data).
    /// Returns the user-facing message (callers usually store it in editor.submitError).
    /// </summary>
    public static string Report(MCBEditor editor, OperationError error, string warningTitle)
    {
        if (error == null)
        {
            return string.Empty;
        }

        MCBLogger.LogError($"[{warningTitle}] {error.Category}: {error.Detail}");
        editor?.warningsModule?.AddWarning(error.UserMessage, MessageType.Error, GetWarningTitle(error.Category, warningTitle));
        editor?.Repaint();

        if (error.Category == OperationErrorCategory.DiskFull)
        {
            OfferFlush(editor, error.UserMessage);
        }

        return error.UserMessage;
    }

    private static string GetWarningTitle(OperationErrorCategory category, string fallback)
    {
        switch (category)
        {
            case OperationErrorCategory.DiskFull: return "Disk full";
            case OperationErrorCategory.ServerDiskFull: return "Server disk full";
            case OperationErrorCategory.Auth: return "Authentication";
            case OperationErrorCategory.VersionConflict: return "Version conflict";
            case OperationErrorCategory.PackageTooLarge: return "Package too large";
            case OperationErrorCategory.Integrity: return "Version files changed";
            case OperationErrorCategory.GitLock: return "Git lock";
            case OperationErrorCategory.Cancelled: return "Cancelled";
            default: return fallback;
        }
    }

    private static void OfferFlush(MCBEditor editor, string message)
    {
        if (EditorUtility.DisplayDialog(
                "Disk Full",
                message + "\n\nFlush removable MCB data now?",
                "Flush removable data",
                "Not now"))
        {
            string summary = VersionRepository.FlushRemovableData(editor);
            EditorUtility.DisplayDialog("Flush Complete", summary, "OK");
        }
    }
}

/// <summary>Small disk helpers shared by the error pipeline and the Advanced window.</summary>
public static class DiskUtils
{
    public static string GetProjectDriveName()
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(Application.dataPath));
        }
        catch
        {
            return string.Empty;
        }
    }

    public static long GetFreeBytesForProjectDrive()
    {
        try
        {
            return new DriveInfo(GetProjectDriveName()).AvailableFreeSpace;
        }
        catch
        {
            return -1L;
        }
    }

    public static string BuildDiskFullMessage(string operation)
    {
        long freeBytes = GetFreeBytesForProjectDrive();
        string drive = GetProjectDriveName();
        string freeText = freeBytes >= 0 ? $"{FormatBytes(freeBytes)} free on {drive}" : "free space unknown";
        return $"{operation} failed because your disk is full ({freeText}). " +
               "Free up disk space, or flush removable MCB data (unused downloaded version files and generated advanced meshes - they can be downloaded or rebuilt again anytime).";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "?";
        }

        if (bytes < 1024L * 1024L)
        {
            return $"{bytes / 1024f:0.#} KB";
        }

        if (bytes < 1024L * 1024L * 1024L)
        {
            return $"{bytes / (1024f * 1024f):0.#} MB";
        }

        return $"{bytes / (1024f * 1024f * 1024f):0.##} GB";
    }
}
#endif
