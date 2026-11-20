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

- Press **`F1`** at any time (Play Mode or Edit Mode) to open **Playtest Logger** and automatically snap the current frame, or open via **`Window -> Playtest Logger`** (`Cmd/Ctrl+Shift+L`).
- Use the top **Action** dropdown to switch between **New Log Entry**, **Saved Entries**, **Quick Screenshot**, **Open PlaytestLogs Folder**, and **Open changes.md**.
- **Markers**: Left-click on any screenshot (new or saved) to drop a pin, **drag** an existing pin to reposition it (the saved PNG re-renders from the clean base capture automatically), or **right-click** a pin to delete it.
- **Saved Entries**: Tick **Done** to mark resolved (`- [x]` in `changes.md`), edit categories/descriptions inline, drag markers, or click **Retake Screenshot** to replace an entry's image in place.
