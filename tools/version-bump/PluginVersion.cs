using System.Globalization;

namespace Phantom.Tools.VersionBump;

/// <summary>
/// A four-part plugin version <c>A.B.C.D</c> (Major.Minor.Patch.Revision), the
/// shape jprm / Jellyfin expect. Comparison is numeric and part-wise so bumps
/// are strictly monotonic. The three-part form <c>A.B.C</c> is the csproj
/// <c>&lt;Version&gt;</c> surface.
/// </summary>
public readonly struct PluginVersion : IComparable<PluginVersion>, IEquatable<PluginVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public int Revision { get; }

    public PluginVersion(int major, int minor, int patch, int revision)
    {
        if (major < 0 || minor < 0 || patch < 0 || revision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(major), "version parts must be non-negative");
        }

        Major = major;
        Minor = minor;
        Patch = patch;
        Revision = revision;
    }

    /// <summary>Parse "A.B.C" or "A.B.C.D" (a missing 4th part defaults to 0).</summary>
    public static PluginVersion Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new FormatException("empty version string");
        }

        var raw = text.Trim();
        var parts = raw.Split('.');
        if (parts.Length is < 3 or > 4)
        {
            throw new FormatException($"version '{raw}' must have 3 or 4 dot-separated numeric parts");
        }

        int P(int i)
        {
            if (i >= parts.Length)
            {
                return 0;
            }

            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v))
            {
                throw new FormatException($"version '{raw}' part '{parts[i]}' is not a non-negative integer");
            }

            return v;
        }

        return new PluginVersion(P(0), P(1), P(2), P(3));
    }

    /// <summary>The next strictly-greater version: increment the revision (4th part).</summary>
    public PluginVersion BumpRevision() => new(Major, Minor, Patch, Revision + 1);

    /// <summary>Four-part canonical string "A.B.C.D" (build.yaml, AssemblyVersion, FileVersion).</summary>
    public string ToFourPart() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}.{Revision}");

    /// <summary>Three-part string "A.B.C" (csproj &lt;Version&gt;).</summary>
    public string ToThreePart() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    public int CompareTo(PluginVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        return c != 0 ? c : Revision.CompareTo(other.Revision);
    }

    public bool Equals(PluginVersion other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PluginVersion v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Revision);

    public override string ToString() => ToFourPart();

    public static bool operator ==(PluginVersion a, PluginVersion b) => a.Equals(b);

    public static bool operator !=(PluginVersion a, PluginVersion b) => !a.Equals(b);

    public static bool operator <(PluginVersion a, PluginVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(PluginVersion a, PluginVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(PluginVersion a, PluginVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(PluginVersion a, PluginVersion b) => a.CompareTo(b) >= 0;
}
