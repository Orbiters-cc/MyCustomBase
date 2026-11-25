#if UNITY_EDITOR
/// <summary>
/// A creator may publish a corrected build of a version under its own number (VersionPublisher, replace). The server
/// counts these builds (<see cref="CustomBaseVersion.revision"/>); the avatar remembers the revision it installed
/// (<see cref="MyCustomBase.appliedCustomBaseRevision"/>), so a corrected build is offered like a newer version.
/// </summary>
public static class VersionRevisions
{
    public static int Installed(MyCustomBase target) => target != null && target.appliedCustomBaseRevision > 0 ? target.appliedCustomBaseRevision : 1;

    /// <summary>The published version the avatar has, in a build its creator published after the one installed.</summary>
    public static bool IsCorrectedBuildOf(CustomBaseVersion version, MyCustomBase target)
    {
        if (version == null || version.isUnsubmitted || target == null || target.appliedCustomBaseAssetId != version.assetId ||
            string.IsNullOrEmpty(target.appliedCustomBaseVersionString) || target.appliedCustomBaseVersionString != version.version)
            return false;
        if (!string.IsNullOrEmpty(target.appliedCustomBaseDefaultAviVersion) && target.appliedCustomBaseDefaultAviVersion != version.defaultAviVersion)
            return false;
        if (!string.IsNullOrEmpty(target.appliedCustomBaseSourceVersionKey) && !string.IsNullOrEmpty(version.sourceVersionKey) &&
            target.appliedCustomBaseSourceVersionKey != version.sourceVersionKey)
            return false;
        return version.Revision > Installed(target);
    }

    /// <summary>The label of a corrected build next to its version number ("1.0.8, corrected build").</summary>
    public static string Describe(CustomBaseVersion version) => version == null ? "" : version.version + ", corrected build";
}
#endif
