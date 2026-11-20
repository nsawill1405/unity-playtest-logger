# Unity Playtest Logger

A lightweight, zero-runtime-overhead Unity Editor window for logging bugs, UI feedback, and gameplay changes while working in **Play Mode** or **Edit Mode**.

Every entry saves directly into `<ProjectRoot>/PlaytestLogs/` (`changes.md` Markdown checklist + `entries.json` + annotated/clean PNG screenshots + Unity Console snapshots), making it effortless to review yourself or feed directly to coding agents.

## Features

- **Dedicated Standalone Window** (`Window -> Playtest Logger` or **`Cmd/Ctrl+Shift+L`**, quick capture on **`F1`**) — stays out of your Scene View and Game View.
- **Captures UI & 3D Views** — grabs either **Game View (including `Screen Space - Overlay` UI Canvases)** or **Scene View** in both Play Mode and Edit Mode.
- **Interactive Adjustable Markers** — click anywhere on a screenshot to place numbered callout pins, **drag pins** to adjust their position at any time, or right-click a pin to remove it. Automatically detects the UI `RectTransform` hierarchy path (`UI: Canvas/...`) or 3D Collider object under each pin.
- **Full Editing & Retake on Saved Entries** — edit categories, descriptions, marker positions/labels, or **retake the screenshot** on any previously saved entry without losing your notes or pins.
- **Markdown + Console Log Snapshots** — writes a clean `- [ ]` / `- [x]` checklist to `PlaytestLogs/changes.md` alongside recent Unity Console logs and stack traces.

## Installation (Unity Package Manager)

1. Open Unity and go to **`Window -> Package Manager`**.
2. Click **`+ -> Add package from git URL...`**.
3. Paste:
   ```text
   https://github.com/nsawill1405/unity-playtest-logger.git
   ```

## Usage

- Press **`F1`** at any time (Play Mode or Edit Mode) to open **Playtest Logger** and snap the view you were just looking at (Game View or Scene View), or open it via **`Window -> Playtest Logger`** (`Cmd/Ctrl+Shift+L`).
- Switch between the **+ New Entry** and **Saved Entries** tabs at the top. The **View** toggle picks Game View or Scene View; with **Auto** on, the draft screenshot follows whichever of the two you focus (it stops once you have placed pins, so it never swaps the image under them).
- **Actions** menu: **Quick Screenshot Now** (saves a separate entry without touching your draft), **Pause Play Mode on F1 Capture**, **Open PlaytestLogs Folder**, **Open changes.md**.
- **Markers**: Left-click a screenshot (new or saved) to drop a numbered pin, **drag** a pin to move it, or **right-click** it to delete it. Saved PNGs are re-rendered from the clean capture with matching numbers drawn next to each pin.
- **Saved Entries**: Tick the checkbox to mark an entry done (`- [x]` in `changes.md`), edit category/description/pin labels inline, use **Select Entry** to jump to one, or **Retake Screenshot** to replace its image in place. **Delete** asks for confirmation and removes the entry's screenshots and console log too.
- Your draft (screenshot, pins, description) survives script reloads and entering Play Mode, and window toggles are remembered between sessions.

## Output

```text
PlaytestLogs/
  changes.md        Markdown checklist, newest first
  entries.json      Source of truth for the window
  Screenshots/      <id>.png (with pins) and <id>_clean.png
  ConsoleLogs/      <id>.log (recent Console messages + stack traces)
```

If `entries.json` can't be parsed, it is moved aside to `entries.json.<timestamp>.bak` (with a Console warning) instead of being overwritten.
