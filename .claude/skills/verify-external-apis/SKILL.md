---
name: verify-external-apis
description: Check that the external data LeagueClanker depends on (op.gg's JSON API, Data Dragon, the League Wiki augment module) still has the shape the parsers expect. Use after a new League patch, when users report missing runes, spells or matchups, or before a release that touches those parsers.
---

# Verifying external data

LeagueClanker reads four outside sources. None of them has a contract with us:

| Source | Read by | Used for |
|---|---|---|
| Data Dragon (`ddragon.leagueoflegends.com`) | `StaticData/DataDragonClient.cs` | items, champions, runes, summoner spells |
| op.gg (`lol-api-champion.op.gg`) | `Opgg/OpggClient.cs` | rune pages, spells, skill order, starting and core items, role rates, matchups |
| League Wiki (`Module:MayhemAugmentData/data`) | `Augments/AugmentDataClient.cs` | ARAM: Mayhem augments |
| League client local API | `LeagueClient/LeagueClientApi.cs` | champ select, rune pages, item sets |

## Quick health check with the CLI

Each command should finish without "didn't answer" notes, and show plausible numbers:

```bash
dotnet run --project src/LeagueClanker.Cli -- --items
dotnet run --project src/LeagueClanker.Cli -- --augments
dotnet run --project src/LeagueClanker.Cli -- --runes Jinx --position bottom
dotnet run --project src/LeagueClanker.Cli -- --matchup - --position top --enemies "Darius;Amumu;Caitlyn"
```

What healthy output looks like:

- **`--items`** lists 100+ legendaries, with stats and traits filled in.
- **`--augments`** reports 200+ augments.
- **`--runes`** prints `Source: op.gg` with games in the tens of thousands, spells, a skill order, and an item set with starting items.
- **`--matchup`** guesses Darius top, and lists counter picks with 200+ games each.

## When something broke

1. Fetch the raw response, for example `curl -s "https://lol-api-champion.op.gg/api/global/champions/ranked/222/adc?tier=all"`. Compare it with what the parser reads.
2. Fix the parser, and keep it tolerant: skip unknown or missing fields rather than throwing.
3. Update or add the parsing test with a trimmed copy of the new shape. Use the `write-tests` skill.
4. If a rune or keystone was renamed, update `KeystoneFit` and the rule templates in `RuleRuneSource.cs`. Rule pages fall back to the first rune of a row, so they keep working meanwhile.
5. If the client API changed, it can only be verified in a real champ select. Ask the user to save a snapshot with ⤓ and send it.

Never swallow a failure silently. Each fallback logs with `Log.Error`, and shows a sentence in the UI.
