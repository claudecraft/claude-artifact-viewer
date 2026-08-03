# Proposed fixes from Fable's 2026-08-03 review

Status: **1, 2, 4 and the SVG framing implemented** (2026-08-03, reviewed and
amended by Opus — see "Implementation notes" at the end). 3 remains proposed. Full
review narrative is in the artifact viewer as `artifact-viewer-code-review.md`;
this doc is the actionable subset with implementation sketches.

Line numbers refer to `MainWindow.xaml.cs` at commit `ba16a5f`.

---

## 1. Rapid tab-switch race in `ShowEntry` (bug, small fix)

**Where:** `ShowEntry`, ~line 1367.

**Problem.** All text renderers write to shared files in the render folder
(`current.html`, `code.html`, …) and `ShowEntry` is invoked fire-and-forget
(`_ = ShowEntry(entry)`). Two quick selections interleave at the `await`
points:

1. Selection A: sets `_renderedPath = A`, awaits `ReadTextWithRetry`.
2. Selection B: sets `_renderedPath = B`, reads, writes `current.html`,
   navigates. Also writes `current.txt` / `tabs.json` saying B is current.
3. A resumes: overwrites `current.html` with A's content, navigates again.

Result: the screen shows **A** while `_renderedPath`, `current.txt`, and
`tabs.json` all say **B**. The control channel and Claude Code's
"look at the current doc" flow consume those files, so the race feeds the
wrong document to the agent, not just the UI.

**Proposed fix:** a generation counter. Everything runs on the dispatcher
thread, so a plain `int` field is sufficient — no interlocking needed.

```csharp
private int _showGeneration;

private async Task ShowEntry(FileEntry entry)
{
    if (!_webReady) return;
    var gen = ++_showGeneration;
    ...
    var text = await ReadTextWithRetry(entry.Path);
    if (gen != _showGeneration) return;   // a newer selection superseded this one
    if (text is null) return;
    ...
    await File.WriteAllTextAsync(rendered, ...);
    if (gen != _showGeneration) return;   // check again before Navigate
    Web.CoreWebView2.Navigate(...);
```

A staleness check goes after **every** `await` in every branch (including
`CopyWithRetry` in the docx/xlsx branch), always before the `Navigate` call.
The synchronous tail (native-render branch) needs no check — it can't be
interleaved.

**Repro / verification recipe.** ~~Compare the title bar against
`%LOCALAPPDATA%\ArtifactViewer\current.txt`.~~ **This recipe does not work**, and
was corrected during implementation: `TxtTitle`, `_renderedPath` and
`WriteCurrentDocState` were all set on adjacent *synchronous* lines with no
`await` between them, so the title bar and `current.txt` can never disagree — not
even on the unfixed build. The observable symptom is the **screen** disagreeing
with the title bar and `current.txt`, i.e. it needs a screenshot (`capture`), not
a file comparison.

Attempts to reproduce it on the unfixed build failed: bursts of `show` commands
at 80 ms and a 3.6 MB markdown probe against a small file at 40 ms both left the
screen, title and state files in agreement. The dispatcher tends to serialise the
two calls in practice. So the fix is justified by inspection (shared render file,
fire-and-forget invocation, `await` between the state write and the navigate),
and the testing below verifies **no regression** rather than the bug's prior
existence — which is consistent with the review's own "low probability, one
baffling report a month" framing.

**Review question for Opus:** should the early state writes
(`WriteCurrentDocState` / `WriteTabsState`, currently before the ext dispatch)
move to *after* the successful navigate, so `current.txt` never claims a doc
that failed to render (e.g. `ReadTextWithRetry` returning null)? That's a
behavior change beyond the race fix; Fable leans yes but flags it as separate.

**Answered: yes, but the question was scoped too narrowly.** Moving only the two
state writes covers half the lie — `TxtTitle`, `TxtDate`, `Title` and
`_renderedPath` were set on the same early lines. And `if (text is null) return;`
is a *deterministic* failure, not a race: a file locked by its writer left the
title bar and `current.txt` naming a document the screen had never shown. So the
whole header-and-state block moved after the render is built, and the failure
path now reports "could not read &lt;name&gt;" in the header — the same place
`BtnKeep_Click` reports "kept →" — leaving the previous document rendered so the
header and the state files keep describing what is actually on screen.

