using System.Text;
using System.Text.Json;
using Markdig;

namespace ArtifactViewer;

/// <summary>
/// Builds the HTML pages the viewer renders artifacts into. Pure static functions
/// with no UI coupling — nothing here touches WPF, WebView2 or the file system, so
/// it is the part of the app that survives a port to another shell unchanged, and
/// the part that is cheap to put under test.
///
/// Split out of MainWindow at ~2,000 lines. The dependency runs one way:
/// MainWindow → Renderers, never the reverse. Which files count as artifacts
/// (CodeExtensions / SupportedExtensions) is *policy* and stays in MainWindow;
/// how one is turned into a page lives here.
/// </summary>
internal static class Renderers
{
    /// <summary>Serves the watch folder — a real https origin, so markdown images and relative asset paths resolve.</summary>
    internal const string VirtualHost = "artifacts.viewer";

    /// <summary>Serves the render cache folder: the generated pages and the vendored mermaid/highlight.js.</summary>
    internal const string RenderHost = "render.viewer";

    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    // ---------- Delimited text ----------

    /// <summary>Row cap — past this the table stops being readable and starts being slow.</summary>
    private const int CsvRowLimit = 5000;

    /// <summary>
    /// Splits delimited text per RFC 4180: quoted fields may contain the delimiter
    /// and newlines, and "" is a literal quote.
    /// </summary>
    /// <remarks>Internal rather than private so a test project can reach it directly — it is pure and the edge cases are where quiet regressions live.</remarks>
    internal static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"') { field.Append(c); continue; }
                if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else inQuotes = false;
            }
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* handled by the \n that follows */ }
            else if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        // Trailing row when the file doesn't end in a newline
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>
    /// Renders delimited text as a table. Parsed here rather than in the page: it
    /// used to pull a whole spreadsheet engine off a CDN just to split commas.
    /// </summary>
    internal static string BuildCsvHtml(string text, char delimiter)
    {
        var rows = ParseDelimited(text, delimiter);
        var truncated = rows.Count > CsvRowLimit;
        var shown = truncated ? rows.Take(CsvRowLimit).ToList() : rows;

        var table = new StringBuilder();
        if (shown.Count == 0)
        {
            table.Append("<div class=\"err\">This file has no rows.</div>");
        }
        else
        {
            table.Append("<table><thead><tr>");
            foreach (var cell in shown[0])
                table.Append("<th>").Append(System.Net.WebUtility.HtmlEncode(cell)).Append("</th>");
            table.Append("</tr></thead><tbody>");
            foreach (var r in shown.Skip(1))
            {
                table.Append("<tr>");
                foreach (var cell in r)
                    table.Append("<td>").Append(System.Net.WebUtility.HtmlEncode(cell)).Append("</td>");
                table.Append("</tr>");
            }
            table.Append("</tbody></table>");
        }

        var notice = truncated
            ? $"<div class=\"note\">Showing the first {CsvRowLimit:N0} of {rows.Count:N0} rows.</div>"
            : "";

        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
              body { margin: 0; background: #1E1E1E; color: #d4d4d4; font: 13px system-ui, sans-serif; }
              #sheet { padding: 12px; overflow: auto; }
              table { border-collapse: collapse; }
              /* pre, not nowrap: keeps the table compact but honours newlines
                 inside quoted fields instead of flattening them */
              td, th { border: 1px solid #3d3d3d; padding: 4px 10px; white-space: pre; text-align: left; }
              th { background: #2d2d2d; font-weight: 600; position: sticky; top: 0; }
              tbody tr:nth-child(even) { background: #242424; }
              .err, .note { padding: 12px; color: #9a9a9a; }
              @media print {
                body { background: #fff; color: #1a1a1a; }
                td, th { border-color: #b4b4b4; white-space: pre-wrap; }
                th { background: #ececec; color: #1a1a1a; }
                thead { display: table-header-group; }
                tr { break-inside: avoid; }
                tbody tr:nth-child(even) { background: #f6f6f6; }
              }
            </style></head><body>
            <div id="sheet">{{notice}}{{table}}</div>
            <script>chrome.webview.postMessage('render-done');</script>
            </body></html>
            """;
    }

    // ---------- Notebooks ----------

    // Jupyter tracebacks carry terminal colour codes, which would print as noise
    private static readonly System.Text.RegularExpressions.Regex AnsiEscape =
        new(@"\x1B\[[0-9;]*[a-zA-Z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private const int NotebookOutputCharLimit = 20_000;

    private static readonly string NotebookCss = """
  .nb-cell { margin: 0 0 18px; }
  .nb-in { display: block; color: #6a9955; font: 11px Consolas, monospace; margin-bottom: 3px; }
  .nb-out { border-left: 3px solid #3a3a3a; margin: 6px 0 0 0; padding-left: 12px; }
  .nb-out pre { background: #1b1b1b; border-color: #2a2a2a; margin: 4px 0; }
  .nb-err pre { background: #3a1d1d; border-color: #6b2b2b; color: #ffb3b3; }
  .nb-out img { max-width: 100%; background: #fff; border-radius: 4px; padding: 4px; }
  @media print {
    .nb-cell { break-inside: avoid; }
    .nb-in { color: #487a3a; }
    .nb-out { border-left-color: #c8c8c8; }
    .nb-out pre { background: #f7f7f7; border-color: #dcdcdc; }
    .nb-err pre { background: #fdf0f0; border-color: #e0b4b4; color: #7a1d1d; }
  }
""";

    /// <summary>
    /// Renders a Jupyter notebook: markdown cells through Markdig, code cells
    /// highlighted, and outputs (text, images, HTML, errors) below their cell.
    /// Read-only — nothing is executed.
    /// </summary>
    internal static string BuildNotebookHtml(string json)
    {
        // nbformat stores source and text as either a string or an array of lines
        static string Text(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.String => e.GetString() ?? "",
            JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(x => x.GetString() ?? "")),
            _ => ""
        };

        static string Pre(string text, string cssClass = "")
        {
            if (text.Length > NotebookOutputCharLimit)
                text = text[..NotebookOutputCharLimit] + "\n… output truncated …";
            var open = string.IsNullOrEmpty(cssClass) ? "<pre>" : $"<pre class=\"{cssClass}\">";
            return $"{open}<code>{System.Net.WebUtility.HtmlEncode(text)}</code></pre>";
        }

        var html = new StringBuilder();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex)
        {
            return BuildDocumentHtml(
                $"<p class='missing-image'>This file isn't valid notebook JSON: {System.Net.WebUtility.HtmlEncode(ex.Message)}</p>",
                NotebookCss);
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                return BuildDocumentHtml("<p class='missing-image'>No cells in this notebook.</p>", NotebookCss);

            // Notebooks are overwhelmingly Python; fall back to it when unspecified
            var language = "python";
            if (doc.RootElement.TryGetProperty("metadata", out var meta)
                && meta.TryGetProperty("language_info", out var li)
                && li.TryGetProperty("name", out var ln))
                language = ln.GetString() ?? "python";

            foreach (var cell in cells.EnumerateArray())
            {
                var type = cell.TryGetProperty("cell_type", out var ct) ? ct.GetString() : "code";
                var source = cell.TryGetProperty("source", out var src) ? Text(src) : "";

                html.Append("<div class=\"nb-cell\">");

                if (type == "markdown")
                {
                    html.Append(Markdown.ToHtml(source, MarkdownPipeline));
                }
                else if (type == "raw")
                {
                    html.Append(Pre(source));
                }
                else
                {
                    var n = cell.TryGetProperty("execution_count", out var ec) && ec.ValueKind == JsonValueKind.Number
                        ? ec.GetInt32().ToString()
                        : " ";
                    html.Append($"<span class=\"nb-in\">In [{n}]</span>");
                    html.Append($"<pre><code class=\"language-{System.Net.WebUtility.HtmlEncode(language)}\">")
                        .Append(System.Net.WebUtility.HtmlEncode(source))
                        .Append("</code></pre>");
                    AppendOutputs(cell, html, Text, Pre);
                }

                html.Append("</div>");
            }
        }

        return BuildDocumentHtml(html.ToString(), NotebookCss);
    }

    private static void AppendOutputs(
        JsonElement cell, StringBuilder html,
        Func<JsonElement, string> text, Func<string, string, string> pre)
    {
        if (!cell.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array) return;

        foreach (var o in outputs.EnumerateArray())
        {
            var kind = o.TryGetProperty("output_type", out var ot) ? ot.GetString() : "";

            if (kind == "stream")
            {
                html.Append("<div class=\"nb-out\">")
                    .Append(pre(o.TryGetProperty("text", out var t) ? text(t) : "", ""))
                    .Append("</div>");
            }
            else if (kind is "execute_result" or "display_data")
            {
                if (!o.TryGetProperty("data", out var data)) continue;
                html.Append("<div class=\"nb-out\">");

                // Richest representation available, preferring images
                if (data.TryGetProperty("image/png", out var png))
                {
                    var b64 = text(png).Replace("\n", "");
                    html.Append($"<img alt=\"notebook output\" src=\"data:image/png;base64,{b64}\">");
                }
                else if (data.TryGetProperty("image/jpeg", out var jpg))
                {
                    html.Append($"<img alt=\"notebook output\" src=\"data:image/jpeg;base64,{text(jpg).Replace("\n", "")}\">");
                }
                else if (data.TryGetProperty("text/html", out var h))
                {
                    // Trusted the same way an .html artifact is: local file, local render
                    html.Append(text(h));
                }
                else if (data.TryGetProperty("text/plain", out var p))
                {
                    html.Append(pre(text(p), ""));
                }
                html.Append("</div>");
            }
            else if (kind == "error")
            {
                var name = o.TryGetProperty("ename", out var en) ? en.GetString() : "Error";
                var value = o.TryGetProperty("evalue", out var ev) ? ev.GetString() : "";
                var trace = o.TryGetProperty("traceback", out var tb) ? text(tb) : $"{name}: {value}";
                html.Append("<div class=\"nb-out nb-err\">")
                    .Append(pre(AnsiEscape.Replace(trace, ""), ""))
                    .Append("</div>");
            }
        }
    }

    // ---------- Source files ----------

    private static readonly Dictionary<string, string> HljsLanguage = new()
    {
        [".py"] = "python", [".ps1"] = "powershell", [".yml"] = "yaml",
        [".cs"] = "csharp", [".rs"] = "rust", [".sh"] = "bash",
        [".bat"] = "dos", [".cmd"] = "dos", [".h"] = "cpp", [".rb"] = "ruby",
        [".jsx"] = "javascript", [".ts"] = "typescript", [".tsx"] = "typescript",
        [".toml"] = "ini", [".cfg"] = "ini", [".conf"] = "ini",
        // Nothing to tokenize, but the shell's dark page and wrapping still apply
        [".log"] = "plaintext", [".txt"] = "plaintext"
    };

    internal static string BuildCodeHtml(string ext, string text)
    {
        var lang = HljsLanguage.TryGetValue(ext, out var mapped) ? mapped : ext.TrimStart('.');
        // Highlighting very large files (big logs) hangs the page — plain text is fine there
        var highlight = text.Length < 500_000;
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <link rel="stylesheet" href="highlight-dark.min.css">
            <style>
              body { margin: 0; background: #1E1E1E; }
              pre { margin: 0; padding: 16px 20px; font: 13px/1.5 Consolas, 'Cascadia Mono', monospace;
                    color: #d4d4d4; white-space: pre-wrap; word-break: break-all; }
            </style></head><body>
            <pre><code class="language-{{lang}}">{{System.Net.WebUtility.HtmlEncode(text)}}</code></pre>
            {{(highlight ? """
            <script src="highlight.min.js"></script>
            <script>try { hljs.highlightAll(); } catch (e) { /* plain text is fine */ }</script>
            """ : "")}}
            <script>chrome.webview.postMessage('render-done');</script>
            </body></html>
            """;
    }

    // ---------- Office documents ----------

    internal static string BuildOfficeHtml(string ext, long ticks) => ext == ".docx"
        ? $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
              body { margin: 0; background: #1E1E1E; }
              #doc { padding: 24px 0; }
              #doc .docx-wrapper { background: transparent; padding: 0; }
              #doc .docx-wrapper > section.docx { margin: 0 auto 16px; box-shadow: 0 2px 12px rgba(0,0,0,.5); }
              .err { color: #d4d4d4; font: 14px system-ui; padding: 40px; text-align: center; }
            </style></head><body>
            <div id="doc"></div>
            <script src="https://cdnjs.cloudflare.com/ajax/libs/jszip/3.10.1/jszip.min.js"></script>
            <script src="https://cdn.jsdelivr.net/npm/docx-preview@0.3.5/dist/docx-preview.min.js"></script>
            <script>
              fetch('current.docx?v={{ticks}}').then(r => r.arrayBuffer())
                .then(buf => docx.renderAsync(buf, document.getElementById('doc')))
                .catch(e => document.getElementById('doc').innerHTML =
                  '<div class="err">Could not render document: ' + e + '<br>(docx rendering needs internet for CDN libraries)</div>')
                .finally(() => chrome.webview.postMessage('render-done'));
            </script></body></html>
            """
        : $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
              body { margin: 0; background: #1E1E1E; color: #d4d4d4; font: 13px system-ui, sans-serif; }
              #tabs { display: flex; gap: 2px; padding: 8px 8px 0; position: sticky; top: 0; background: #1E1E1E; }
              #tabs button { background: #2d2d2d; color: #d4d4d4; border: 0; padding: 6px 14px;
                             cursor: pointer; border-radius: 4px 4px 0 0; font: inherit; }
              #tabs button.active { background: #3d3d3d; color: #fff; }
              #sheet { padding: 12px; overflow: auto; }
              table { border-collapse: collapse; }
              td, th { border: 1px solid #3d3d3d; padding: 4px 10px; white-space: nowrap; }
              tr:first-child td { background: #2d2d2d; font-weight: 600; }
              .err { padding: 40px; text-align: center; }
            </style></head><body>
            <div id="tabs"></div><div id="sheet"></div>
            <script src="https://cdn.jsdelivr.net/npm/xlsx@0.18.5/dist/xlsx.full.min.js"></script>
            <script>
              fetch('current.xlsx?v={{ticks}}').then(r => r.arrayBuffer()).then(buf => {
                const wb = XLSX.read(buf);
                const tabs = document.getElementById('tabs');
                const show = name => {
                  document.getElementById('sheet').innerHTML = XLSX.utils.sheet_to_html(wb.Sheets[name]);
                  [...tabs.children].forEach(b => b.classList.toggle('active', b.textContent === name));
                };
                wb.SheetNames.forEach(name => {
                  const b = document.createElement('button');
                  b.textContent = name; b.onclick = () => show(name);
                  tabs.appendChild(b);
                });
                show(wb.SheetNames[0]);
              }).catch(e => document.getElementById('sheet').innerHTML =
                '<div class="err">Could not render spreadsheet: ' + e + '<br>(xlsx rendering needs internet for CDN libraries)</div>')
                .finally(() => chrome.webview.postMessage('render-done'));
            </script></body></html>
            """;

    // ---------- Vector images ----------

    /// <summary>
    /// Frames an SVG in a page of our own so it lands centred on the document
    /// background and scaled to the window, instead of pinned to the top-left corner
    /// of a white page at whatever size the file happens to declare. Loaded as an
    /// image (cross-origin from the artifacts host, which is fine for display), so
    /// script inside a dropped SVG stays inert.
    /// </summary>
    internal static string BuildSvgHtml(string escapedName, long ticks) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><style>
          :root { color-scheme: dark; }
          html, body { height: 100%; margin: 0; }
          body { background: #1e1e1e; display: flex; align-items: center; justify-content: center;
                 padding: 24px; box-sizing: border-box; }
          #art { width: 100%; height: 100%; object-fit: contain; }
          .err { color: #d4d4d4; font: 14px system-ui, sans-serif; text-align: center; }
        </style></head><body>
        <img id="art" alt="">
        <script>
          const art = document.getElementById('art');
          const done = () => chrome.webview.postMessage('render-done');
          art.addEventListener('load', done);
          art.addEventListener('error', () => {
            document.body.innerHTML = '<div class="err">Could not render this SVG.</div>';
            done();
          });
          art.src = 'https://{{VirtualHost}}/{{escapedName}}?v={{ticks}}';
        </script></body></html>
        """;

    // ---------- Shared document shell ----------

    /// <summary>
    /// Shared document styling for the markdown and notebook renderers, screen and
    /// print. Kept in one place so the two can't drift apart.
    /// </summary>
    private const string DocumentCss = """
  :root { color-scheme: dark; }
  body {
    background: #1e1e1e; color: #d4d4d4;
    font-family: "Segoe UI", -apple-system, sans-serif;
    font-size: 15px; line-height: 1.6;
    max-width: 860px; margin: 0 auto; padding: 28px 36px 60px;
  }
  h1, h2, h3, h4 { color: #ffffff; line-height: 1.3; margin-top: 1.6em; }
  h1 { border-bottom: 1px solid #3a3a3a; padding-bottom: .3em; }
  h2 { border-bottom: 1px solid #2e2e2e; padding-bottom: .25em; }
  a { color: #4fc1ff; text-decoration: none; }
  a:hover { text-decoration: underline; }
  code { background: #2d2d30; padding: 2px 5px; border-radius: 4px; font-size: .9em;
         font-family: Consolas, "Cascadia Code", monospace; }
  pre { background: #252526; border: 1px solid #333; border-radius: 8px; padding: 14px 16px; overflow-x: auto; }
  pre code { background: none; padding: 0; }
  blockquote { border-left: 3px solid #0e9cdb; margin-left: 0; padding-left: 16px; color: #9a9a9a; }
  table { border-collapse: collapse; margin: 1em 0; display: block; overflow-x: auto; }
  th, td { border: 1px solid #3a3a3a; padding: 6px 12px; }
  th { background: #2d2d30; }
  tr:nth-child(even) { background: #242424; }
  img { max-width: 100%; border-radius: 6px; }
  hr { border: none; border-top: 1px solid #3a3a3a; margin: 2em 0; }
  ::-webkit-scrollbar { width: 12px; height: 12px; }
  ::-webkit-scrollbar-thumb { background: #3a3a3a; border-radius: 6px; }
  ::-webkit-scrollbar-track { background: transparent; }

  /* PDF export (Export to PDF… on the tab's right-click menu) prints through
     this block: light page, and pagination rules the screen view doesn't need.
     Page margins come from CoreWebView2PrintSettings, not @page. */
  @media print {
    :root { color-scheme: light; }
    body { background: #fff; color: #1a1a1a; max-width: none; margin: 0; padding: 0;
           font-size: 10.5pt; line-height: 1.5; }
    h1, h2, h3, h4 { color: #12395c; break-after: avoid; }
    h1 { border-bottom-color: #c4c4c4; }
    h2 { border-bottom-color: #dcdcdc; }
    a { color: #14507d; }
    blockquote { color: #40464d; border-left-color: #0e6fa0; }
    code { background: #f1f1f1; color: #1a1a1a; }
    pre { background: #f7f7f7; border-color: #dcdcdc; break-inside: avoid; }
    figure, img { break-inside: avoid; }
    hr { border-top-color: #c4c4c4; }
    /* The screen rule is display:block for horizontal scrolling, which stops
       tables paginating and stops header rows repeating. */
    table { display: table; width: 100%; overflow: visible; }
    thead { display: table-header-group; }
    tr { break-inside: avoid; }
    th, td { border-color: #b4b4b4; }
    th { background: #ececec; color: #1a1a1a; }
    tr:nth-child(even) { background: #f6f6f6; }
    /* highlight.js is loaded with a dark theme; restate the tokens as light
       ones so code doesn't print pale-on-white. Self-contained: no second CDN
       stylesheet to race with the print job. */
    .hljs { background: #f7f7f7; color: #24292e; }
    .hljs-keyword, .hljs-selector-tag, .hljs-literal, .hljs-section { color: #b31d28; }
    .hljs-string, .hljs-attr, .hljs-addition, .hljs-meta-string { color: #032f62; }
    .hljs-comment, .hljs-quote { color: #6a737d; }
    .hljs-number, .hljs-built_in, .hljs-type, .hljs-selector-attr { color: #005cc5; }
    .hljs-title, .hljs-name, .hljs-attribute { color: #6f42c1; }
    /* Mermaid renders with theme:'dark'. Node boxes carry their own fills and
       survive on a white page, but loose label text is styled for a dark
       background and prints nearly invisible. */
    .mermaid .messageText, .mermaid .loopText, .mermaid .loopText tspan,
    .mermaid .labelText, .mermaid .labelText tspan,
    .mermaid .edgeLabel text, .mermaid .edgeLabel tspan,
    .mermaid .titleText, .mermaid .sectionTitle, .mermaid .taskText {
      fill: #1a1a1a !important; color: #1a1a1a !important;
    }
    .mermaid .edgeLabel rect, .mermaid .labelBkg, .mermaid .edgeLabel .labelBkg {
      fill: #ffffff !important; background-color: #ffffff !important; opacity: 1 !important;
    }
    .mermaid .messageLine0, .mermaid .messageLine1 { stroke: #555 !important; }
  }
""";

    internal static string BuildMarkdownHtml(string markdown) =>
        BuildDocumentHtml(Markdown.ToHtml(markdown, MarkdownPipeline));

    /// <summary>
    /// Wraps already-rendered document HTML in the shared page shell: styles,
    /// vendored highlight.js and mermaid, and the render-done signal.
    /// </summary>
    private static string BuildDocumentHtml(string body, string extraCss = "") => $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<base href="https://{{VirtualHost}}/">
<!-- Absolute render-host URLs: the base tag above points relative paths at the
     watch folder so markdown images resolve, which would otherwise misdirect these. -->
<link rel="stylesheet" href="https://{{RenderHost}}/highlight-dark.min.css">
<style>
{{DocumentCss}}
{{extraCss}}
</style>
</head>
<body>
{{body}}
<script src="https://{{RenderHost}}/highlight.min.js"></script>
<script src="https://{{RenderHost}}/mermaid.min.js"></script>
<script>
  // Everything inside one guarded block: whatever fails, 'render-done' must
  // still fire or a PDF export sits waiting on it.
  (async () => {
    try {
      document.querySelectorAll('pre code:not(.language-mermaid)').forEach(el => hljs.highlightElement(el));
    } catch (err) { /* unhighlighted code is still readable */ }

    try {
      // Markdig's diagram extension already emits <div class="mermaid">; plain
      // renderers emit <pre><code class="language-mermaid">. Normalize the latter.
      document.querySelectorAll('pre > code.language-mermaid').forEach(code => {
        const div = document.createElement('div');
        div.className = 'mermaid';
        div.textContent = code.textContent;
        code.parentElement.replaceWith(div);
      });
      if (document.querySelector('.mermaid')) {
        // The vendored bundle assigns globalThis.mermaid on load
        mermaid.initialize({ startOnLoad: false, theme: 'dark' });
        await mermaid.run();
      }
    } catch (err) {
      const banner = document.createElement('div');
      banner.style.cssText = 'background:#5a1d1d;color:#ffb3b3;padding:10px 16px;border-radius:8px;margin:12px 0;font-family:monospace;white-space:pre-wrap;';
      banner.textContent = 'mermaid failed: ' + (err && err.message ? err.message : err);
      document.body.prepend(banner);
    }

    // Tells the host that highlighting and diagrams are done, so a PDF export
    // doesn't print half-rendered content.
    chrome.webview.postMessage('render-done');
  })();
</script>
</body>
</html>
""";
}
