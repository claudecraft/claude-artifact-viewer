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

    // ---------- SQL scripts ----------

    /// <summary>
    /// A numbered step inside a batch — the <c>-- 1. validate inputs</c> convention.
    /// <paramref name="Text"/> is the code from this step up to the next one, so the
    /// renderer can give each step its own anchor without re-splitting.
    /// </summary>
    internal readonly record struct SqlStep(string Number, string Label, int StartLine, string Text);

    /// <summary>One top-level batch of a SQL script, with whatever object it defines.</summary>
    /// <param name="Kind">PROCEDURE, TABLE, VIEW… or empty when the batch defines nothing.</param>
    /// <param name="Name">Bare object name, schema and brackets stripped.</param>
    /// <param name="StartLine">1-based line in the original file, for cross-referencing an editor.</param>
    /// <param name="Text">The batch, or just its preamble when it has numbered steps.</param>
    internal readonly record struct SqlBatch(
        string Kind, string Name, int StartLine, string Text, List<SqlStep> Steps);

    /// <remarks>
    /// Matched only against lines that are entirely comment, so a numbered item in data
    /// or an expression like <c>@x = 1 . 2</c> can't masquerade as a step. Tolerates the
    /// leading <c>--</c>, a block comment's <c>*</c> gutter, and a closing <c>*/</c>.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex SqlStepComment =
        new(@"^\s*(?:-{2,}|\*+|/\*)?\s*(\d{1,2}[A-Za-z]?)\s*[\.\)]\s+(\S.{0,110}?)\s*(?:\*/)?\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SqlBatchSeparator =
        new(@"^\s*GO\s*;?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <remarks>
    /// Anchored at line start because a definition that opens a batch always is.
    /// Deliberately not matched mid-line: <c>IF NOT EXISTS (…) CREATE TABLE #tmp</c>
    /// inside a procedure body is not a new top-level object.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex SqlObjectDefinition =
        new(@"^\s*(?:CREATE|ALTER|DROP)(?:\s+OR\s+ALTER)?\s+" +
            @"(PROCEDURE|PROC|FUNCTION|VIEW|TABLE|TRIGGER|INDEX|TYPE|SCHEMA|SEQUENCE|SYNONYM)\s+" +
            @"(\[?[\w\.\[\]]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Blanks out comments and string literals, keeping every other character at its
    /// original column, so batch separators and object definitions can be matched
    /// without tripping over a commented-out <c>GO</c> or a <c>CREATE TABLE</c> that
    /// only exists inside a dynamic-SQL string.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test project can reach it: the state carried
    /// across lines (open block comment, open string) is where quiet breakage lives —
    /// one unbalanced quote desyncs everything below it.
    /// </remarks>
    internal static string[] SanitizeSqlLines(string sql)
    {
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        var result = new string[lines.Length];

        // Carried across lines: T-SQL block comments nest, and both block comments
        // and string literals can span any number of lines.
        var commentDepth = 0;
        var inString = false;
        var inBracket = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var sb = new StringBuilder(line.Length);
            var inLineComment = false;

            for (var j = 0; j < line.Length; j++)
            {
                var c = line[j];
                var next = j + 1 < line.Length ? line[j + 1] : '\0';

                if (inLineComment) { sb.Append(' '); continue; }

                if (commentDepth > 0)
                {
                    if (c == '*' && next == '/') { commentDepth--; sb.Append("  "); j++; }
                    else if (c == '/' && next == '*') { commentDepth++; sb.Append("  "); j++; }
                    else sb.Append(' ');
                    continue;
                }

                if (inString)
                {
                    // '' is a literal quote, not the end of the string
                    if (c == '\'' && next == '\'') { sb.Append("  "); j++; }
                    else if (c == '\'') { inString = false; sb.Append('\''); }
                    else sb.Append(' ');
                    continue;
                }

                // A bracketed identifier is opaque: quotes and -- inside it are just
                // characters, and missing that desyncs the rest of the file.
                if (inBracket)
                {
                    if (c == ']' && next == ']') { sb.Append("  "); j++; }
                    else if (c == ']') { inBracket = false; sb.Append(']'); }
                    else sb.Append(c);
                    continue;
                }

                if (c == '-' && next == '-') { inLineComment = true; sb.Append("  "); j++; }
                else if (c == '/' && next == '*') { commentDepth++; sb.Append("  "); j++; }
                else if (c == '\'') { inString = true; sb.Append('\''); }
                else if (c == '[') { inBracket = true; sb.Append('['); }
                else sb.Append(c);
            }

            result[i] = sb.ToString();
        }

        return result;
    }

    /// <summary>
    /// Splits a T-SQL script into its top-level batches and labels each with the object
    /// it defines. Batches split *after* each <c>GO</c>, so a block reads the way it was
    /// written: definition first, terminator last.
    /// </summary>
    /// <remarks>
    /// Returns a single unlabelled batch when the script has no <c>GO</c> and no top-level
    /// definitions — an ad-hoc query gets no banding rather than misleading banding.
    /// </remarks>
    internal static List<SqlBatch> SplitSqlBatches(string sql)
    {
        var lines = sql.Replace("\r\n", "\n").Split('\n');
        var clean = SanitizeSqlLines(sql);

        // GO is the real batch separator; a script that uses it is unambiguous. Only
        // when there is none do we fall back to splitting at definitions themselves,
        // which is the shape of a DDL script written as one batch.
        var hasGo = clean.Any(l => SqlBatchSeparator.IsMatch(l));

        var starts = new List<int> { 0 };
        for (var i = 0; i < clean.Length; i++)
        {
            if (hasGo)
            {
                // Split after the separator, not before it
                if (SqlBatchSeparator.IsMatch(clean[i]) && i + 1 < clean.Length) starts.Add(i + 1);
            }
            else if (i > 0 && SqlObjectDefinition.IsMatch(clean[i]))
            {
                starts.Add(i);
            }
        }

        var batches = new List<SqlBatch>();
        for (var b = 0; b < starts.Count; b++)
        {
            var from = starts[b];
            var to = b + 1 < starts.Count ? starts[b + 1] : lines.Length;

            // Skip the blank lines that follow a GO: they'd render as a gap under the
            // batch header, and skipping them makes StartLine land on the definition
            // itself rather than on the whitespace above it.
            while (from < to && string.IsNullOrWhiteSpace(lines[from])) from++;
            if (from >= to) continue;

            var text = string.Join("\n", lines[from..to]).TrimEnd();
            // A trailing GO leaves an empty tail batch; nothing to show for it
            if (text.Length == 0) continue;

            var kind = "";
            var name = "";
            for (var i = from; i < to; i++)
            {
                var m = SqlObjectDefinition.Match(clean[i]);
                if (!m.Success) continue;
                kind = m.Groups[1].Value.ToUpperInvariant();
                if (kind == "PROC") kind = "PROCEDURE";
                // dbo.[spX] / [dbo].[spX] / spX all reduce to spX
                name = m.Groups[2].Value.Split('.').Last().Trim('[', ']');
                break;
            }

            // Numbered comment steps within the batch. A line is comment-only when the
            // sanitizer blanked it but the raw line had content — no comment parsing
            // needed here beyond that.
            var stepLines = new List<(int Index, string Number, string Label)>();
            for (var i = from; i < to; i++)
            {
                if (!string.IsNullOrWhiteSpace(clean[i]) || string.IsNullOrWhiteSpace(lines[i])) continue;
                var m = SqlStepComment.Match(lines[i]);
                if (m.Success) stepLines.Add((i, m.Groups[1].Value, m.Groups[2].Value.Trim()));
            }

            var steps = new List<SqlStep>();
            // One lone "1." is a numbered sentence, not a step list
            if (stepLines.Count >= 2)
            {
                for (var s = 0; s < stepLines.Count; s++)
                {
                    var sFrom = stepLines[s].Index;
                    var sTo = s + 1 < stepLines.Count ? stepLines[s + 1].Index : to;
                    steps.Add(new SqlStep(
                        stepLines[s].Number, stepLines[s].Label, sFrom + 1,
                        string.Join("\n", lines[sFrom..sTo]).TrimEnd()));
                }
                // The batch keeps only what precedes its first step
                text = string.Join("\n", lines[from..stepLines[0].Index]).TrimEnd();
            }

            batches.Add(new SqlBatch(kind, name, from + 1, text, steps));
        }

        return batches;
    }

    /// <summary>
    /// Renders a SQL script as banded batches: alternating backgrounds so a new
    /// definition is visible while scanning, and a sticky header naming the object so
    /// it stays identified while scrolling through a long body.
    /// <para>
    /// SQL-only on purpose. <c>GO</c> makes T-SQL batch boundaries unambiguous;
    /// every other language in <c>CodeExtensions</c> would need its own segmentation
    /// and would guess.
    /// </para>
    /// </summary>
    /// <summary>
    /// The naming prefix every object in a script shares, cut back to an underscore —
    /// <c>spMISCREPORTS_</c> across fourteen procedures. Returns empty unless hiding it
    /// is clearly worthwhile: in a 250px index the prefix is the part that forces
    /// mid-identifier wrapping, and it's the part that carries no information.
    /// </summary>
    internal static string CommonNamePrefix(List<string> names)
    {
        if (names.Count < 3) return "";

        var shared = names[0].Length;
        foreach (var other in names.Skip(1))
        {
            var i = 0;
            var max = Math.Min(shared, other.Length);
            while (i < max && char.ToUpperInvariant(names[0][i]) == char.ToUpperInvariant(other[i])) i++;
            shared = i;
            if (shared == 0) return "";
        }

        // Only cut at a name-part boundary, or the remainder reads as a fragment
        var cut = names[0][..shared].LastIndexOf('_');
        if (cut < 4) return "";

        var prefix = names[0][..(cut + 1)];
        // Never leave an entry with nothing to show
        return names.All(n => n.Length > prefix.Length + 1) ? prefix : "";
    }

    internal static string BuildSqlHtml(string text)
    {
        var batches = SplitSqlBatches(text);

        // Nothing to delimit: render exactly as any other source file, so an ad-hoc
        // query doesn't get chrome it has no use for.
        if (batches.Count < 2) return BuildCodeHtml(".sql", text);

        var prefix = CommonNamePrefix(
            batches.Where(b => b.Name.Length > 0).Select(b => b.Name).ToList());
        var highlight = text.Length < 500_000;
        var body = new StringBuilder();
        var index = new StringBuilder();

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            var id = $"b{i + 1}";
            var name = System.Net.WebUtility.HtmlEncode(batch.Name);
            var kind = System.Net.WebUtility.HtmlEncode(batch.Kind);

            body.Append("<section class=\"batch\" id=\"").Append(id).Append("\">");
            if (batch.Name.Length > 0)
            {
                body.Append("<header><span class=\"kind\">").Append(kind)
                    .Append("</span><span class=\"name\">").Append(name)
                    .Append("</span><span class=\"line\">line ").Append(batch.StartLine)
                    .Append("</span></header>");

                // Full name in the body header, short name in the index: the kind is
                // dropped here too, since a script of fourteen procedures says
                // PROCEDURE fourteen times and never distinguishes anything.
                var shortName = prefix.Length > 0 && batch.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? System.Net.WebUtility.HtmlEncode(batch.Name[prefix.Length..])
                    : name;

                index.Append("<li><a href=\"#").Append(id).Append("\" data-for=\"").Append(id)
                     .Append("\" title=\"").Append(name).Append("\">")
                     .Append("<span class=\"name\">").Append(shortName).Append("</span>")
                     .Append("<span class=\"line\">").Append(batch.StartLine).Append("</span>")
                     .Append("</a></li>");
            }

            if (batch.Text.Length > 0)
            {
                body.Append("<pre><code class=\"language-sql\">")
                    .Append(System.Net.WebUtility.HtmlEncode(batch.Text))
                    .Append("</code></pre>");
            }

            // Each step is its own chunk so it can carry an anchor. Chunks are seamless:
            // one <pre> per step with no margin reads as a single listing.
            if (batch.Steps.Count > 0)
            {
                var steps = new StringBuilder();
                for (var s = 0; s < batch.Steps.Count; s++)
                {
                    var step = batch.Steps[s];
                    var stepId = $"{id}s{s + 1}";

                    body.Append("<div class=\"step\" id=\"").Append(stepId).Append("\">")
                        .Append("<pre><code class=\"language-sql\">")
                        .Append(System.Net.WebUtility.HtmlEncode(step.Text))
                        .Append("</code></pre></div>");

                    steps.Append("<li><a href=\"#").Append(stepId)
                         .Append("\" data-for=\"").Append(stepId).Append("\">")
                         .Append("<span class=\"num\">").Append(System.Net.WebUtility.HtmlEncode(step.Number))
                         .Append(".</span><span class=\"steplabel\">")
                         .Append(System.Net.WebUtility.HtmlEncode(step.Label))
                         .Append("</span></a></li>");
                }

                // Steps of an unnamed batch (a script header, typically) still deserve an
                // index entry, so the list opens rather than being attached to nothing.
                if (batch.Name.Length == 0)
                    index.Append("<li class=\"anon\"><a href=\"#").Append(id)
                         .Append("\" data-for=\"").Append(id).Append("\">")
                         .Append("<span class=\"name\">(header)</span>")
                         .Append("<span class=\"line\">").Append(batch.StartLine)
                         .Append("</span></a></li>");

                index.Append("<li class=\"steps\"><ol>").Append(steps).Append("</ol></li>");
            }

            body.Append("</section>");
        }

        // One entry is a list of one; the index earns its width from about two up —
        // and a single object with a step list is worth indexing on the steps alone.
        var named = batches.Count(b => b.Name.Length > 0);
        var stepCount = batches.Sum(b => b.Steps.Count);
        var label = (named, stepCount) switch
        {
            ( > 0, > 0) => $"{named} objects · {stepCount} steps",
            ( > 0, _) => $"{named} objects",
            _ => $"{stepCount} steps"
        };
        // Say what was hidden, so a stripped name is never a mystery
        if (prefix.Length > 0)
            label += $"<span class=\"prefix\">{System.Net.WebUtility.HtmlEncode(prefix)}…</span>";
        var nav = named >= 2 || stepCount >= 2
            ? $"<nav id=\"idx\"><div class=\"idxhdr\">{label}</div><ol>{index}</ol></nav>"
            : "";

        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <link rel="stylesheet" href="highlight-dark.min.css">
            <style>
              html { scroll-behavior: smooth; }
              body { margin: 0; background: #1E1E1E; display: flex; align-items: flex-start; }
              main { flex: 1 1 auto; min-width: 0; }   /* min-width:0 or <pre> won't wrap in a flex row */

              /* Jump index. Sticky rather than fixed so it scrolls its own overflow
                 independently while staying put beside the script. */
              #idx {
                position: sticky; top: 0; flex: 0 0 250px;
                max-height: 100vh; overflow-y: auto;
                background: #252526; border-right: 1px solid #333;
                font: 12px system-ui, sans-serif;
              }
              .idxhdr { padding: 7px 14px; color: #7A7A7A; font-size: 10px; letter-spacing: .08em;
                        text-transform: uppercase; border-bottom: 1px solid #333;
                        position: sticky; top: 0; background: #252526; z-index: 2;
                        display: flex; justify-content: space-between; gap: 8px; }
              .prefix { color: #5E6B73; text-transform: none; letter-spacing: 0;
                        font-family: Consolas, 'Cascadia Mono', monospace; }
              #idx ol { list-style: none; margin: 0; padding: 4px 0; }
              #idx a { display: grid; grid-template-columns: 1fr auto; align-items: baseline;
                       gap: 0 8px; padding: 5px 14px; text-decoration: none;
                       border-left: 2px solid transparent; }
              #idx .name { grid-column: 1; }
              #idx .line { grid-column: 2; justify-self: end; }
              #idx a:hover { background: #313135; }
              /* Light enough to find at a glance while scrolling — the whole point of
                 the index is answering "where am I" without stopping to read */
              #idx a.on { background: #3A4048; border-left-color: #35B7F5; }
              /* The enclosing object of the current step stays marked, so the index
                 answers "where am I" and not only "where can I go" */
              #idx a.trail { border-left-color: #14506B; }
              #idx .name { color: #D4D4D4; font-family: Consolas, 'Cascadia Mono', monospace;
                           font-size: 11.5px; overflow-wrap: anywhere; }
              #idx a.on .name { color: #FFFFFF; }
              #idx .line { color: #6A6A6A; font-size: 10.5px; }
              #idx .anon .name { color: #8A8A8A; font-style: italic; }

              /* Numbered steps, nested under their object */
              #idx li.steps > ol { padding: 0 0 4px; }
              #idx li.steps a { grid-template-columns: auto 1fr; padding: 3px 14px 3px 22px; gap: 0 6px; }
              #idx .num { color: #35B7F5; font-size: 10.5px; font-variant-numeric: tabular-nums; }
              #idx .steplabel { color: #9A9A9A; font-size: 11px;
                                white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
              #idx li.steps a.on .steplabel { color: #FFFFFF; }

              /* A 250px index inside a narrow window costs more than it gives */
              @media (max-width: 700px) { #idx { display: none; } }

              /* Alternating bands are the point: a new definition is a change of
                 background, visible while scrolling fast without reading anything. */
              .batch { background: #1E1E1E; border-top: 1px solid #303030; }
              /* A step's anchor has to clear the batch header that stays pinned above it */
              .step { scroll-margin-top: 34px; }
              .step + .step, pre + .step { border-top: 1px dashed #34343A; }

              /* Jumped-to target stays lit until you jump somewhere else, so it answers
                 "did I land where I meant to" as well as marking the spot. Translucent
                 so it tints whichever band it lands on; inset shadow rather than a
                 border so nothing shifts. */
              section.batch:target, .step:target {
                background: rgba(255, 213, 79, .13);
                box-shadow: inset 3px 0 0 #E3B341;
              }
              section.batch:target > header {
                background: #443A20; border-bottom-color: #5E5029;
              }
              section.batch:target > header .kind,
              section.batch:target > header .line { color: #B49A62; }
              .batch:nth-child(even) { background: #232327; }
              .batch:first-child { border-top: 0; }
              /* Sticky per batch, so a 400-line procedure stays identified all the way
                 down and the next one's header pushes the last one out. */
              .batch > header {
                position: sticky; top: 0; z-index: 1;
                display: flex; align-items: baseline; gap: 10px;
                padding: 5px 20px;
                background: #2D2D30; border-bottom: 1px solid #3A3A3A;
                font: 12px system-ui, sans-serif;
              }
              .batch .kind { color: #7A7A7A; font-size: 10px; letter-spacing: .08em; }
              .batch .name { color: #FFFFFF; font-weight: 600; font-family: Consolas, 'Cascadia Mono', monospace; }
              .batch .line { color: #6A6A6A; margin-left: auto; font-size: 11px; }
              pre { margin: 0; padding: 12px 20px; font: 13px/1.5 Consolas, 'Cascadia Mono', monospace;
                    color: #d4d4d4; white-space: pre-wrap; word-break: break-all; background: none; }
              .hljs { background: none; }

              @media print {
                body { background: #fff; display: block; }
                #idx { display: none; }
                .batch { background: #fff; border-top-color: #C8C8C8; }
                .batch:nth-child(even) { background: #F6F6F8; }
                /* Wherever you happened to be sitting isn't part of the document */
                section.batch:target, .step:target { background: none; box-shadow: none; }
                section.batch:target > header { background: #ECECEC; border-bottom-color: #C8C8C8; }
                .batch > header {
                  position: static; background: #ECECEC; border-bottom-color: #C8C8C8;
                  break-after: avoid;
                }
                .batch .name { color: #1A1A1A; }
                .batch .kind, .batch .line { color: #55595E; }
                pre { color: #24292E; }
              }
            </style></head><body>
            {{nav}}
            <main>{{body}}</main>
            {{(highlight ? """
            <script src="highlight.min.js"></script>
            <script>try { document.querySelectorAll('pre code').forEach(el => hljs.highlightElement(el)); } catch (e) { /* plain text is still readable */ }</script>
            """ : "")}}
            <script>
              // Track what's under the top edge so the index shows where you are, not
              // only where you can go. Guarded: without it, jumping still works.
              //
              // Last-target-past-the-line rather than IntersectionObserver: batches and
              // their steps are nested, so a tall batch overlaps the top band the whole
              // way down and "is intersecting" can't distinguish it from the step being
              // read. Document order gives monotonically increasing tops, so the last
              // one past the line is unambiguous.
              try {
                const idx = document.getElementById('idx');
                const links = new Map(
                  [...document.querySelectorAll('#idx a')].map(a => [a.dataset.for, a]));
                const targets = [...document.querySelectorAll('section.batch, .step')];

                if (idx && links.size && targets.length) {
                  let active = null, trail = null, queued = false;

                  const update = () => {
                    queued = false;
                    let cur = targets[0];
                    for (const t of targets) {
                      if (t.getBoundingClientRect().top <= 64) cur = t; else break;
                    }

                    const a = links.get(cur.id);
                    if (!a || a === active) return;
                    active?.classList.remove('on');
                    trail?.classList.remove('trail');
                    a.classList.add('on');
                    active = a;

                    const section = cur.closest('section.batch');
                    trail = section && section !== cur ? links.get(section.id) : null;
                    trail?.classList.add('trail');

                    // Keep the active entry visible in a long index. Setting scrollTop
                    // directly, not scrollIntoView: that scrolls ancestors too, and with
                    // smooth scrolling it fights the page scroll that triggered this.
                    const top = a.offsetTop, bottom = top + a.offsetHeight;
                    if (top < idx.scrollTop + 24 || bottom > idx.scrollTop + idx.clientHeight - 8) {
                      idx.scrollTop = Math.max(0, top - idx.clientHeight / 2);
                    }
                  };

                  addEventListener('scroll',
                    () => { if (!queued) { queued = true; requestAnimationFrame(update); } },
                    { passive: true });
                  update();
                }
              } catch (e) { /* no position tracking; the index still jumps */ }

              chrome.webview.postMessage('render-done');
            </script>
            </body></html>
            """;
    }

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