---

## 2. Command channel silently drops a command (bug, small fix)

**Where:** `ProcessCommandFile`, ~line 655.

**Problem.** `_processingCommand` is a plain re-entrancy latch with no
re-check. If a second command is written to `command.txt` while the first is
mid-processing (awaits inside `CmdCapture`/`CmdPdf`/`CmdScrollTo` make this
window wide), the watcher event fires, its handler sees the latch set, and
returns. Nothing ever revisits the file — the command sits unprocessed until
some future filesystem event touches it. Under the documented contract
("write a line, wait ~1–2 s, read `command-result.txt`") a dropped command is
indistinguishable from a hang.

**Proposed fix:** loop until the file stays gone.

```csharp
private async Task ProcessCommandFile()
{
    if (_processingCommand) return;
    var cmdFile = System.IO.Path.Combine(_appDataDir, "command.txt");
    _processingCommand = true;
    try
    {
        // A command written while a previous one was processing fires an event
        // the latch swallows; keep going until the file stays gone.
        while (File.Exists(cmdFile))
        {
            var text = (await ReadTextWithRetry(cmdFile))?.Trim();
            try { File.Delete(cmdFile); } catch (Exception) { }
            if (string.IsNullOrEmpty(text)) continue;
            ... existing verb dispatch and result write, unchanged ...
        }
    }
    finally { _processingCommand = false; }
}
```

**Why this closes the race completely:** all handlers run via
`Dispatcher.InvokeAsync`, so there is no true concurrency — the latch only
guards re-entrancy across `await`s. A write that lands before the loop's final
`File.Exists` check is processed by the loop; a write that lands after it
queues a fresh dispatcher callback, which runs after the current work item
completes — by which point the `finally` has already cleared the latch
(no `await` between the last check and the `finally`). No remaining window.

**Repro / verification recipe.** This one reproduces without timing luck if
the first command is slow. From PowerShell, with a couple of artifacts (`a.md`,
`b.md`) in the watch folder:

```powershell
$cmd = "$env:LOCALAPPDATA\ArtifactViewer\command.txt"
Set-Content $cmd 'pdf'            # slow command: render-settle wait + print
Start-Sleep -Milliseconds 300     # let processing start, then land a second
Set-Content $cmd 'show b.md'      # command while the latch is held
Start-Sleep 5
Get-Content "$env:LOCALAPPDATA\ArtifactViewer\command-result.txt"
```

Today: `command-result.txt` still shows the `pdf` result, `b.md` is never
shown, and `command.txt` is left sitting on disk unprocessed — that's the
drop. After the loop fix: `b.md` is showing, the result file records the
`show`, and `command.txt` is gone. Run it a few times; the pre-fix drop is
near-certain because the `pdf` path awaits render settling for seconds.

Guard against a malformed/empty file spinning the loop: the
`string.IsNullOrEmpty → continue` path re-checks `File.Exists`, and the file
was already deleted, so it exits. An undeletable-but-present file (exotic)
would spin — if Opus wants belt-and-braces, cap the loop at e.g. 10
iterations.

---

## 3. Watch folder is one shared origin for all HTML artifacts (docs only)

**Where:** virtual host mapping, `MainWindow_Loaded` ~line 326.

**Problem.** `artifacts.viewer` maps the entire watch folder as a single
origin with `Allow` access, and CDN/network access is unrestricted. Any
`.html` artifact — or a notebook `text/html` output, which is injected raw
(see the comment at ~line 1682) — can therefore `fetch()` every other
artifact in the folder and exfiltrate it. Drag-and-drop import means
third-party files can land in that origin.

**Proposed fix (this round): documentation, not code.** The trust model
("local file, local render") is fine for self-authored artifacts; the gap is
that it's nowhere stated. Add a short "Security model" note to `README.md`
along the lines of:

> HTML and notebook artifacts render with full Chromium and share one origin
> with the whole watch folder: any HTML artifact can read every other file in
> the folder and reach the network. Treat the watch folder as trusted space —
> don't drop HTML or `.ipynb` files from sources you don't trust.

Code-level hardening (per-file origins, blocking network for the artifacts
host, sandboxed iframes for notebook HTML output) is deliberately out of
scope — it would break legitimate CDN-using dashboards, which are a core use
case. Revisit only if an "open file from anywhere" feature is ever added.

---

## 4. Extract renderers into `Renderers.cs` (refactor, no behavior change)

**Where:** `MainWindow.xaml.cs` lines ~1447–1975 (the `// ---------- Rendering`
tail), plus `MarkdownPipeline` at line 39.

**Problem.** The file is 1,975 lines. The five `Build*Html` functions and
their supporting members are pure statics with zero UI coupling — the cheapest
~700-line extraction available, best done before the next feature grows the
file further.

**Proposed move list** — a new `internal static class Renderers` in
`Renderers.cs`:

| Member | Note |
|---|---|
| `VirtualHost`, `RenderHost` consts | Move here (they're render concerns: base href, stylesheet URLs). See dependency note below. |
| `MarkdownPipeline` | Only used by the builders. |
| `DocumentCss`, `NotebookCss` | |
| `CsvRowLimit`, `NotebookOutputCharLimit` | |
| `AnsiEscape`, `HljsLanguage` | |
| `ParseDelimited`, `BuildCsvHtml` | |
| `BuildNotebookHtml`, `AppendOutputs` | |
| `BuildCodeHtml`, `BuildOfficeHtml` | |
| `BuildMarkdownHtml`, `BuildDocumentHtml` | |

**Stays in `MainWindow`:** `CodeExtensions` / `SupportedExtensions` (file-type
*policy* — which files are artifacts, which expect a render signal),
`ReadTextWithRetry` / `CopyWithRetry` (file IO, also used by the command
channel), and all `ShowEntry` dispatch logic.

**Dependency direction:** `MainWindow` → `Renderers`, never the reverse.
`MainWindow` still needs the host names for
`SetVirtualHostNameToFolderMapping`, navigation URLs, and
`BtnFolder_RightClick`; to keep that diff minimal, alias them:

```csharp
private const string VirtualHost = Renderers.VirtualHost;
private const string RenderHost = Renderers.RenderHost;
```

`using Markdig;` moves from `MainWindow.xaml.cs` to `Renderers.cs` (no other
Markdig use remains in MainWindow).

**Implemented 2026-08-03, ahead of the planned macOS port** — moving a clean seam
across is cheaper than untangling it afterwards with two platform shells to keep
in sync. Done as proposed, with three deviations:

- **No const aliases.** The sketch suggested
  `private const string VirtualHost = Renderers.VirtualHost;` to keep the diff
  small. The ~10 call sites reference `Renderers.VirtualHost` directly instead,
  so the dependency direction is visible at each use rather than hidden behind a
  same-named local const.
- **`ParseDelimited` is `internal`, not `private`**, so the test project planned
  after the port can reach it without `InternalsVisibleTo` gymnastics. The rest
  of the moved members are private except the six `Build*Html` entry points and
  the two host constants.
- **`BuildSvgHtml` moved too** (it postdates the proposal), and `using
  System.Text;` came out of `MainWindow` along with `using Markdig;` — the last
  `StringBuilder` went with the renderers.

`MainWindow.xaml.cs` 2,085 → 1,561 lines; `Renderers.cs` is 558.

**Verified behaviour-preserving by byte comparison, not just by eye.** The
generated pages (`current.html`, `csv.html`, `code.html`, `svg.html`,
`office.html`) were captured from the pre-extraction build at `92a4c8e` and from
the extracted build for the same five artifacts, and hashed: identical, SHA-256
for SHA-256. That check first *failed* — every file differed by one byte per
line, because `Renderers.cs` was authored with LF endings while the repo is CRLF,
and C# raw string literals embed the source file's line endings verbatim. Worth
knowing for the port: a renderer file with the wrong line endings silently
changes every byte of generated output. Converted to CRLF, then identical.

The notebook renderer has no artifact in the test folder to compare against, so
it was exercised functionally instead — a notebook with markdown, code, stream,
`execute_result`, `error` and `raw` cells rendered correctly, with the ANSI
escapes stripped from the traceback and `NotebookCss` present in the output.

**Follow-on this unlocks (still to do):** `ParseDelimited`,
`BuildCsvHtml`, and `BuildNotebookHtml` become trivially testable once they
live in a UI-free class — a small test project covering quoted-field CSV edge
cases and nbformat string-vs-array sources would catch the likeliest quiet
regressions.

---

## Explicitly not proposed (minor findings, noted for completeness)

- Inconsistent path comparison: `PathEq` exists but several sites inline
  `string.Equals(..., OrdinalIgnoreCase)` (`CmdShow`, `CmdPdf`,
  `WriteTabsState`). Cosmetic cleanup, fold into any nearby change.
- `SaveSetting` read-modify-write has no cross-instance locking.
- `capture`/`pdf` accept arbitrary output paths — the control channel is an
  unauthenticated local RPC by design; keep it documented as such.

## Implementation notes (Opus, 2026-08-03)

Amendments made to the proposals above while implementing them:

- **Order flipped.** The doc suggested 2 → 1; they shipped together. `CmdShow`
  reaches `ShowEntry` through the same fire-and-forget path, so fixing the
  dropped command is exactly what makes back-to-back `show` commands *both*
  execute — fixing 2 alone increases exposure to 1.
- **A lock, not just a generation counter.** The counter alone leaves a hole the
  sketch missed: a superseded render that passes its staleness check and *then*
  gets interleaved during `File.WriteAllTextAsync` still collides with the winner
  on the shared render file — two concurrent writes to one path can interleave or
  just throw (silently, under `_ = ShowEntry(...)`). A `SemaphoreSlim(1,1)` keeps
  one render in flight; the counter now only skips work nobody will see.
- **Render signals must stay armed synchronously.** They could not move down to
  the navigate: `ExportPdf` calls `SelectEntry` and then immediately awaits
  `WaitForRenderAsync`, so arming any later than the first `await` would leave an
  export waiting on the *previous* render's already-completed signal and printing
  the page it replaced. Arming stays at the top of `ShowEntry`, with a comment
  recording the coupling.
- **Iteration cap taken** on the command loop (`MaxCommandsPerBatch = 10`). The
  `IsNullOrEmpty → continue` path assumes the delete succeeded, and that delete
  is inside a swallowed `try`.
- **SVG framing added** (not in the original list): Chromium renders a bare
  `.svg` at whatever size the file declares, top-left, against a white page, so a
  600×600 drawing sat in the corner of a white field. `BuildSvgHtml` frames it
  centred on the document background, scaled to fit. Loaded via `<img>`, so
  script inside a dropped SVG stays inert — a small bonus against the trust gap
  in item 3.

**Verified:** `dotnet build` clean; all ten render branches (md, csv, code, docx,
xlsx, pdf, png, html, json, svg) navigate with `current.txt` matching; CSV and SVG
renders eyeballed via `capture`; PDF export produces correctly-sized output for
the document actually on screen (41 KB for an SVG vs 182 KB for `welcome.md`,
confirming it is not printing a stale page). The dropped-command fix reproduces
its bug and its fix exactly as the recipe in item 2 describes — pre-fix the
`show` behind a `pdf` was lost; post-fix both run. The `ShowEntry` race could not
be reproduced either way (see item 1).

## Suggested order

2 → 1 (both small, both bugs, independent) → 4 (before the next feature) →
3 (one README paragraph, any time). Verify with `dotnet build` and a manual
pass: rapid tab-switching across md/csv/code artifacts, then two
back-to-back `show` commands through the control channel.
