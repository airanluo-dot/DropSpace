using System.Globalization;
using System.Text.RegularExpressions;

namespace DropSpace.Core.Updates;

public readonly partial record struct ReleaseVersion(
    int Major,
    int Minor,
    int Patch,
    int? PrereleaseNumber,
    string PrereleaseLabel = "beta") : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => PrereleaseNumber.HasValue;

    public static ReleaseVersion Parse(string value) =>
        TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a supported DropSpace release version.");

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = ReleasePattern().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        if (major > 20 || minor > 99 || patch > 99)
        {
            return false;
        }

        int? prerelease = null;
        if (match.Groups["prerelease"].Success)
        {
            if (!int.TryParse(match.Groups["prerelease"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number is < 1 or > 9998)
            {
                return false;
            }

            prerelease = number;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease, match.Groups["label"].Success ? match.Groups["label"].Value : "beta");
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;
        return PrereleaseNumber!.Value.CompareTo(other.PrereleaseNumber!.Value);
    }

    public int ToVersionCode() => checked(
        (Major * 100_000_000) +
        (Minor * 1_000_000) +
        (Patch * 10_000) +
        (PrereleaseNumber ?? 9_999));

    public Version ToWindowsVersion() => new(Major, Minor, Patch, PrereleaseNumber ?? 0);

    public Version ToPackageVersion() => new(Major, Minor, Patch, PrereleaseNumber ?? 9_999);

    public override string ToString() =>
        IsPrerelease
            ? $"{Major}.{Minor}.{Patch}-{PrereleaseLabel}.{PrereleaseNumber!.Value}"
            : $"{Major}.{Minor}.{Patch}";

    public string ToTagString() => $"v{this}";

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    [GeneratedRegex(
        "^v?(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:-(?<label>preview|beta)\\.(?<prerelease>[1-9][0-9]*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleasePattern();
}
