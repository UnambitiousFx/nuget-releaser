using System.Diagnostics;

namespace UnambitiousFx.NugetReleaser;

/// <summary>Runs <c>git</c> and <c>dotnet</c>; a non-zero exit becomes a <see cref="ReleaserException" />.</summary>
internal static class ProcessRunner
{
    public static string Run(string workingDirectory, string fileName, params IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new ReleaserException($"Could not start '{fileName}'.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            // The arguments are left out on purpose: they can hold an API key.
            throw new ReleaserException($"'{fileName} {info.ArgumentList.FirstOrDefault()}' failed (exit {process.ExitCode}):\n{error.Result}{output}".TrimEnd());
        }

        return output;
    }
}
