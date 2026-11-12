# UnambitiousFx.NugetReleaser — Guidelines

A goreleaser-style .NET tool (`nuget-releaser`) that versions, packs and publishes each NuGet package of a repository
independently. Namespace: `UnambitiousFx.NugetReleaser`. Solution: `NugetReleaser.slnx`.

## Layout

| Path | Purpose |
|------|---------|
| `src/NugetReleaser` | The tool: `Program` (CLI), `ReleaserConfig` (TOML), `Repository` (discovery, tags, commits), `Planner` (versions), `Releaser` (pack, push, tag, GitHub) |
| `test/NugetReleaser.Tests` | Unit tests, plus `ReleaserIntegrationTests` against a real temp git repository and real `dotnet` |

## Principles

- **The plan is the single source of truth**: `Planner` decides every version from data only (no IO); `Releaser`
  only executes it. A dry run must show exactly what a real run does.
- **Idempotent**: every step skips what already exists (`--skip-duplicate`, existing tags, existing releases), so a
  failed run is fixed by re-running it.
- **No version in source**: versions come from git tags (`tag_format`) and are injected through a generated
  `versions.props` (`CustomBeforeMicrosoftCommonProps`), never a global `-p:Version`, which would leak into referenced
  projects.
- **Native AOT-safe**: TOML through `ReleaserConfigContext` (Tomlyn source generator), JSON through
  `JsonSerializerContext`s; never reflection-based overloads.
- **Secrets**: never print or log a command line that may hold an API key.
- **Distribution**: Native AOT binaries only (`linux`/`osx`/`win` × `x64`/`arm64`, `binaries.yml`), attached to the
  GitHub release; the tool itself is not published to NuGet yet. Anything that breaks AOT breaks the release.
- **Modern C#**: file-scoped namespaces, pattern matching, records, collection expressions.

## Conventions

- File-scoped namespaces (`namespace UnambitiousFx.NugetReleaser;`).
- Naming: `PascalCase` (public/types/methods), `camelCase` (params/locals), `_camelCase` (private fields), `IPascalCase` (interfaces).
- Always use braces, even single statements.
- Types are `internal` (the tool has no public API); tests see them through `InternalsVisibleTo`.
- Targets `net11.0` (`LatestTargetFramework` in `build.props`), `RollForward=Major`.
- Central Package Management: add/bump versions in `Directory.Packages.props`, not in `.csproj`.

## Testing

Target 80%+ coverage.

- Name tests `MethodName_Scenario_ExpectedBehavior`.
- AAA = Gherkin (Given-When-Then); comment the sections.
- `[Theory]` + `[InlineData]` / `[MemberData]` for multi-scenario logic.
- Planning rules get unit tests on `Planner`; anything touching git, MSBuild or packing gets an integration test.

## Build & Test

```bash
dotnet build NugetReleaser.slnx
dotnet test --solution NugetReleaser.slnx
```

## Commits & Branches

semantic-release derives the next version from commit messages (see `CONTRIBUTING.md`), so these rules decide what gets released.

- Commits and PR titles follow [Conventional Commits](https://www.conventionalcommits.org/): `type(scope)?: summary`, imperative, lowercase, no trailing period.
- `feat` → minor, `fix`/`perf` → patch. `docs`, `test`, `refactor`, `build`, `ci`, `chore`, `style`, `revert` → no release.
- Breaking change: `type!:` or a `BREAKING CHANGE:` footer → major. Required for any CLI or configuration break.
- PRs are squash-merged with the PR title as the commit message: the PR title is what counts (checked by `pr-title.yml`).
- Branches: `<type>/<issue>-<slug>`, or `<type>/<slug>` without an issue; kebab-case, `type` from the list above.

## Labels

Always label a PR or issue when opening it (`gh pr create --label`, `gh issue create --label`). Use existing labels only (`gh label list`), never create new ones, and add every label that applies.

| Change | Label |
|---|---|
| `feat` / feature request | `enhancement` |
| `fix` / bug report | `bug` |
| `docs` | `documentation` |
| other (`ci`, `chore`, dependencies…) | the closest existing label |
