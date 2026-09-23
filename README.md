# LeagueClanker

A local Windows app that watches your League of Legends game and ranks what to buy next. It works out every player's stats from their champion, level and items, and recomputes whenever anyone's items, level or K/D change, every 2 seconds at most.

The window shows the build you accepted. When the game shifts enough that a different build would clearly be better, it suggests a pivot, and you choose. Decline, and it won't nag about those items again, but "Switch anyway" stays one click away.

It explains itself in plain sentences:

> Enemy team is 87% AP, so I suggest Kaenic Rookern, but Force of Nature is also ok.
>
> Enemy has 3 tanks (Ornn, Braum, Sejuani) at ~131 armor / 1930 HP, so I suggest Lord Dominik's Regards, but Mortal Reminder is also ok.

| Your build | Pivot suggested | After "Keep mine" | Everyone's stats |
|---|---|---|---|
| ![Build tab](docs/screenshots/build.png) | ![Pivot suggestion](docs/screenshots/pivot-suggested.png) | ![Pivot declined](docs/screenshots/pivot-declined.png) | ![Players tab](docs/screenshots/players.png) |

The screenshots come from the demo snapshots in `samples/`, not a live game.

## Running it

You need Windows and the .NET 10 SDK. The first launch downloads item and champion data from Riot's Data Dragon CDN and caches it per patch in `%LOCALAPPDATA%\LeagueClanker`.

```bash
dotnet run --project src/LeagueClanker.App
```

Then start a game. Practice Tool works. Run League in borderless or windowed mode, because exclusive fullscreen hides every other window.

To try it without a game, point it at a saved snapshot:

```bash
dotnet run --project src/LeagueClanker.App -- --demo samples/ap-heavy.json
```

Point it at a folder to replay snapshots in order, one every 10 seconds. `samples/pivot-demo` is a Garen game where the enemy team switches from AD to AP items, which triggers a pivot suggestion:

```bash
dotnet run --project src/LeagueClanker.App -- --demo samples/pivot-demo
```

The CLI prints the same output as text, which is faster when tuning rules:

```bash
dotnet run --project src/LeagueClanker.Cli -- samples/three-tanks.json
```

Run it with no arguments to watch the live game, or with `--items` to list every item and the traits the parser detected. Give it a folder to replay snapshots through the pivot planner. It accepts every pivot, or declines them with `--decline`:

```bash
dotnet run --project src/LeagueClanker.Cli -- samples/pivot-demo --decline
```

To turn one of your own games into a sample, save the API response while in a match:

```bash
curl -k https://127.0.0.1:2999/liveclientdata/allgamedata -o samples/my-game.json
```

## Is it allowed

The app reads two things: Riot's official Live Client Data API on localhost, and Data Dragon. It doesn't read game memory, inject into the client, send input, or draw inside the game. It's an ordinary window next to the game, so Vanguard has nothing to object to.

Riot's third-party policy allows apps that highlight decisions with multiple choices rather than dictate them. That's why every suggestion comes with an alternative and a reason.

## How it works

Everything that matters lives in `src/LeagueClanker.Core`.

`LiveClient/` polls `allgamedata` every 2 seconds. `BuildAdvisor` recomputes when anyone's items, level or kills/deaths change.

`StaticData/` parses Data Dragon. Item stats come from the description's `<stats>` block, because the structured `stats` field leaves out lethality, penetration and ability haste. Item effects like anti-heal ("Wounds"), stasis, spell shields and armor shred are regex matches on the description text. New items on a new patch get picked up without code changes, as long as Riot keeps the wording.

`Analysis/` turns each player into a profile. It estimates their archetype (mage, marksman, bruiser and so on), their AP/AD split, their threat from item gold and K/D, and how tanky, healing, shielding or CC-heavy they are. The AP/AD split starts from the champion and moves with what they buy, so an AP Kai'Sa counts as AP once she has the items.

