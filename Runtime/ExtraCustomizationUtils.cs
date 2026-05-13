using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class ExtraCustomizationUtils
{
    public const string SuggestRealisticKey = "suggestRealistic";

    public static bool HasFlag(IEnumerable<object> entries, string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
        {
            return false;
        }

        return (entries ?? Enumerable.Empty<object>())
            .Any(entry => IsStringFlag(entry, flag));
    }

    public static List<object> CloneEntries(IEnumerable<object> entries)
    {
        return (entries ?? Enumerable.Empty<object>())
            .Where(entry => entry != null)
            .Select(CloneEntry)
            .ToList();
    }

    public static void SetFlag(List<object> entries, string flag, bool enabled)
    {
        if (entries == null || string.IsNullOrWhiteSpace(flag))
        {
            return;
        }

        entries.RemoveAll(entry => IsStringFlag(entry, flag));
        if (enabled)
        {
            entries.Add(flag);
        }
    }

    public static List<string> GetStringList(IEnumerable<object> entries, string key)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(key))
        {
            return values;
        }

        foreach (object entry in entries ?? Enumerable.Empty<object>())
        {
            if (!TryGetObjectValue(entry, key, out object rawValue))
            {
                continue;
            }

            values.AddRange(ReadStringValues(rawValue));
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static void SetStringList(List<object> entries, string key, IEnumerable<string> values)
    {
        if (entries == null || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        entries.RemoveAll(entry => HasObjectKey(entry, key));

        var normalizedValues = (values ?? Enumerable.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalizedValues.Length == 0)
        {
            return;
        }

        entries.Add(new Dictionary<string, object>
        {
            { key, normalizedValues }
        });
    }

    public static object[] ToArrayOrNull(IEnumerable<object> entries)
    {
        var normalized = (entries ?? Enumerable.Empty<object>())
            .Where(entry => entry != null)
            .Where(entry => !(entry is string text) || !string.IsNullOrWhiteSpace(text))
            .ToList();

        return normalized.Count > 0 ? normalized.ToArray() : null;
    }

    public static string ToDisplayString(object entry)
    {
        if (entry == null)
        {
            return string.Empty;
        }

        if (entry is string text)
        {
            return text;
        }

        if (entry is JToken token)
        {
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        return entry.ToString();
    }

    private static bool IsStringFlag(object entry, string flag)
    {
        if (entry is string text)
        {
            return string.Equals(text.Trim(), flag, StringComparison.OrdinalIgnoreCase);
        }

        if (entry is JValue value && value.Type == JTokenType.String)
        {
            return string.Equals(value.ToString().Trim(), flag, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static object CloneEntry(object entry)
    {
        if (entry is JToken token)
        {
            return token.DeepClone();
        }

        if (entry is IDictionary<string, object> dict)
        {
            return new Dictionary<string, object>(dict, StringComparer.Ordinal);
        }

        return entry;
    }

    private static bool HasObjectKey(object entry, string key)
    {
        return TryGetObjectValue(entry, key, out _);
    }

    private static bool TryGetObjectValue(object entry, string key, out object value)
    {
        value = null;
        if (entry == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (entry is JObject jObject)
        {
            if (jObject.TryGetValue(key, StringComparison.OrdinalIgnoreCase, out JToken token))
            {
                value = token;
                return true;
            }

            return false;
        }

        if (entry is IDictionary<string, object> dict)
        {
            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = pair.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> ReadStringValues(object rawValue)
    {
        if (rawValue == null)
        {
            yield break;
        }

        if (rawValue is string text)
        {
            yield return text;
            yield break;
        }

        if (rawValue is JArray jArray)
        {
            foreach (JToken token in jArray)
            {
                if (token.Type == JTokenType.String)
                {
                    yield return token.ToString();
                }
            }

            yield break;
        }

        if (rawValue is JValue jValue && jValue.Type == JTokenType.String)
        {
            yield return jValue.ToString();
            yield break;
        }

        if (rawValue is IEnumerable enumerable)
        {
            foreach (object value in enumerable)
            {
                if (value is string stringValue)
                {
                    yield return stringValue;
                }
                else if (value is JValue nestedValue && nestedValue.Type == JTokenType.String)
                {
                    yield return nestedValue.ToString();
                }
            }
        }
    }
}
