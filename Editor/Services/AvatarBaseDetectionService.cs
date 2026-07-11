#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;

public static class AvatarBaseDetectionService
{
    public sealed class DetectionResult
    {
        public int baseIndex;
        public int avatarBaseId;
        public int sourceRevisionId;
        public List<AvatarBaseSourceMatcher.Match> matches;
    }

    public static DetectionResult DetectUniqueBase(
        IReadOnlyList<CreatorAvatarBaseOption> avatarBases,
        IReadOnlyList<AvatarBaseSourceMatcher.LocalFile> localFiles)
    {
        var matches = new List<DetectionResult>();
        for (int index = 0; index < (avatarBases?.Count ?? 0); index++)
        {
            matches.AddRange(DetectForBase(avatarBases[index], index, localFiles));
        }

        return SelectUniqueMostSpecific(matches);
    }

    public static DetectionResult DetectUniqueRevision(
        CreatorAvatarBaseOption avatarBase,
        int baseIndex,
        IReadOnlyList<AvatarBaseSourceMatcher.LocalFile> localFiles)
    {
        var matches = DetectForBase(avatarBase, baseIndex, localFiles);
        return SelectUniqueMostSpecific(matches);
    }

    private static List<DetectionResult> DetectForBase(
        CreatorAvatarBaseOption avatarBase,
        int baseIndex,
        IReadOnlyList<AvatarBaseSourceMatcher.LocalFile> localFiles)
    {
        if (avatarBase == null) return new List<DetectionResult>();

        return (avatarBase.sourceRevisions ?? new List<CreatorAvatarBaseSourceRevisionOption>())
            .Where(revision => revision != null && revision.isActive)
            .Select(revision => new
            {
                revision,
                matches = AvatarBaseSourceMatcher.MatchOneToOne(
                    (revision.sourceFiles ?? new List<CreatorAvatarBaseSourceFileOption>())
                        .OrderBy(file => file.position)
                        .Select(file => new AvatarBaseSourceMatcher.SourceFile
                        {
                            id = file.id,
                            path = file.canonicalPath,
                            hash = file.hash,
                            tag = file
                        }),
                    localFiles,
                    requireEqualCount: false)
            })
            .Where(entry => entry.matches != null)
            .Select(entry => new DetectionResult
            {
                baseIndex = baseIndex,
                avatarBaseId = avatarBase.id,
                sourceRevisionId = entry.revision.id,
                matches = entry.matches
            })
            .ToList();
    }

    private static DetectionResult SelectUniqueMostSpecific(IReadOnlyList<DetectionResult> matches)
    {
        if (matches == null || matches.Count == 0) return null;

        int largestSourceCount = matches.Max(match => match.matches?.Count ?? 0);
        var mostSpecific = matches
            .Where(match => (match.matches?.Count ?? 0) == largestSourceCount)
            .ToList();
        return mostSpecific.Count == 1 ? mostSpecific[0] : null;
    }
}
#endif
