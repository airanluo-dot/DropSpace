namespace DropSpace.Core.Updates;

public static class UpdateReleaseSelector
{
    public static UpdateRelease? SelectHighest(
        ReleaseVersion currentVersion,
        UpdateChannel channel,
        IEnumerable<UpdateRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return releases
            .Where(release => !release.IsDraft)
            .Select(release => new
            {
                Release = release,
                Parsed = ReleaseVersion.TryParse(release.TagName, out var version) ? version : (ReleaseVersion?)null,
            })
            .Where(candidate => candidate.Parsed.HasValue)
            .Where(candidate => channel == UpdateChannel.Preview || !candidate.Parsed!.Value.IsPreview)
            .Where(candidate => candidate.Parsed!.Value > currentVersion)
            .OrderByDescending(candidate => candidate.Parsed!.Value)
            .Select(candidate => candidate.Release)
            .FirstOrDefault();
    }

    public static ReleaseVersion? HighestStable(IEnumerable<UpdateRelease> releases) => releases
        .Where(release => !release.IsDraft)
        .Select(release => ReleaseVersion.TryParse(release.TagName, out var version) ? version : (ReleaseVersion?)null)
        .Where(version => version.HasValue && !version.Value.IsPreview)
        .OrderByDescending(version => version!.Value)
        .FirstOrDefault();
}
