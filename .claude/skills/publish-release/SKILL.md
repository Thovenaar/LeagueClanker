---
name: publish-release
description: Publish a new LeagueClanker version as a GitHub release with the single-file exe. Use when a group of features is done and the user wants (or has approved) a release.
---

# Releasing LeagueClanker

Releases are built by `.github/workflows/release.yml`. It tests, builds a self-contained `LeagueClanker.exe` and publishes it as release `v<version>`. Publishing is outward-facing, so only release when the user asked for it or approved it.

## Before

1. **Tests pass:** `dotnet test tests/LeagueClanker.Core.Tests`.
2. **Everything builds:** `dotnet build LeagueClanker.slnx`, with 0 warnings.
3. **README matches:** use the `readme-sync` skill.
4. **Screenshots are current** if the UI changed: use the `screenshots` skill.
5. **External data works** if parsers changed: use the `verify-external-apis` skill.
6. **Choose the version.** Take the latest from `gh release list --limit 1`. Bump the minor version for new features, the patch version for fixes only.

## Release

```bash
git status --short                               # nothing unexpected
git add -A && git commit -m "<what changed>"     # end with the Co-Authored-By line
git push
gh workflow run release.yml -f version=<x.y.z>
gh run list --workflow=release.yml --limit 1     # get the run id
gh run watch <run id> --exit-status
gh release view v<x.y.z> --json url,assets
```

Run `gh run watch` in the background and wait for the notification, rather than polling. If the workflow fails, read the log with `gh run view <run id> --log-failed`, fix the problem, and run the workflow again. The version check refuses a version whose tag already exists, so a failed run can reuse its number.

## After

Report the release link and the exe size. List what's in the release in a few lines, and what's still unverified against a real client.
