#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

internal sealed class UnitGitReleaseApiResult
{
    public bool Success;
    public string Message = string.Empty;
    public string CommitHash = string.Empty;
    public string ReleaseId = string.Empty;
}

/// <summary>
/// Reflection boundary for the current Unit Git release API contract. MCB supports exactly
/// API v2 here; newer contracts should be handled by intentionally updating this binding.
/// </summary>
internal sealed class UnitGitReleaseApiBinding
{
    internal const int SupportedApiVersion = 2;
    internal const string CommitFilesCapability = "commit-files";
    internal const string ScopedReleaseCheckpointCapability = "scoped-release-checkpoint";
    internal const string FullProjectReleaseCheckpointCapability = "full-project-release-checkpoint";

    private const string UnitGitReleasesTypeName = "Orbiters.UnitGit.Editor.UnitGitReleases";
    private const string UnitGitReleaseEntryTypeName = "Orbiters.UnitGit.Editor.UnitGitReleaseEntry";
    private const string UnitGitReleaseFieldTypeName = "Orbiters.UnitGit.Editor.UnitGitReleaseField";
    internal const string MissingPackageMessage = "The Unit Git package (orbiters.unitgit) is not installed.";

    private readonly Type releasesType;
    private readonly Type entryType;
    private readonly Type fieldType;

    private UnitGitReleaseApiBinding(Type releasesType, Type entryType, Type fieldType)
    {
        this.releasesType = releasesType;
        this.entryType = entryType;
        this.fieldType = fieldType;
    }

    internal static bool TryCreateInstalled(
        IEnumerable<string> requiredCapabilities,
        out UnitGitReleaseApiBinding api,
        out string message)
    {
        return TryCreateForTypes(
            FindType(UnitGitReleasesTypeName),
            FindType(UnitGitReleaseEntryTypeName),
            FindType(UnitGitReleaseFieldTypeName),
            requiredCapabilities,
            out api,
            out message);
    }

    internal static bool TryCreateForTypes(
        Type releasesType,
        Type entryType,
        Type fieldType,
        IEnumerable<string> requiredCapabilities,
        out UnitGitReleaseApiBinding api,
        out string message)
    {
        api = null;

        if (releasesType == null || entryType == null || fieldType == null)
        {
            message = MissingPackageMessage;
            return false;
        }

        int apiVersion = GetStaticIntMember(releasesType, "ApiVersion");
        if (apiVersion != SupportedApiVersion)
        {
            message = $"The Unit Git package is installed but incompatible. MCB requires Unit Git release API v{SupportedApiVersion}; found v{apiVersion}.";
            return false;
        }

        if (!TryGetCapabilities(releasesType, out var capabilities, out message))
        {
            return false;
        }

        foreach (string capability in requiredCapabilities ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(capability))
            {
                continue;
            }

            if (!capabilities.Contains(capability, StringComparer.Ordinal))
            {
                message = $"The Unit Git package is installed but incompatible. Missing release API capability: {capability}.";
                return false;
            }
        }

        if (!ValidateEntryApi(entryType, out message) ||
            !ValidateFieldApi(fieldType, out message) ||
            !ValidateMethod(releasesType, "CommitFiles", typeof(string), typeof(string), typeof(string[]), out message))
        {
            return false;
        }

        if ((requiredCapabilities ?? Enumerable.Empty<string>()).Contains(ScopedReleaseCheckpointCapability, StringComparer.Ordinal) &&
            !ValidateMethod(releasesType, "PublishRelease", entryType, typeof(string), typeof(string[]), out message))
        {
            return false;
        }

