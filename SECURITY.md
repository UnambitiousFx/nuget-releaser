# Security Policy

## Supported versions

Security fixes are released for the latest minor version of the current major version.

| Version | Supported |
|---|---|
| 0.x (latest minor) | ✅ |
| Older versions | ❌ |

Releases are automatic: a fix merged on `main` is published as a GitHub release within minutes. Update to the latest version
before reporting, in case the issue is already fixed.

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions or pull requests.**

Report them privately through GitHub: open the [Security tab](https://github.com/UnambitiousFx/nuget-releaser/security) of
this repository and click **Report a vulnerability**
([direct link](https://github.com/UnambitiousFx/nuget-releaser/security/advisories/new)).

Please include:

- the version of nuget-releaser and the platform of the binary (for example `linux-arm64`);
- the .NET version, the operating system and the CI system, if any;
- a description of the issue and its impact;
- the smallest `nuget-releaser.toml` and repository layout that reproduce it (with every secret removed).

## What to expect

UnambitiousFx is maintained on a best-effort basis by a small team.

- We aim to acknowledge your report within **7 days**.
- We will confirm whether we consider it a vulnerability, and keep you informed while we work on a fix.
- Once a fixed version is released, we publish a GitHub security advisory and credit you, unless you prefer to stay
  anonymous.

Please give us a reasonable time to release a fix before disclosing the issue publicly.

## Scope

nuget-releaser handles publishing credentials and decides what is pushed to a public feed. In scope, for example:

- An API key or a GitHub token written to the console, a log, a package or any file.
- A package pushed to a feed, or a tag or release created, that the plan did not list.
- A package published with another package's content or version.

Out of scope: vulnerabilities in the packages a repository publishes with the tool, in .NET, git or the NuGet client
themselves, or in third-party dependencies (report those upstream; we will update the dependency).
