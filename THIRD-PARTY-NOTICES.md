# Third-party notices

Artifact Viewer bundles the components below. Each is redistributed under its own
licence, reproduced or linked here as those licences require. Nothing here is
covered by Artifact Viewer's own MIT licence in `LICENSE`.

## Vendored at build time

These live in `assets/lib/`, are embedded in the executable, and are unpacked into
`%LOCALAPPDATA%\ArtifactViewer\render\` at startup. They are shipped as-is with no
modifications. Vendored rather than loaded from a CDN so rendering works offline
and cannot change underneath a given release.

| Component | Version | Licence | Project |
|---|---|---|---|
| Mermaid | 11.16.0 | MIT | https://github.com/mermaid-js/mermaid |
| highlight.js | 11.9.0 | BSD-3-Clause | https://github.com/highlightjs/highlight.js |
| highlight.js `github-dark` theme | 11.9.0 | BSD-3-Clause | https://github.com/highlightjs/highlight.js |

Mermaid's bundle embeds further MIT-licensed components (d3, dagre, khroma,
cytoscape and others); their notices are retained inside `mermaid.min.js` itself,
as its build produces.

### Updating a vendored library

1. Download the replacement into `assets/lib/` under the same filename.
   Mermaid must be the **UMD** build (`dist/mermaid.min.js`) — the ESM entry
   lazy-imports a `chunks/` tree and is not self-contained.
2. Update the version in the table above.
3. Bump `VendoredLibsStamp` in `MainWindow.xaml.cs` so existing installs
   re-extract the new file instead of keeping the old one.

## NuGet dependencies

Restored at build time, not vendored in this repository.

| Package | Licence | Project |
|---|---|---|
| Markdig | BSD-2-Clause | https://github.com/xoofx/markdig |
| Microsoft.Web.WebView2 | Microsoft proprietary (see package) | https://developer.microsoft.com/microsoft-edge/webview2/ |

## Loaded from a CDN at runtime

Only used when viewing Word or Excel files, and only fetched when such a file is
opened. Not redistributed with this application.

| Component | Purpose |
|---|---|
| SheetJS (xlsx) | `.xlsx` rendering |
| docx-preview, JSZip | `.docx` rendering |