        api = new UnitGitReleaseApiBinding(releasesType, entryType, fieldType);
        message = string.Empty;
        return true;
    }

    internal static bool IsMissingPackageMessage(string message)
    {
        return string.Equals(message, MissingPackageMessage, StringComparison.Ordinal);
    }

    internal object CreateReleaseEntry()
    {
        return Activator.CreateInstance(entryType);
    }

    internal void SetReleaseEntryValue(object entry, string name, object value)
    {
        SetMember(entry, name, value);
    }

    internal string GetReleaseEntryString(object entry, string name)
    {
        return GetMemberValue(entry, name) as string ?? string.Empty;
    }

    internal void AddReleaseField(object entry, string key, string value)
    {
        if (!(GetMemberValue(entry, "fields") is IList fields))
        {
            throw new InvalidOperationException("Unit Git release API v2 entry is missing a writable fields list.");
        }

        fields.Add(CreateReleaseField(key, value));
    }

    internal UnitGitReleaseApiResult CommitFiles(string commitTitle, string trailingParagraph, string[] projectRelativePaths)
    {
        object result = InvokeUnitGitMethod(
            "CommitFiles",
            new[] { typeof(string), typeof(string), typeof(string[]) },
            new object[] { commitTitle, trailingParagraph, projectRelativePaths });
        return ToResult(result);
    }

    internal UnitGitReleaseApiResult PublishRelease(object entry, string commitTitle, string[] projectRelativePaths)
    {
        object result = InvokeUnitGitMethod(
            "PublishRelease",
            new[] { entryType, typeof(string), typeof(string[]) },
            new object[] { entry, commitTitle, projectRelativePaths });
        return ToResult(result);
    }

    private object CreateReleaseField(string key, string value)
    {
        var constructor = fieldType.GetConstructor(new[] { typeof(string), typeof(string) });
        if (constructor != null)
        {
            return constructor.Invoke(new object[] { key, value });
        }

        object field = Activator.CreateInstance(fieldType);
        SetMember(field, "key", key);
        SetMember(field, "value", value);
        return field;
    }

    private object InvokeUnitGitMethod(string methodName, Type[] parameterTypes, object[] arguments)
    {
        var method = releasesType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static, null, parameterTypes, null);
        if (method == null)
        {
            throw new MissingMethodException(releasesType.FullName, methodName);
        }

        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }

    private static UnitGitReleaseApiResult ToResult(object result)
    {
        return new UnitGitReleaseApiResult
        {
            Success = GetBoolMember(result, "Success"),
            Message = GetStringMember(result, "Message"),
            CommitHash = GetStringMember(result, "CommitHash"),
            ReleaseId = GetStringMember(result, "ReleaseId")
        };
    }

    private static bool TryGetCapabilities(Type releasesType, out string[] capabilities, out string message)
    {
        capabilities = Array.Empty<string>();
        var method = releasesType.GetMethod("GetCapabilities", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (method == null)
        {
            message = "The Unit Git package is installed but incompatible. Missing release API method: GetCapabilities().";
            return false;
        }

        object rawCapabilities;
        try
        {
            rawCapabilities = method.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            message = "The Unit Git package is installed but incompatible. GetCapabilities() failed: " + ex.InnerException.Message;
            return false;
        }

        if (!(rawCapabilities is IEnumerable enumerable))
        {
            message = "The Unit Git package is installed but incompatible. GetCapabilities() must return an enumerable capability list.";
            return false;
        }

        capabilities = enumerable
            .Cast<object>()
            .Select(value => value?.ToString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        message = string.Empty;
        return true;
    }

    private static bool ValidateEntryApi(Type entryType, out string message)
    {
        if (entryType.GetConstructor(Type.EmptyTypes) == null)
        {
            message = "The Unit Git package is installed but incompatible. Release entry must have a public parameterless constructor.";
            return false;
        }

        object entry;
        try
        {
            entry = Activator.CreateInstance(entryType);
        }
        catch (Exception ex)
        {
            message = "The Unit Git package is installed but incompatible. Release entry could not be created: " + ex.Message;
            return false;
        }

        if (!(GetMemberValue(entry, "fields") is IList))
        {
            message = "The Unit Git package is installed but incompatible. Release entry must expose a fields list.";
            return false;
        }

        return ValidateWritableMember(entryType, "tool", out message) &&
               ValidateWritableMember(entryType, "type", out message) &&
               ValidateWritableMember(entryType, "name", out message) &&
               ValidateWritableMember(entryType, "version", out message) &&
               ValidateWritableMember(entryType, "title", out message) &&
               ValidateWritableMember(entryType, "changelog", out message) &&
               ValidateWritableMember(entryType, "scope", out message) &&
               ValidateWritableMember(entryType, "date", out message);
    }

    private static bool ValidateFieldApi(Type fieldType, out string message)
    {
        if (fieldType.GetConstructor(new[] { typeof(string), typeof(string) }) != null)
        {
            message = string.Empty;
            return true;
        }

        if (fieldType.GetConstructor(Type.EmptyTypes) == null)
        {
            message = "The Unit Git package is installed but incompatible. Release field must have a public (string, string) or parameterless constructor.";
            return false;
        }

        return ValidateWritableMember(fieldType, "key", out message) &&
               ValidateWritableMember(fieldType, "value", out message);
    }

    private static bool ValidateWritableMember(Type type, string memberName, out string message)
    {
        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null && !field.IsInitOnly)
        {
            message = string.Empty;
            return true;
        }

        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && property.CanWrite)
        {
            message = string.Empty;
            return true;
        }

        message = $"The Unit Git package is installed but incompatible. Missing writable release API member: {type.Name}.{memberName}.";
        return false;
    }

    private static bool ValidateMethod(Type releasesType, string methodName, Type firstParameter, Type secondParameter, Type thirdParameter, out string message)
    {
        var method = releasesType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { firstParameter, secondParameter, thirdParameter },
            null);
        if (method == null)
        {
            message = $"The Unit Git package is installed but incompatible. Missing release API method: {methodName}().";
            return false;
        }

        return ValidateResultApi(method.ReturnType, out message);
    }

    private static bool ValidateResultApi(Type resultType, out string message)
    {
        return ValidateReadableMember(resultType, "Success", typeof(bool), out message) &&
               ValidateReadableMember(resultType, "Message", typeof(string), out message) &&
               ValidateReadableMember(resultType, "CommitHash", typeof(string), out message) &&
               ValidateReadableMember(resultType, "ReleaseId", typeof(string), out message);
    }

    private static bool ValidateReadableMember(Type type, string memberName, Type memberType, out string message)
    {
        var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (field != null && field.FieldType == memberType)
        {
            message = string.Empty;
            return true;
        }

        var property = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && property.CanRead && property.PropertyType == memberType)
        {
            message = string.Empty;
            return true;
        }

        message = $"The Unit Git package is installed but incompatible. Missing readable release API member: {type.Name}.{memberName}.";
        return false;
    }

    private static int GetStaticIntMember(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name))
        {
            return 0;
        }

        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (field != null && field.GetValue(null) is int fieldValue)
        {
            return fieldValue;
        }

        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        if (property != null && property.CanRead && property.GetValue(null) is int propertyValue)
        {
            return propertyValue;
        }

        return 0;
    }

    private static Type FindType(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        var direct = Type.GetType(fullName);
        if (direct != null)
        {
            return direct;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = null;
            try { type = assembly.GetType(fullName); }
            catch { }
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static void SetMember(object target, string name, object value)
    {
        if (target == null || string.IsNullOrEmpty(name))
        {
            return;
        }

        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            field.SetValue(target, value);
            return;
        }

        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null && property.CanWrite)
        {
            property.SetValue(target, value);
            return;
        }

        throw new InvalidOperationException($"Unit Git release entry is missing writable member '{name}'.");
    }

    private static object GetMemberValue(object target, string name)
    {
        if (target == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        var type = target.GetType();
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            return field.GetValue(target);
        }

        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property != null && property.CanRead ? property.GetValue(target) : null;
    }

    private static bool GetBoolMember(object target, string name)
    {
        object value = GetMemberValue(target, name);
        return value is bool boolValue && boolValue;
    }

    private static string GetStringMember(object target, string name)
    {
        return GetMemberValue(target, name) as string ?? string.Empty;
    }
}
#endif
