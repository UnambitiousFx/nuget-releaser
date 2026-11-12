using System.Text.RegularExpressions;

namespace UnambitiousFx.NugetReleaser;

/// <summary>A commit that touched a package, as read from <c>git log</c>.</summary>
internal sealed record Commit(string Sha, string Message)
{
    public string Subject => Message.Split('\n', 2)[0].Trim();
}

/// <summary>Maps a Conventional Commits message to the bump it asks for.</summary>
internal static partial class ConventionalCommit
{
    [GeneratedRegex(@"^(?<type>[A-Za-z]+)(\([^)]*\))?(?<breaking>!)?: \S")]
    private static partial Regex Header();

    [GeneratedRegex(@"^BREAKING[ -]CHANGE: ", RegexOptions.Multiline)]
    private static partial Regex BreakingFooter();

    /// <summary><c>!</c> or a <c>BREAKING CHANGE:</c> footer → major, <c>feat</c> → minor, <c>fix</c>/<c>perf</c> → patch.</summary>
    public static Bump BumpOf(string message)
    {
        var header = Header().Match(message.Split('\n', 2)[0].Trim());
        if (!header.Success)
        {
            return Bump.None;
        }

        if (header.Groups["breaking"].Success || BreakingFooter().IsMatch(message))
        {
            return Bump.Major;
        }

        return header.Groups["type"].Value.ToLowerInvariant() switch
        {
            "feat" => Bump.Minor,
            "fix" or "perf" => Bump.Patch,
            _ => Bump.None
        };
    }
}
