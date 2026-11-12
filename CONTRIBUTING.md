# Contributing to nuget-releaser

Thank you for considering contributing to nuget-releaser!

## Code of Conduct

We expect all contributors to be respectful and considerate, to accept constructive criticism gracefully, and to focus
on what is best for the community.

## How Can I Contribute?

### Reporting Bugs

Check existing issues first, then use the **bug report template**. Include your `nuget-releaser.toml`, the repository
layout (which projects reference which), the output of `nuget-releaser plan` and what you expected instead.

### Suggesting Enhancements

Use the **feature request template**: the use case, the proposed command or configuration, and the alternatives you
considered.

### Pull Requests

1. Create your branch from `main` (`<type>/<issue>-<slug>`, e.g. `feat/12-prerelease-channels`).
2. Follow the coding standards below.
3. Add tests: unit tests for the planning logic, an integration test (real git and `dotnet`) for anything that touches
   discovery, git or packing.
4. Update the README if a command, an option or a configuration key changes.
5. Run `dotnet test --solution NugetReleaser.slnx`.

PR titles follow [Conventional Commits](https://www.conventionalcommits.org/) (`feat:`, `fix:`, `perf:`, `docs:`,
`test:`, `refactor:`, `build:`, `ci:`, `chore:`): PRs are squash-merged and the title becomes the commit that decides the
release (see [Release Process](#release-process)).

## Development Environment Setup

### Prerequisites

- [.NET 11 SDK](https://dotnet.microsoft.com/download) and git
- An IDE: [JetBrains Rider](https://www.jetbrains.com/rider/), [Visual Studio](https://visualstudio.microsoft.com/), or
  [VS Code](https://code.visualstudio.com/)

### Getting Started

```bash
git clone https://github.com/UnambitiousFx/nuget-releaser.git
cd nuget-releaser
dotnet build NugetReleaser.slnx
dotnet test --solution NugetReleaser.slnx
```

### Project Structure

```
nuget-releaser/
├── src/NugetReleaser/          # The tool
└── test/NugetReleaser.Tests/   # Unit and integration tests
```

## Coding Standards

- File-scoped namespaces (`namespace UnambitiousFx.NugetReleaser;`), braces on every block, modern C# syntax.
- `PascalCase` for public members and types, `camelCase` for parameters and locals, `_camelCase` for private fields.
- Nothing that breaks trimming or Native AOT: TOML and JSON go through source-generated contexts, never reflection.
- Errors the user can fix throw `ReleaserException`, which prints without a stack trace. Never print a command line
  that may hold an API key.

## Testing Your Changes

```bash
# All tests
dotnet test --solution NugetReleaser.slnx

# With coverage (Microsoft Testing Platform)
dotnet test --solution NugetReleaser.slnx --coverage --coverage-output-format cobertura --coverage-settings ./test/coverage.settings.xml

# The Native AOT binary
dotnet publish src/NugetReleaser -c Release -r <rid> -p:PublishAot=true
```

## Release Process

(For maintainers)

Releases are automatic. Every push to `main` that passes CI runs [semantic-release](https://semantic-release.gitbook.io/)
(`.github/workflows/release.yml`):

- `feat:` → minor, `fix:`/`perf:` → patch, `!` or a `BREAKING CHANGE:` footer → major; other types don't release.
- Native AOT binaries are built for `linux`, `osx` and `win`, each in `x64` and `arm64` (`.github/workflows/binaries.yml`);
  only once all six are built are the `vX.Y.Z` tag and the GitHub release created, with the archives and their
  `checksums.txt` attached.
- Nothing is published to NuGet for now.

While the repository is private, the release job is skipped.

## License

By contributing to nuget-releaser, you agree that your contributions will be licensed under the [MIT License](LICENSE).
