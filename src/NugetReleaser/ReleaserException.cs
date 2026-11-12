namespace UnambitiousFx.NugetReleaser;

/// <summary>An error the user can act on: printed without a stack trace, exit code 1.</summary>
internal sealed class ReleaserException(string message) : Exception(message);
