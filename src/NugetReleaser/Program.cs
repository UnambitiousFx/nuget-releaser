using System.Text.Json;
using System.Text.Json.Serialization;
using UnambitiousFx.NugetReleaser;

const string usage = """
                     Usage:
                       nuget-releaser plan [--json] [--config <path>]
                       nuget-releaser release [--dry-run] [--config <path>] [--output <dir>]

                     plan      Prints what would be released, with old and new versions and why. Changes nothing.
                     release   Packs the released packages; then, unless --dry-run, pushes them, tags and creates GitHub Releases.

                     --config  Configuration file (default: nuget-releaser.toml at the repository root).
                     --output  Where packages are written (default: artifacts/nuget-releaser at the repository root).
                     """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 2 : 0;
}

var command = args[0];
var options = new Dictionary<string, string?>();
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--json" or "--dry-run":
            options[args[i]] = null;
            break;
        case "--config" or "--output" when i + 1 < args.Length:
            options[args[i]] = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Unknown or incomplete option '{args[i]}'.\n\n{usage}");
            return 2;
    }
}

if (command is not ("plan" or "release"))
{
    Console.Error.WriteLine($"Unknown command '{command}'.\n\n{usage}");
    return 2;
}

try
{
    var repository = Repository.Open(Directory.GetCurrentDirectory());
    var config = ReleaserConfig.Load(options.GetValueOrDefault("--config") ?? Path.Combine(repository.Root, ReleaserConfig.FileName));
    var json = options.ContainsKey("--json");
    // With --json, stdout carries only the plan so it can be piped.
    var releaser = new Releaser(repository, config, json ? Console.Error : Console.Out, GitHubReleases.FromEnvironment(repository.OriginUrl()));
    var plan = releaser.Plan();

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(plan, PlanJsonContext.Default.ReleasePlan));
    }
    else
    {
        PrintPlan(plan);
    }

    if (command == "release")
    {
        var output = options.GetValueOrDefault("--output") ?? Path.Combine(repository.Root, "artifacts", "nuget-releaser");
        await releaser.ReleaseAsync(plan, output, options.ContainsKey("--dry-run"));
    }

    return 0;
}
catch (ReleaserException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static void PrintPlan(ReleasePlan plan)
{
    var releases = plan.Releases.ToList();
    Console.WriteLine($"{releases.Count} of {plan.Packages.Count} package(s) to release.");
    var width = plan.Packages.Select(p => p.Id.Length).DefaultIfEmpty(0).Max();
    foreach (var package in plan.Packages)
    {
        var versions = package.Release
            ? $"{package.CurrentVersion ?? "-"} → {package.NextVersion}"
            : $"{package.CurrentVersion} (unchanged)";
        Console.WriteLine($"  {package.Id.PadRight(width)}  {versions}");
        foreach (var reason in package.Reasons)
        {
            Console.WriteLine($"  {"".PadRight(width)}    {reason}");
        }
    }
}

namespace UnambitiousFx.NugetReleaser
{
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
    [JsonSerializable(typeof(ReleasePlan))]
    internal sealed partial class PlanJsonContext : JsonSerializerContext;
}
