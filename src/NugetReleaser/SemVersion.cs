namespace UnambitiousFx.NugetReleaser;

/// <summary>The part of a version a release increments.</summary>
internal enum Bump
{
    None,
    Patch,
    Minor,
    Major
}

/// <summary>A stable SemVer 2.0 version (<c>major.minor.patch</c>).</summary>
// shortcut: pre-release and build metadata are not parsed, so pre-release tags are ignored; extend when channels land.
internal readonly record struct SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static bool TryParse(string text, out SemVersion version)
    {
        version = default;
        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!TryParsePart(parts[0], out var major) || !TryParsePart(parts[1], out var minor) || !TryParsePart(parts[2], out var patch))
        {
            return false;
        }

        version = new SemVersion(major, minor, patch);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new ReleaserException($"'{text}' is not a major.minor.patch version.");

    public SemVersion Increment(Bump bump) => bump switch
    {
        Bump.Major => new SemVersion(Major + 1, 0, 0),
        Bump.Minor => new SemVersion(Major, Minor + 1, 0),
        Bump.Patch => this with { Patch = Patch + 1 },
        _ => this
    };

    public int CompareTo(SemVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    // SemVer forbids leading zeros and signs.
    private static bool TryParsePart(string part, out int value)
    {
        value = 0;
        return part.Length > 0
               && part.All(char.IsAsciiDigit)
               && (part.Length == 1 || part[0] != '0')
               && int.TryParse(part, out value);
    }
}
