# LeagueClanker

A local Windows app that advises League of Legends builds, runes and picks. .NET 10, WPF.

## Layout

- `src/LeagueClanker.Core` has all logic, and has no UI. It's the only project the tests cover.
  - `LiveClient/`: the in-game Live Client Data API.
  - `LeagueClient/`: the League client's local API (champ select, runes, spells, item sets).
  - `StaticData/`: Data Dragon.
  - `Analysis/`, `Recommendation/`: the item advisor.
  - `Augments/`: ARAM: Mayhem.
  - `Runes/`, `Spells/`, `ItemSets/`, `Matchups/`, `Opgg/`: champ select.
- `src/LeagueClanker.Vision` has screen capture and OCR for augment offers.
- `src/LeagueClanker.App` is the WPF window. View models sit next to `MainWindow.xaml`.
- `src/LeagueClanker.Cli` prints the same advice as text. Use it to tune rules and check external data.
- `tests/LeagueClanker.Core.Tests` uses xUnit. `TestData.cs` has a synthetic patch; `Data/` has trimmed real Data Dragon files.
- `samples/` has demo snapshots, replayed with `--demo` (games) and `--champselect` (champ select).
- `tools/Capture-Screenshots.ps1` regenerates the README screenshots.

## Commands

```bash
dotnet build LeagueClanker.slnx
dotnet test tests/LeagueClanker.Core.Tests
dotnet run --project src/LeagueClanker.App -- --demo samples/ap-heavy.json
dotnet run --project src/LeagueClanker.Cli -- --runes Jinx --position bottom
```

## Rules that aren't obvious from the code

- **Riot policy.** Never reveal what champ select hides: player names in ranked, enemy picks in blind pick, enemy summoner spells. No enemy timers.
- **Augment win rates.** Riot asks apps not to show augment win rates in game. The owner decided (2026-09-24) that this local app uses them anyway: arammayhem.com's Mayhem win rates, behind the *community augment stats* setting, on by default. Keep them behind that setting, and keep the README's *Is it allowed* section honest about it. Arena items still get no win rates.
- **Unofficial sources.** op.gg's JSON API and the League client's local API are unofficial. Every call must fail soft: log it with `Log.Error`, then fall back to LeagueClanker's own rules. Say so in a sentence the user sees.
- **Never overwrite user data.** Don't overwrite user data the user didn't select. Rune pages go to the current page only if it's editable. Item sets are read, merged and written back with the user's own sets untouched.
- **User-facing text.** Plain sentences with concrete numbers: "Enemy team is 87% AP, so I suggest X, but Y is also ok." No em dashes, no hype.
- **Match the surrounding code.** Short XML doc summaries, and comments that explain why rather than what.
- **Reproduce before fixing.** Save a snapshot (the ⤓ button, or `samples/`) and replay it, rather than guessing.

## Skills

Use the project skills in `.claude/skills/`:

- `write-tests` when adding or changing logic.
- `readme-sync` after any change a user can see.
- `screenshots` when the window's look changes.
- `verify-external-apis` when op.gg, Data Dragon or the wiki might have changed.
- `publish-release` to publish a version.
