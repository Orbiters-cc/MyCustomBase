#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

public static class AvatarBaseSourceMatcher
{
    public sealed class SourceFile
    {
        public int id;
        public string path;
        public string hash;
        public object tag;
    }

    public sealed class LocalFile
    {
        public string path;
        public string hash;
        public object tag;
    }

    public sealed class Match
    {
        public SourceFile source;
        public LocalFile local;
        public string provenance;
    }

    public static List<Match> MatchOneToOne(
        IEnumerable<SourceFile> sourceFiles,
        IEnumerable<LocalFile> localFiles,
        bool requireEqualCount = true)
    {
        var suppliedSources = (sourceFiles ?? Enumerable.Empty<SourceFile>())
            .Where(file => file != null)
            .ToList();
        var suppliedLocals = (localFiles ?? Enumerable.Empty<LocalFile>())
            .Where(file => file != null)
            .ToList();
        var sources = suppliedSources
            .Where(IsValidSource)
            .ToList();
        var locals = suppliedLocals
            .Where(IsValidLocal)
            .ToList();
        if (sources.Count == 0 ||
            sources.Count != suppliedSources.Count ||
            locals.Count != suppliedLocals.Count ||
            sources.Count > locals.Count ||
            (requireEqualCount && sources.Count != locals.Count))
        {
            return null;
        }

        var unused = new HashSet<int>(Enumerable.Range(0, locals.Count));
        var result = new Match[sources.Count];
        var deferred = new List<int>();

        for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            SourceFile source = sources[sourceIndex];
            var exact = unused
                .Where(localIndex => PathsEqual(locals[localIndex].path, source.path) &&
                                     HashesEqual(locals[localIndex].hash, source.hash))
                .ToList();
            if (exact.Count > 1) return null;
            if (exact.Count == 1)
            {
                int localIndex = exact[0];
                unused.Remove(localIndex);
                result[sourceIndex] = CreateMatch(source, locals[localIndex], "PATH_AND_HASH");
            }
            else
            {
                deferred.Add(sourceIndex);
            }
        }

        foreach (int sourceIndex in deferred)
        {
            SourceFile source = sources[sourceIndex];
            var hashMatches = unused
                .Where(localIndex => HashesEqual(locals[localIndex].hash, source.hash))
                .ToList();
            if (hashMatches.Count != 1) return null;

            int localIndex = hashMatches[0];
            unused.Remove(localIndex);
            result[sourceIndex] = CreateMatch(source, locals[localIndex], "UNIQUE_HASH");
        }

        return result.ToList();
    }

    private static Match CreateMatch(SourceFile source, LocalFile local, string provenance)
    {
        return new Match { source = source, local = local, provenance = provenance };
    }

    private static bool IsValidSource(SourceFile file)
    {
        return file != null && !string.IsNullOrWhiteSpace(file.path) && IsSha256(file.hash);
    }

    private static bool IsValidLocal(LocalFile file)
    {
        return file != null && !string.IsNullOrWhiteSpace(file.path) && IsSha256(file.hash);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool HashesEqual(string left, string right)
    {
        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return path?.Trim().Replace('\\', '/');
    }

    private static bool IsSha256(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash.Trim().Length != 64) return false;
        return hash.Trim().All(Uri.IsHexDigit);
    }
}
#endif
