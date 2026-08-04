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

## 2. The trust model is nowhere stated (item 3 of the 2026-08-03 review)

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

## 3. `.ps1`, `.bat` and `.cmd` render unhighlighted

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
