# Artifact Viewer — instructions for Claude Code

Paste the section below into `~/.claude/CLAUDE.md` (create it if missing) on any
machine where the Artifact Viewer runs. Every Claude Code session on that machine
will then know how to use the viewer.

---

## Artifact Viewer (local artifacts)

I run **Artifact Viewer**, a local app that watches a folder and instantly
renders files dropped into it (like Claude Desktop artifacts). Use it whenever a
visual/rendered deliverable would be clearer than terminal text — reports,
summaries, dashboards, diagrams, mockups, charts — or when I say things like
"show me", "as an artifact", or "in the viewer".

**Default behavior for substantial analyses:** whenever you produce a problem
analysis, comparison, or report that is long or has tables/diagrams, don't put
the full version in the terminal — terminal output scrolls away and I lose it.
Write the full analysis as a markdown file to the watched folder, and keep your
terminal reply to a short summary plus a pointer ("full analysis in the
viewer: <filename>"). If the analysis evolves during the session, overwrite the
same file so the viewer live-updates.

**How to use it:** simply write the file to the watched folder:
`%USERPROFILE%\Documents\claude_artifacts` by default — check
`%LOCALAPPDATA%\ArtifactViewer\settings.json` for the configured location
(create the folder if it doesn't exist). The viewer picks it up automatically —
no other action needed. If the app isn't running, launch it first:
`<clone folder>\bin\Debug\net10.0-windows\ArtifactViewer.exe`
(build with `dotnet build` from the clone folder if the exe is missing).

**Supported formats:**
- **Markdown** (`.md`) — GitHub-style rendering with syntax-highlighted code
  blocks and ```mermaid diagram fences (both CDN-based, need internet). Prefer
  this for reports and summaries.
- **HTML** (`.html`) — full Chromium, JavaScript and CDN scripts allowed.
  Prefer this for dashboards, charts, and interactive content. Dark background
  suggested (the viewer chrome is dark).
- **PDF, SVG, PNG/JPG/GIF/WebP, txt, json** — rendered natively.
- **Word (`.docx`) / Excel (`.xlsx`)** — rendered via CDN libraries (needs
  internet). Fine for viewing dropped files; prefer md/html for what you author.
- **CSV/TSV** — rendered as a table. Good for tabular query results/exports.
- **Code, config, and log files** (`.py` `.js` `.ts` `.cs` `.sql` `.yaml`
  `.log` `.ps1` `.sh` and more) — syntax-highlighted. Useful for showing a
  script or log excerpt as a deliverable.
- **Video/audio** (`.mp4` `.webm` `.mp3` `.wav`) — native playback.

**Seeing what I see:** the viewer writes the path of the currently displayed
file to `%LOCALAPPDATA%\ArtifactViewer\current.txt`. When I say "look at the
current doc", "the one I'm viewing", or similar — read that file to get the
path, then read the file it points to. The full tab list (every artifact with
`open`/`current` flags and timestamps) is in
`%LOCALAPPDATA%\ArtifactViewer\tabs.json` — use it for "what's in the viewer",
"copy all open tabs to X", or targeting a specific tab.

**Driving the viewer (control channel):** write a single line to
`%LOCALAPPDATA%\ArtifactViewer\command.txt`; the viewer executes it, deletes the
file, and writes the outcome to `command-result.txt` (JSON: command, status
ok/error, detail). Commands:
- `capture [png-path]` — screenshot the currently rendered artifact (the actual
  render, not the source) to the given path, or `capture.png` in the same folder
  if omitted. Use this to *see* what the user sees — e.g. to verify a chart or
  layout you just wrote actually renders correctly.
- `show <file>` — bring an artifact on screen (bare filename resolves in the
  watch folder; reopens a closed tab). Use when telling the user "see <file>".
- `scroll-to <heading-or-#id>` — scroll the current artifact to a heading
  (case-insensitive substring) or anchor id. Use with "look at section X".

After sending a command, wait ~1–2 seconds and read `command-result.txt` to
confirm it worked.

**Conventions:**
- Use short, descriptive kebab-case filenames (`db-schema-diagram.md`,
  `perf-report.html`) — the filename is the tab title in the viewer.
- Overwriting the same file live-updates the view; writing a new filename adds
  a new tab. Prefer overwriting when iterating on one deliverable, new files
  for distinct deliverables.
- Files are user-visible deliverables, not scratch space — don't write
  temporary or intermediate files there.
