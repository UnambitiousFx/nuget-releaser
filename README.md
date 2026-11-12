# nuget-releaser

[![Build Status](https://github.com/UnambitiousFx/nuget-releaser/workflows/CI/badge.svg)](https://github.com/UnambitiousFx/nuget-releaser/actions)
[![Release](https://img.shields.io/github/v/release/UnambitiousFx/nuget-releaser)](https://github.com/UnambitiousFx/nuget-releaser/releases/latest)
[![codecov](https://codecov.io/gh/UnambitiousFx/nuget-releaser/branch/main/graph/badge.svg)](https://codecov.io/gh/UnambitiousFx/nuget-releaser)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download)

A goreleaser-style release tool for .NET repositories that ship several NuGet packages: it versions, packs and
publishes **each package independently**.

With one shared version, an untouched package gets a new release whenever a sibling changes. nuget-releaser works out
which packages changed since their own last release, computes each new version from conventional commits, and pushes
only those, with their git tags and a GitHub Release.

## 🎯 Features

- **Per-package versioning**: each packable project has its own SemVer version, tracked by its own git tag
  (`Synapse.Core/v1.4.0`). No version lives in source; it is injected at pack time.
- **Change detection**: a package is released only when a commit touched its folder, its `extra_paths` or a shared
  build file (`Directory.Build.props`, `Directory.Packages.props`, `global.json`) since its last tag.
- **Conventional commits**: `feat` → minor, `fix`/`perf` → patch, `!` or a `BREAKING CHANGE:` footer → major.
- **Dependency propagation**: when a package is released, every package that references it through `ProjectReference`
  is re-released against the new version; a major bump cascades as at least a minor bump.
- **Plan first**: `nuget-releaser plan` prints what would be released and why, without touching anything; `--json`
  gives the same plan to other tools.
- **Idempotent**: re-running after a partial failure skips packages already on the feed, existing tags and existing
  GitHub Releases.
- **Native AOT ready**: starts in milliseconds in CI.

## 📦 Installation

Download the archive for your platform from the [latest release](https://github.com/UnambitiousFx/nuget-releaser/releases/latest)
and put `nuget-releaser` on your `PATH`. Each archive holds one self-contained Native AOT executable: no .NET runtime is
needed to run it (the .NET SDK is still needed to pack your packages).

| OS | x64 | arm64 |
|---|---|---|
| Linux | `linux-x64.tar.gz` | `linux-arm64.tar.gz` |
| macOS | `osx-x64.tar.gz` | `osx-arm64.tar.gz` |
| Windows | `win-x64.zip` | `win-arm64.zip` |

```bash
# example: Linux x64, checked against checksums.txt
version=0.1.0
curl -sSLO "https://github.com/UnambitiousFx/nuget-releaser/releases/download/v$version/nuget-releaser-$version-linux-x64.tar.gz"
curl -sSL "https://github.com/UnambitiousFx/nuget-releaser/releases/download/v$version/checksums.txt" | sha256sum --check --ignore-missing
tar -xzf "nuget-releaser-$version-linux-x64.tar.gz" nuget-releaser
```

The tool is not published to NuGet as a .NET tool yet.

## 🚀 Quick start

```bash
# what would be released, and why
nuget-releaser plan

# pack the released packages into artifacts/nuget-releaser, without publishing anything
nuget-releaser release --dry-run

# pack, push to the feeds, push the tags and create the GitHub Release
NUGET_API_KEY=... GITHUB_TOKEN=... nuget-releaser release
```

```text
2 of 5 package(s) to release.
  Synapse.Abstractions  1.3.0 (unchanged)
  Synapse.Core          1.3.0 → 2.0.0
                          major: feat!: drop the legacy pipeline (4f2c1aa)
  Synapse.Http          1.1.2 → 1.2.0
                          dependency Synapse.Core → 2.0.0
```

The history must be complete: in GitHub Actions, check out with `fetch-depth: 0`.

## ⚙️ Configuration

Every key is optional: without a `nuget-releaser.toml`, every tracked project with `IsPackable=true` is a package and
the defaults below apply.

```toml
[defaults]
tag_format = "{package}/v{version}"
initial_version = "0.1.0"          # version of a package that has no tag yet
shared_paths = ["Directory.Build.props", "Directory.Packages.props", "global.json"]

[[package]]
project = "src/Synapse.Http/Synapse.Http.csproj"
extra_paths = ["shared/http"]      # more folders whose changes release this package

[github]
release_mode = "grouped"           # or "per-package"

[[feed]]
name = "nuget.org"
source = "https://api.nuget.org/v3/index.json"
api_key_env = "NUGET_API_KEY"      # environment variable holding the key
```

| Environment variable | Used for |
|---|---|
| `NUGET_API_KEY` (or each feed's `api_key_env`) | `dotnet nuget push` |
| `GITHUB_TOKEN` | Creating GitHub Releases (skipped when absent) |
| `GITHUB_REPOSITORY` | The `owner/repo` to release in; read from the `origin` remote otherwise |

For [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing), run the `NuGet/login`
action first and pass its `NUGET_API_KEY` output as the environment variable.

## 🗺️ Status

Early development. Planned next: pre-release channels, per-package `CHANGELOG.md`, versions read from the NuGet feed
(`version_source = "feed"`), changesets, a GitHub Action wrapper and a .NET tool package on NuGet.

## 🤝 Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). To report a vulnerability, see [SECURITY.md](SECURITY.md).

## 📄 License

[MIT](LICENSE)
