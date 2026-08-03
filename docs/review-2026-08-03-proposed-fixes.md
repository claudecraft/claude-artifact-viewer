# Proposed fixes from Fable's 2026-08-03 review

Status: **proposed, not implemented** — written for Opus to review before any code
changes. The working tree was left untouched. Full review narrative is in the
artifact viewer as `artifact-viewer-code-review.md`; this doc is the actionable
subset with implementation sketches.

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

**Review question for Opus:** should the early state writes
(`WriteCurrentDocState` / `WriteTabsState`, currently before the ext dispatch)
move to *after* the successful navigate, so `current.txt` never claims a doc
that failed to render (e.g. `ReadTextWithRetry` returning null)? That's a
behavior change beyond the race fix; Fable leans yes but flags it as separate.

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

**Follow-on this unlocks (not this round):** `ParseDelimited`,
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

## Suggested order

2 → 1 (both small, both bugs, independent) → 4 (before the next feature) →
3 (one README paragraph, any time). Verify with `dotnet build` and a manual
pass: rapid tab-switching across md/csv/code artifacts, then two
back-to-back `show` commands through the control channel.
