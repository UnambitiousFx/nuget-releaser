# Docs

`docs/` (the pages) and `sidebars.ts` are published at <https://unambitiousfx.com/nuget-releaser/> by [UnambitiousFx.github.io](https://github.com/UnambitiousFx/UnambitiousFx.github.io), which builds every UnambitiousFx project into one Docusaurus site.

- `main` is served as the latest major. Each older major is served from its latest release tag that has docs.
- Every Release run redeploys the site, so merged changes go live a few minutes later, including `docs:` commits that do not release.
- `llms.txt` and `llms-full.txt` are generated under `/nuget-releaser/` (configured in the site repo).

`docs/index.mdx` started as a copy of the root `README.md`: when one changes, update the other.

## Preview

From a clone of UnambitiousFx.github.io next to this repository:

```bash
pnpm install
NUGET_RELEASER_REPO=../nuget-releaser pnpm start
```

This serves your working tree, uncommitted changes included. `pnpm build` also checks links: the site uses `onBrokenLinks: "throw"`, so a broken link here blocks the next deploy of the whole site.
