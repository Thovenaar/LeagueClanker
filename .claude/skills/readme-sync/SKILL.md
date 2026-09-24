---
name: readme-sync
description: Bring README.md in line with the code after any change a user can see (a feature, setting, CLI flag, data source, mode, limitation or screenshot). Use before every commit that changes behavior, and always before a release.
---

# Keeping the README in sync

The README is the only user documentation. Readers download the exe from it and decide whether the app is allowed. It has to match the code exactly.

## Checklist

Go through the diff since the last release (`git diff v<last>..HEAD --stat`, then read the changed files) and update each section this change affects:

1. **Intro and screenshots.** Does the top still describe what the app does? Is a screenshot needed for a new screen? If so, use the `screenshots` skill.
2. **Running it from source.** Every new demo sample or app flag (`--demo`, `--champselect`, `--scan-image`) gets a command.
3. **CLI.** Every new CLI command or option gets one example command. Keep the usage comment at the top of `src/LeagueClanker.Cli/Program.cs` in step.
4. **Is it allowed.** Update it whenever the app reads a new local API or writes anything into the client.
5. **How it works.** Each feature gets a section explaining the logic in plain words, with its thresholds as numbers (for example "at least 200 games and 51% or more"). Name the class that holds the constants so readers can find them.
6. **Rules table.** Add a row when a rule is added to `BuildRules.Default`.
7. **Known gaps.** Add what isn't done, isn't tested against the real client, or is guessed. Remove gaps that got fixed.
8. **Legal.** Attribute every new external data source (license or "not affiliated").

## Verify

- Run every command you added or changed and check it does what the text says.
- Check that numbers in the text match the constants in the code (`grep` for them).
- Check that image links point to files that exist in `docs/screenshots`.

## Style

Match the existing README. These rules come from the project's writing style:

- Headings in sentence case. No emojis.
- No em dashes. Use periods or commas instead. Don't overuse colons.
- Short sentences, one idea each. Active voice. Say what happens: "Apply overwrites your current page", not "pages are updated".
- Concrete numbers and names over adjectives. No "powerful", "seamless" or "smart".
- Bold only the first words of a bullet that names the item, followed by new detail.
- Describe limits honestly. If something is unverified against a real client, say so.
