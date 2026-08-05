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
  blocks and ```mermaid diagram fences (both work offline). Prefer this for
  reports and summaries.
- **HTML** (`.html`) — full Chromium, JavaScript and CDN scripts allowed.
  Prefer this for dashboards, charts, and interactive content. Dark background
  suggested (the viewer chrome is dark).
- **PDF, SVG, PNG/JPG/GIF/WebP** — rendered natively.
- **Word (`.docx`) / Excel (`.xlsx`)** — rendered via CDN libraries (needs
  internet). Fine for viewing dropped files; prefer md/html for what you author.
- **CSV/TSV** — rendered as a table (parsed in-app, handles quoted fields with
  commas and newlines). Good for tabular query results/exports.
- **Jupyter notebooks** (`.ipynb`) — markdown cells rendered, code highlighted,
  outputs shown (text, PNG/JPEG figures, HTML, tracebacks). Read-only, nothing
  executes. Fine for handing over an analysis complete with its charts.
- **Code, config, data, and log files** (`.py` `.js` `.ts` `.cs` `.yaml`
  `.json` `.txt` `.log` `.ps1` `.sh` and more) — syntax-highlighted on the same
  dark page as everything else. Useful for showing a script, a JSON payload or a
  log excerpt as a deliverable.
- **SQL scripts** (`.sql`) — batches split on `GO` with alternating backgrounds,
  a sticky header naming the object in view, and a jump index of every
  definition plus any `-- 1.` numbered steps. Worth knowing when writing one:
  separate batches with `GO` and number the steps inside a long procedure, and
  the script becomes navigable rather than a wall.
- **Video/audio** (`.mp4` `.webm` `.mp3` `.wav`) — native playback.

**Seeing what I see:** the viewer writes the path of the currently displayed
file to `%LOCALAPPDATA%\ArtifactViewer\current.txt`. When I say "look at the
current doc", "the one I'm viewing", or similar — read that file to get the
path, then read the file it points to. The full tab list (every artifact with
`open`/`pinned`/`current` flags and timestamps) is in
`%LOCALAPPDATA%\ArtifactViewer\tabs.json` — use it for "what's in the viewer",
"copy all open tabs to X", or targeting a specific tab. **Pinned** artifacts sit
in their own row and are the ones I keep returning to, so prefer them when I
refer to a document vaguely.

Each tab also carries an **`origin`**: `mine` or `team`. A `team` artifact came
from a shared folder that colleagues also write to — **never overwrite or delete
one**, and don't count them as my work when summarising what's in the viewer.
Write new artifacts into my own watch folder and use `share` to send a copy over,
rather than writing into the team folder directly.

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
- `pdf [pdf-path]` — export the currently rendered artifact to PDF, defaulting
  to the same name beside the source. Use when I ask for a PDF, or for something
  to send/share. Prints the live render through a print stylesheet (light page,
  repeating table headers, page numbers), so the PDF matches what's on screen.
  Never prompts. Not available for `.pdf`/audio/video artifacts. I can do the
  same by right-clicking the tab → *Export to PDF…*.
- `copy [file]` — put an artifact's source on my clipboard (the named one, shown
  on the way, or the current one). Use it when I say "copy that" or when I'm
  about to paste something elsewhere — a SQL script into SSMS, a table into an
  email. Text-ish artifacts copy their source text, images copy the bitmap,
  and `.pdf`/Office/media return an error since there is nothing to copy.
- `share [--move] [file]` — put an artifact into my **team folder**, a shared
  folder colleagues also watch. Use it when I say "share this with the team",
  "send this to Xiaoyi", or similar. **Copies** by default, leaving mine where it
  is; only pass `--move` if I actually said to move it, since moving takes the
  artifact out of my own folder. Errors if I haven't set a team folder — tell me
  to set one with the 👥 button rather than trying to create one.
- `focus [file]` — raise the viewer window, optionally showing a file on the
  way. Use when the viewer is likely buried behind an editor and I need to look
  at something. Note: launching the exe again does *not* focus the running
  window, it starts a second instance (intentional — two watch folders at once).

After sending a command, wait ~1–2 seconds and read `command-result.txt` to
confirm it worked.

**Verifying your own output:** `capture` screenshots the render, so you can check
a chart or table actually looks right before telling me it's done. To inspect an
exported PDF the same way, `show` it and then `capture` — the viewer renders PDFs
natively.

**Conventions:**
- Use short, descriptive kebab-case filenames (`db-schema-diagram.md`,
  `perf-report.html`) — the filename is the tab title in the viewer.
- Overwriting the same file live-updates the view; writing a new filename adds
  a new tab. Prefer overwriting when iterating on one deliverable, new files
  for distinct deliverables.
- Files are user-visible deliverables, not scratch space — don't write
  temporary or intermediate files there.
