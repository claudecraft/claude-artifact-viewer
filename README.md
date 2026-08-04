# Artifact Viewer

[![Latest release](https://img.shields.io/github/v/release/claudecraft/claude-artifact-viewer?label=download)](https://github.com/claudecraft/claude-artifact-viewer/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/claudecraft/claude-artifact-viewer/total)](https://github.com/claudecraft/claude-artifact-viewer/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**Local artifacts for Claude Code on Windows.** A small WPF + WebView2 app that
watches a folder and instantly renders any file dropped into it — so Claude
Code gets the "artifact panel" experience from the Claude desktop app.

### ⬇ [Download ArtifactViewer.exe](https://github.com/claudecraft/claude-artifact-viewer/releases/latest/download/ArtifactViewer.exe)

One file, ~63 MB, run it. The size is the bundled .NET runtime — there is nothing
to install, unzip, or configure. Windows shows *"Windows protected your PC"* on
first run because the file isn't code-signed: **More info → Run anyway**
([why](#is-it-safe)). Prefer to build it yourself? [Jump to the source](#build-from-source).

![Artifact Viewer screenshot](docs/screenshot.png)

Tell Claude Code to write its outputs — reports, analyses, dashboards,
diagrams — into the watched folder and they appear in the viewer as they're
written, live-updating as Claude iterates. No more losing a great analysis
table to terminal scrollback.

## Features

- **Watches a folder** — new and changed files appear instantly (debounced,
  retries on locked files)
- **Renders** markdown (GitHub-style via Markdig, syntax-highlighted code
  blocks, ```mermaid diagrams), HTML (full Chromium, JS included), PDF, SVG,
  images (png/jpg/gif/webp/bmp/ico/avif), txt, json, **video/audio**
  (mp4/webm/mp3/wav), **Word (.docx) and Excel (.xlsx)** via
  docx-preview/SheetJS (CDN-based, no Office install needed; multi-sheet
  workbooks get sheet tabs), **CSV/TSV** as tables (RFC 4180 parsing, so quoted
  fields containing commas and newlines survive), and **source/config/log
  files** (.py .js .ts .cs .sql .yaml .log .ps1 and ~20 more) with syntax
  highlighting
- **Jupyter notebooks** (`.ipynb`) — markdown cells rendered, code cells
  highlighted, and outputs shown beneath them: stream text, `text/plain`
  results, embedded PNG/JPEG figures, HTML output, and tracebacks with the
  terminal colour codes stripped. Read-only; nothing is executed.
- **Works offline** — mermaid and highlight.js are bundled into the exe, not
  fetched from a CDN, so markdown, diagrams and code render on a plane and a
  release renders identically forever. Only Word/Excel reach the network.
- **Zoom** — `Ctrl +` / `Ctrl -` / `Ctrl 0`, persisted between sessions and
  reapplied as you move between artifacts.
- **Update notice** — checks GitHub for a newer release at most once a day and
  shows a dismissible banner. It sends nothing but a User-Agent; turn it off
  with `"checkForUpdates": "false"` in `settings.json`.
- **Tabs** — one per file, ordered oldest → newest; hover for a ✕ that hides
  the tab without touching the file. Closed state persists across restarts;
  a closed file reopens automatically if it's rewritten. Right-click for
  **Close other tabs** / **Close all tabs**.
- **Pinned tabs** — right-click → *Pin tab* moves an artifact to its own row
  above the others, VS-style. A pinned tab keeps its place when new artifacts
  arrive, survives *Close all tabs*, and persists across restarts. The row is
  hidden until something is pinned.
- **◀ ▶ arrows** (also `Alt+Left` / `Alt+Right`) step through files by date;
  the title bar shows the current file, its timestamp, and position (`3 / 7`)
- **☰ sidebar** — full file list with timestamps; closed docs appear greyed
  and click to reopen; hover a row for a 🗑 that deletes the file (to the
  Recycle Bin)
- **📌 always-on-top** pin, **📁** opens the watched folder
- **📥 Keep** — copies the current file to a configured "keep folder"
  (right-click to set it). The watch folder is a scratchpad; Keep is the
  one-way valve to your durable store (an Obsidian vault inbox, a project
  docs folder, wherever). Never overwrites — collisions get a numbered suffix.
- **Drag & drop** — drop files anywhere on the window to copy them into the
  watched folder and display them (name collision = overwrite, i.e. update)
- **Claude can see what you see** — the viewer writes the currently displayed
  file's path to `%LOCALAPPDATA%\ArtifactViewer\current.txt` and the full tab
  list to `tabs.json`, so telling Claude Code "look at the current doc" or
  "copy all open tabs somewhere" just works (the included
  [CLAUDE-PROMPT.md](CLAUDE-PROMPT.md) teaches it the conventions)
- **Copy to clipboard** — right-click a tab → *Copy contents*. Puts the artifact's
  **source** on the clipboard, not the render, so a script or a table pastes straight
  into SSMS, an editor or an email. Images offer *Copy image* and copy the bitmap
  instead; `.pdf`, Office files and media grey the item out. The status line reports
  what landed (`copied 471 lines → clipboard`).
- **Export to PDF** — right-click a tab → *Export to PDF…*. Prints the live
  render through a print stylesheet (light page, repeating table headers, page
  numbers in the footer), so what you hand someone matches what you were
  looking at. No Python, no LaTeX, no pandoc — the viewer already hosts the
  Chromium that does the printing. Skipped for artifacts where it's meaningless
  (`.pdf`, audio, video).
- **Claude can drive the viewer** — a file-based control channel
  (`%LOCALAPPDATA%\ArtifactViewer\command.txt` → `command-result.txt`) with
  six commands: `capture <png-path>` screenshots the current render (Claude
  can verify the chart it just wrote actually looks right), `show <file>`
  brings an artifact on screen, `scroll-to <heading>` jumps to a section,
  `pdf [pdf-path]` exports the current artifact (defaults to the same name
  beside the source; never prompts), `copy [file]` clipboards an artifact's
  source, and `focus [file]` raises the window —
  optionally showing a file on the way — for when the viewer is behind your
  editor. Relaunching the exe won't do this: it starts a second instance,
  deliberately, so you can watch two folders at once.
- **Live reload** — editing the shown file re-renders it; a new file
  auto-displays only if you're already viewing the latest (browsing history
  never gets interrupted)

![Sidebar with a greyed closed doc and the hover delete button](docs/examples/sidebar.png)

## Getting started

### Requirements

Windows 10 or 11, plus the Microsoft Edge **WebView2 runtime** — preinstalled on
Windows 11 and most Windows 10 machines. If it's missing, the app says so on
launch and offers the download page rather than failing silently. Nothing is
written outside your own user folder: settings live in
`%LOCALAPPDATA%\ArtifactViewer\`, and deleting that folder plus the exe removes
every trace. No registry keys, no installer, no services.

### What touches the network

Two things, both easy to avoid, and neither sends any information about you or
your files:

| What | When | Opt out |
|---|---|---|
| GitHub releases API | once a day at most, to notice a new version | `"checkForUpdates": "false"` in `settings.json` |
| CDN libraries for Word/Excel rendering | only when you open a `.docx` or `.xlsx` | don't open those file types |

Markdown, diagrams, code highlighting, CSV, notebooks, images, PDFs and media
all render entirely offline.

### Is it safe?

Fair question for an unsigned 63 MB binary from an account you've never heard of.
What's on offer instead of a signature:

- **The build is public.** Every release is built from its tag by
  [the release workflow](.github/workflows/release.yml) on a GitHub-hosted
  runner, not on anybody's laptop. The log is public, so you can see exactly what
  produced the file.
- **The file is verifiable.** Each release publishes the exe's SHA-256, both in
  the release notes and as an `ArtifactViewer.exe.sha256` asset. Compare it:
  ```
  Get-FileHash ArtifactViewer.exe -Algorithm SHA256
  ```
- **The source is all here**, including the two vendored libraries in
  `assets/lib/`, kept byte-identical to their upstream downloads so you can diff
  them against the CDN copies ([notices](THIRD-PARTY-NOTICES.md)).
- **Or skip the binary entirely** and build from source below — same result.

It isn't code-signed because signing certificates are issued against a verified
legal identity, which this project doesn't have. SmartScreen's warning means
"rarely downloaded", not "known bad".

### Build from source

Requirements: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download),
WebView2 runtime.

```
git clone https://github.com/claudecraft/claude-artifact-viewer
cd claude-artifact-viewer
dotnet run
```

To produce the same single-file exe the releases ship (settings are in the
csproj, so no extra switches):

```
dotnet publish -c Release -r win-x64
```

By default it watches `Documents\claude_artifacts` (created automatically).
The watched folder is a setting: **right-click the 📁 button** to change it
(persisted in `%LOCALAPPDATA%\ArtifactViewer\settings.json`). If the configured
folder no longer exists at startup, a folder picker appears (cancel falls back
to the default). A command-line argument overrides the setting for that launch
without persisting:

```
ArtifactViewer.exe C:\some\folder
```

Pass a **file** instead and it watches that file's folder and opens it selected:

```
ArtifactViewer.exe C:\some\folder\report.md
```

Each launch is its own instance — two folders side by side is supported, so
running the exe again won't raise an existing window (use the `focus` command
for that).

## Scope, and non-goals

One job: **render what Claude just wrote, immediately.** Feature requests get
weighed against that sentence, and things that don't serve it are declined —
not because they're bad ideas, but because the tool stays useful by staying small.

In scope, and welcome: anything that helps you *read* an artifact. Find-in-page,
an outline for long documents, search across artifacts, a light theme, better
print output, more file types, nicer navigation.

Deliberately not planned:

- **Editing.** No saving, no inline changes. Claude writes the file, your editor
  edits it, this renders it. An editor here would be a worse version of both.
- **Cloud sync, accounts, sharing.** Everything is a local folder on your machine.
  That's the point, not a limitation waiting to be lifted.
- **Plugins or an extension API.** They'd trade the small self-contained binary
  for a support surface one person can't maintain.
- **Becoming an IDE.** No terminal, no git integration, no project tree.
- **An Office suite.** `.docx`/`.xlsx` rendering is a convenience for files you
  drop in, not a feature line to develop.
- **Telemetry or analytics.** Nothing is collected, and nothing will be. The only
  network calls are listed under [What touches the network](#what-touches-the-network).

Not a non-goal, just not done yet: **macOS and Linux.** Windows-only today because
it's WPF; cross-platform support is planned.

## Teaching Claude Code to use it

[`CLAUDE-PROMPT.md`](CLAUDE-PROMPT.md) contains a ready-to-paste section for
your `~/.claude/CLAUDE.md`. It tells Claude Code to write visual deliverables
and long analyses into the watched folder (with a short terminal summary
pointing at them) instead of dumping everything into scrollback.

## Usage examples

The pattern is always the same: you talk to Claude Code in the terminal, it
writes a file into the watched folder, and the viewer renders it instantly.
(These assume the [CLAUDE-PROMPT.md](CLAUDE-PROMPT.md) section is installed.)

![Claude Code on the left, the rendered analysis in the viewer on the right](docs/examples/hero-splitscreen.png)

**A long analysis that would drown in scrollback**

> "Compare the three caching strategies we discussed and recommend one."

Claude writes `caching-comparison.md` — full tables, trade-offs, code samples —
and the terminal gets a two-line summary plus *"full analysis in the viewer:
caching-comparison.md"*. Nothing lost to scrollback.

![Analysis rendered in the viewer](docs/examples/example-analysis.png)

**An interactive dashboard**

> "Show me the test-suite timings from this run as a dashboard."

Claude writes `test-timings.html`; it opens as a new tab with a real chart —
full Chromium, so JavaScript and CDN chart libraries just work.

![Dashboard rendered in the viewer](docs/examples/example-dashboard.png)

**Architecture diagrams**

> "Draw the request flow of this service as a diagram."

Claude writes `request-flow.md` with a ```mermaid fence — rendered as an
actual diagram, not ASCII art in the terminal.

![Mermaid diagram rendered in the viewer](docs/examples/example-diagram.png)

**Iterating on one deliverable**

> "Good, but move the auth section up and add a risks table."

Claude overwrites `caching-comparison.md` and the open tab live-updates in
place. One deliverable, one tab, however many revisions.

![The same tab after a live update, now showing Rev 2](docs/examples/example-live-update.png)

**No Claude required**

The viewer renders anything that lands in the folder from any source — save a
PDF from your browser there, `curl -o` an API response, drop in a screenshot.
If it appears in the folder, it gets a tab.

![A raw JSON file rendered natively](docs/examples/example-any-file.png)

## Implementation notes

- Non-markdown files are served through a WebView2 virtual host mapped to the
  watch folder, so Chromium renders them natively and relative asset paths work.
- Markdown is rendered to a cache file (`%LOCALAPPDATA%\ArtifactViewer\render`)
  served via a second virtual host, so the page has a real origin rather than
  `NavigateToString`'s `about:blank`.
- mermaid and highlight.js are embedded as resources and unpacked into that same
  render folder at startup, then loaded as same-origin files. Vendored rather
  than CDN-loaded for three reasons: offline rendering, no network fetch racing
  a PDF export, and a release that renders the same way in five years. Note that
  mermaid's ESM entry can't be vendored as one file — it lazy-imports a
  `chunks/` tree — so the UMD build is used, which sets `globalThis.mermaid`.
  See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) to update a version.
- CSV/TSV is parsed in C# rather than in the page; it previously loaded SheetJS
  (860 KB) from a CDN to split commas.
- Closed-tab state lives in `%LOCALAPPDATA%\ArtifactViewer\closed-tabs.json`.

## License

MIT — see [LICENSE](LICENSE). Not affiliated with Anthropic; "Claude" is a
product of Anthropic, this is just a companion tool built for it (with it,
actually — Claude Code wrote this app).
