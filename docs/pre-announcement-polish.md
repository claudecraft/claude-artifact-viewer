# Polish before a public announcement

Deferred deliberately (2026-08-04, at v1.2.0). None of it blocks use — the
project has been shared by link, not announced, and the audience so far is its
author. Each item is here because it costs more to rediscover than to record.

Ordered by what a first-time visitor sees first.

---

## 1. Two README screenshots contradict the app

Both were captured before v1.2.0 and now document behaviour that no longer
exists.

| Image | Shows | Should show |
|---|---|---|
| `docs/examples/sidebar.png` | The tab strip at full window width, so the selected tab sits directly above the sidebar's first row — the same filename twice, stacked. That is the muddle v1.2.0 removed. | The sidebar running from the toolbar to the bottom, tab strip beside it. |
| `docs/examples/example-any-file.png` | A `.json` artifact on Chromium's unstyled white page with its "Pretty-print" checkbox. | The same file highlighted on the app's own dark page. |

`example-any-file.png`'s caption was already updated, so image and text
currently disagree — the caption describes v1.2.0 behaviour and the image
doesn't.

**Commit the fixtures this time.** `docs/examples/` holds only PNGs, so the
six-file demo set the screenshots were taken against
(`integration-test.md`, `artifact-viewer-readme.md`, `caching-comparison.md`,
`test-timings.html`, `request-flow.md`, `api-response.json`) has to be
reconstructed from what's visible in the images. Committing them under
something like `docs/examples/fixtures/` makes every future refresh a rerun
instead of an archaeology exercise — which is the actual reason these two went
stale.

A screenshot can be driven without touching the running instance: launch the
build with a watch folder as its argument, toggle the sidebar through UI
Automation (the ☰ control is a `ToggleButton`), and capture the window rect.
That is how the v1.2.0 layout was verified; worth turning into
`tools/refresh-screenshots.ps1` while the fixtures are being committed.

## 1b. The SQL view has no screenshot

Added in v1.3.0 and described in the README under *Long SQL scripts*, but the
one feature that is purely visual is the one with nothing to look at. It needs a
**synthetic** script — a few procedures with numbered steps and a shared naming
prefix, so the prefix-stripping and the nested step index both show. Not a real
one: whatever gets committed as a fixture is published.

Same fixture problem as item 1, so do them together.

**Fixtures written 2026-08-05** — `docs/examples/fixtures/`, both verified by
rendering and capturing them:

- `warehouse-procs.sql` — mixed kinds (TABLE / VIEW / PROCEDURE / TRIGGER),
  8 objects · 9 steps. Also the adversarial case: a `CREATE TABLE` and a `GO`
  inside a dynamic-SQL string, plus a commented-out `GO`. Neither splits a batch
  and the dynamic `CREATE` doesn't become a ninth object.
- `warehouse-reports.sql` — all procedures on a shared `spWHREPORTS_` prefix,
  5 objects · 4 steps, which is what makes the stripping visible.

**Two fixtures because one can't do both jobs:** `CommonNamePrefix` takes the
prefix shared by *every* named object, so the `t`/`v`/`sp`/`tr` kind letters of a
mixed script cut it to nothing. Mixed-kind rendering and prefix stripping are
mutually exclusive by construction — screenshot whichever sells better, or both.

**Screenshot taken and placed 2026-08-05** — `docs/examples/example-sql.png`,
in the *Long SQL scripts* section. Shot from `warehouse-procs.sql` scrolled to
`spWAREHOUSE_RECONCILE_NIGHTLY` (`scroll-to b7`), so one frame carries the
index, position tracking, nested steps, the sticky header with its line number,
and the dynamic-SQL block that must not split. The prefix-stripping shot was
captured and then dropped rather than committed as an orphan asset the README
never references — regenerate it any time with `show warehouse-reports.sql`
followed by `capture <path>`, which is the point of committing the fixtures.

**This item is done. Item 1 is not** — the two stale screenshots and the
six-file demo set still need reconstructing.

## 2. The trust model is nowhere stated (item 3 of the 2026-08-03 review) — **DONE 2026-08-05**

README now carries *"The watch folder is trusted space"* after the network
table: one origin for the whole folder, HTML artifacts can read every other file
and reach the network, notebook `text/html` is injected raw, so don't drop
untrusted `.html`/`.ipynb` in — including by drag & drop. Says why the trade is
deliberate (CDN dashboards) rather than implying an unfixed hole.

Still proposed, and it is the one deferred item with public-facing risk: an
announcement brings users who did not write the artifacts they render.

`artifacts.viewer` maps the whole watch folder as a single origin with
unrestricted network access, and notebook `text/html` output is injected raw.
Any `.html` artifact can therefore read every other file in the folder and
reach the network — fine for self-authored artifacts, which is the actual use
case, but the README never says so. Drag-and-drop import means third-party
files can land in that origin.

Fix is one README paragraph, wording in
`docs/review-2026-08-03-proposed-fixes.md` section 3. Code-level hardening
stays out of scope: it would break CDN-using dashboards, which are a core use
case.

## 3. `.ps1`, `.bat` and `.cmd` render unhighlighted — **DONE 2026-08-05**

Resolved the cheap way: `powershell.min.js` (4.4 KB) and `dos.min.js` (1.4 KB)
vendored alongside the common bundle, rather than the ~1.2 MB full build for two
languages. Both are 11.9.0, matching the core; they `hljs.registerLanguage` onto
the global, so the script tags must follow `highlight.min.js` — they do, in the
code shell and the markdown shell (a ```powershell fence is common on Windows).
Not added to the SQL shell, which only ever renders `.sql`. `VendoredLibsStamp`
bumped to `hljs-11.9.0+ps+dos` so installed copies re-extract.

Original note follows.

### Original

`HljsLanguage` maps `.ps1` → `powershell` and `.bat`/`.cmd` → `dos`, and the
README advertises `.ps1` among the highlighted formats, but the vendored
`highlight.min.js` is the "common" bundle: it has no `grmr_powershell` and no
`grmr_dos`. The mapping resolves to nothing, the `try` swallows it, and the
file renders as plain text on the dark page.

Fixing it means vendoring a larger highlight.js build (exe size) or adding just
those two language files. Either is a deliberate size-vs-coverage call, not a
typo fix — hence deferred rather than folded into the v1.2.0 rendering work.

## 4. No per-version release notes

`.github/release-notes.md` is static install instructions, so a release page
says nothing about what changed in that version. **Deliberately deferred**: a
changelog written for an audience of one is paperwork.

Cheap when the time comes — every tag so far is annotated with real notes, so
`git tag -l -n99` is the raw material and the job is transcription. Keep
annotating tags and it stays that way.

## 5. The pure renderers still have no tests

Noted as unlocked by the `Renderers.cs` extraction and still not done.
`ParseDelimited` (RFC 4180 quoted fields, embedded newlines) and
`BuildNotebookHtml` (nbformat string-vs-array sources) are where quiet
regressions would live, and both are UI-free statics. `ParseDelimited` is
already `internal` rather than `private` for exactly this.

Lowest priority here — it protects the code, not the first impression.