Every player gets a full stat block in `StatBlock.cs`: AD, AP, attack speed, crit, lethality, % penetration, ability haste, life steal, omnivamp, armor, MR, health, move speed and range. Riot's API only exposes real stats for you, so yours come straight from the game, runes and buffs included. Percentage stats are the exception, because their units vary between game versions, so those still come from items. Everyone else's stats are base stats at their level, using League's growth curve, plus item stats. The Players tab shows all ten. Tankiness works the same way. Early on a tank champion is assumed to go tank. After about two items' worth of gold only what they bought counts, so an Ornn who built Liandry's and Sorcerer's Shoes stops counting as a tank. `samples/tanks-no-defense.json` shows that case. `ChampionKnowledge.cs` holds the hand-kept lists Data Dragon doesn't have: healers, shielders, true damage and heavy crowd control.

`Recommendation/` scores every legendary item for you. The base score is what your archetype values in its stats, minus anything past a cap you've already hit: crit stops at 100% and attack speed at 2.5 per second. Each rule that fires adds a bonus to items that answer it. Ranking is greedy with diminishing returns. Once one MR item answers "enemy is AP", the next MR item gets half the bonus, and items you already own count the same way. Without this, one strong situation fills all six slots with the same kind of item.

`BuildPlanner.cs` holds the build you accepted and compares every new ranking against it. It only suggests a pivot when all of these hold:

- An item enters your next 3 purchases.
- That item has a real in-game reason worth at least 0.5 points, so score noise from kills or levels doesn't count.
- You haven't declined that item this game.
- The swap scores at least 0.5 points better than what it replaces.

Accepting swaps just those items. Declining remembers them for the rest of the game. "Switch anyway" adopts the latest ranking whenever you want. Buying items from your build never counts as a pivot, and a new game or champion starts a fresh build.

### Rules

All in `Recommendation/BuildRules.cs`.

| Rule | Fires when | Pushes |
|---|---|---|
| DamageTypeRule | enemy damage is 60%+ AP or AD | the matching resistance |
| MixedDamageRule | enemy split is 40-60% and you're a frontliner | dual resist and health |
| TankShredRule | 2+ enemies actually built tanky, or high average armor/MR | % penetration and shred if they stacked resists, %HP damage if they stacked health. Lethality loses points |
| SquishyTeamRule | 3+ squishies and you build flat pen | lethality or flat magic pen |
| AntiHealRule | enemy healing, weaker if an ally already has anti-heal | Wounds items, plus the component to rush |
| AntiShieldRule | lots of enemy shields | Serpent's Fang |
| CritDefenseRule | enemy carries at 40%+ crit chance | Randuin's Omen |
| AttackSpeedDefenseRule | enemies attacking 1.1+ times per second | Frozen Heart |
| EnemyPenetrationRule | enemies with 20+ lethality, 30%+ armor pen, 15+ magic pen or 35%+ magic pen | health over the resist they cut through |
| CrowdControlRule | heavy enemy CC | tenacity and cleanse |
| BurstDefenseRule | a fed assassin or several | stasis and spell shields |
| TrueDamageRule | 2+ true damage champions | health |
| NoFrontlineRule | you're a frontliner and no ally is | health and resists |
| TeamDamageSkewRule | your team is 75%+ one damage type | % penetration |

To add a rule, implement `IBuildRule`, return a `Situation` with a label, a sentence, an impact and a match function, and add it to `BuildRules.Default`. Thresholds and weights are constants at the top of each rule. Archetype stat weights and core items are in `ArchetypeProfiles.cs`. Change one, rerun the CLI on the samples, and see what moved.

## Known gaps

- Enemy stats are estimates. Runes, stat shards and stacking passives (Malphite, Cho'Gath, Sion) don't show up in the API.
- Only stats and keyword traits count. Rabadon's Deathcap's AP multiplier, for example, is invisible to the scorer. The core-item bonus papers over some of this.
- Your own archetype comes from Riot's class tags plus a short override list. Off-meta picks like AP Shaco get the wrong item pool. A manual archetype picker in the window would fix it.
- Summoner's Rift only. Arena augments and item win rates are also off-limits under Riot's policy.
- The core-item lists and weights are my best guess for patch 16.18. Real win-rate data per matchup would beat them.

## Legal

LeagueClanker isn't endorsed by Riot Games and doesn't reflect the views or opinions of Riot Games or anyone officially involved in producing or managing Riot Games properties. Riot Games, and all associated properties are trademarks or registered trademarks of Riot Games, Inc.
