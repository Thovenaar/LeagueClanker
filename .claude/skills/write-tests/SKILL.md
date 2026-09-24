---
name: write-tests
description: Write or update xUnit tests for LeagueClanker.Core. Use whenever you add or change logic in src/LeagueClanker.Core (rules, parsers, advisors, client writers), fix a bug, or touch how external JSON is read.
---

# Writing tests for LeagueClanker

All logic lives in `src/LeagueClanker.Core`, and `tests/LeagueClanker.Core.Tests` covers it with xUnit. The WPF app and the CLI have no tests. Keep logic out of them so it stays testable.

## Where things go

- Add a test class per feature area, next to the existing ones: `RuneTests`, `MatchupTests`, `ChampSelectExtrasTests`, `LeagueClassicTests`, `AugmentAdvisorTests` and so on. Name tests `Subject_WhatItDoes`, for example `CounterPicks_PutChampionsYouPlayFirst_AndSkipWhatYouCantPick`.
- `TestData.cs` is a small synthetic patch with a handful of items and champions. It goes through the real Data Dragon parsers. Use `TestData.Game(allies, enemies)` to build a live game where the first ally is you.
- `Data/` holds real Data Dragon files: `runesReforged.json` and `summoner.json`. They're copied to the output folder, so read them with `Path.Combine(AppContext.BaseDirectory, "Data", ...)`. Add a file there only when the test needs Riot's real structure. Otherwise write a synthetic one inline.

## Rules

1. **No network.** Fake every HTTP call:
   - op.gg: `new OpggClient(new HttpClient(fakeHandler))`, with an `HttpMessageHandler` that returns JSON.
   - Matchup data: a fake `IMatchupData`.
   - Rune pages: a fake `IRunePageStore`.
   - Apply: a fake `IChampSelectWriter`.
2. **External JSON gets a parsing test.** Use a trimmed copy of the real response, just the fields we read, as a raw string literal. When op.gg or the client changes shape, this is the test that should fail.
3. **Test the fallback too.** For every unofficial source, test what happens when it throws or returns nothing: the user still gets an answer and a sentence saying why.
4. **Test thresholds at the edge.** Rules have constants at the top (`MinGames`, `TanksForCutDown`, `MinCounterWinRate`). Put one case just over and one just under.
5. **Deterministic.** The augment simulation uses a stable seed. Keep it that way. No `DateTime.Now` or `Random` without a seed in assertions.
6. **One behavior per test**, with a comment on the line that makes the case interesting (`// the champion you hover`).
7. **A bug fix starts with a failing test** that reproduces it. For example, "item set crashes with no visible enemies" became `Recommend_WorksBeforeAnyEnemyIsVisible`.

## Run

```bash
dotnet test tests/LeagueClanker.Core.Tests
```

Every test must pass before a commit. The release workflow runs the same tests and refuses to publish on a failure. If the LSP reports xUnit types as missing but `dotnet test` passes, the LSP is stale. Ignore it.
