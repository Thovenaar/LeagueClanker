---
name: screenshots
description: Regenerate or add README screenshots of the LeagueClanker window from the demo samples. Use when the UI's look changes (header, panels, colors, new sections) or a new screen needs a screenshot in the README.
---

# README screenshots

Screenshots come from the demo snapshots in `samples/`, never from real games. That way they contain no real player names and can be reproduced exactly.

## Regenerate

From the repository root, on Windows:

```bash
powershell -ExecutionPolicy Bypass -File tools/Capture-Screenshots.ps1
```

The script:

1. Builds the app.
2. Starts the app once per shot.
3. Drives it with UI Automation, clicking by accessible name ("Keep mine", "Players", "Settings", "Apply").
4. Saves each window to `docs/screenshots/`.

Use `-Only build,champselect` for a subset, and `-OutDir <folder>` to preview without overwriting.

The champ select shot and the in-game matchup line need op.gg, so you need an internet connection. Don't use the mouse while it runs.

## Check every image

Open each new PNG and look at it before committing:

- Is the right panel showing, fully loaded? No "Loading...", and runes and icons filled in.
- Is it the current UI? A stale build in another `bin` folder once produced old screenshots.
- Are there no real names? Samples use "You#EUW", "Ally1" and so on.

## Add a new shot

1. Make or reuse a sample. For a game, save one with the app's ⤓ button, which replaces player names, and copy it into `samples/`. For champ select, write a small snapshot like `samples/champselect/top-vs-darius.json`.
2. Add an entry to `$shots` in `tools/Capture-Screenshots.ps1`. Give buttons you need to click an `AutomationProperties.Name` in the XAML, if they don't have one yet.
3. Reference the image from the README. Use the `readme-sync` skill.
