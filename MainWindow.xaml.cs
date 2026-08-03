using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Core;

namespace ArtifactViewer;

public record FileEntry(string Path, string Name, DateTime LastWrite);

public record SidebarItem(FileEntry Entry, bool IsClosed);

public partial class MainWindow : Window
{
    private const string VirtualHost = "artifacts.viewer";
    private const string RenderHost = "render.viewer";

    private static readonly string[] CodeExtensions =
    {
        ".py", ".js", ".ts", ".jsx", ".tsx", ".cs", ".sql", ".yaml", ".yml",
        ".toml", ".xml", ".log", ".ps1", ".sh", ".bat", ".cmd", ".c", ".cpp",
        ".h", ".java", ".rb", ".go", ".rs", ".php", ".css", ".ini", ".cfg", ".conf"
    };

    private static readonly string[] SupportedExtensions = new[]
    {
        ".md", ".markdown", ".ipynb", ".html", ".htm", ".pdf", ".svg",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif",
        ".txt", ".json", ".docx", ".xlsx", ".csv", ".tsv",
        ".mp4", ".webm", ".mp3", ".wav"
    }.Concat(CodeExtensions).ToArray();

    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private string _watchDir;
    private bool _watchDirFromArgs; // per-launch override; suppresses first-run seeding
    private string? _initialFile;   // file passed on the command line, selected after the first scan
    private readonly string _renderDir;
    private readonly string _settingsPath;
    private readonly string _appDataDir;
    private FileSystemWatcher? _cmdWatcher;
    private bool _processingCommand;
    private readonly ObservableCollection<FileEntry> _files = new();
    private readonly ObservableCollection<FileEntry> _pinnedFiles = new();
    // Ordered so the pinned row keeps the order you pinned things in
    private readonly List<string> _pinnedPaths = new();
    private string _pinnedStatePath = "";
    private readonly ObservableCollection<SidebarItem> _allFiles = new();
    private List<FileEntry> _scanned = new();
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private readonly Dictionary<string, DateTime> _closed = new(StringComparer.OrdinalIgnoreCase);
    private string _closedStatePath = "";

    private string ResolveWatchDir(string[] args)
    {
        // Command-line arg is a per-launch override and is not persisted
        if (args.Length > 1)
        {
            // Malformed paths (invalid characters, over-long) throw here rather
            // than taking the app down before a window ever exists
            try
            {
                var arg = System.IO.Path.GetFullPath(args[1]);

                // A file argument — "ArtifactViewer.exe report.md" — means "show me
                // this": watch the folder it lives in and select it once scanned.
                // Treating it as a folder used to throw on CreateDirectory.
                if (File.Exists(arg))
                {
                    _initialFile = arg;
                    var parent = System.IO.Path.GetDirectoryName(arg);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        _watchDirFromArgs = true;
                        return parent;
                    }
                }
                else
                {
                    // Folder, existing or to be created. Created here rather than
                    // left to the caller so an unusable path (.NET's GetFullPath
                    // accepts '?' and '|', which CreateDirectory then rejects)
                    // fails inside this try instead of taking the app down.
                    Directory.CreateDirectory(arg);
                    _watchDirFromArgs = true;
                    return arg;
                }
            }
            catch (Exception) { /* unusable argument — fall back to the configured folder */ }
        }

        var configured = LoadSetting("watchDir");
        if (configured is not null)
        {
            if (Directory.Exists(configured)) return configured;

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"Watched folder not found ({configured}) — choose a folder to watch"
            };
            if (dlg.ShowDialog() == true)
            {
                SaveSetting("watchDir", dlg.FolderName);
                return dlg.FolderName;
            }
        }

        var fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "claude_artifacts");
        SaveSetting("watchDir", fallback);
        return fallback;
    }

    // Bump when a vendored library is replaced — mismatched stamp re-extracts
    private const string VendoredLibsStamp = "mermaid-11.16.0 hljs-11.9.0";

    private static readonly string[] VendoredLibs =
        { "mermaid.min.js", "highlight.min.js", "highlight-dark.min.css" };

    /// <summary>
    /// Unpacks the vendored render libraries into the render folder, where the
    /// generated pages load them from as same-origin files. Replaces what used to
    /// be CDN script tags: works offline, and a PDF export can't race a download.
    /// </summary>
    private void ExtractVendoredLibs()
    {
        var stampPath = System.IO.Path.Combine(_renderDir, "libs.stamp");
        try
        {
            if (File.Exists(stampPath) &&
                File.ReadAllText(stampPath) == VendoredLibsStamp) return;

            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            foreach (var name in VendoredLibs)
            {
                using var resource = asm.GetManifestResourceStream(name);
                if (resource is null) continue;
                using var file = new FileStream(
                    System.IO.Path.Combine(_renderDir, name), FileMode.Create, FileAccess.Write);
                resource.CopyTo(file);
            }
            File.WriteAllText(stampPath, VendoredLibsStamp);
        }
        catch (Exception)
        {
            // Degrades to unhighlighted code and a diagram error banner rather
            // than taking startup down
        }
    }

    /// <summary>
    /// Drops a welcome document into the watch folder the first time the app runs,
    /// so a new user has something rendered to look at instead of an empty window.
    /// Strictly once: deleting it must not bring it back.
    /// </summary>
    private void SeedWelcomeArtifact()
    {
        if (_watchDirFromArgs) return;          // don't write into a folder the user just pointed at
        if (LoadSetting("seeded") == "true") return;

        // Flagged before writing: if this fails, it shouldn't retry on every launch
        SaveSetting("seeded", "true");

        try
        {
            var dst = System.IO.Path.Combine(_watchDir, "welcome.md");
            if (File.Exists(dst)) return;      // never clobber a file already there

            using var resource = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("welcome.md");
            if (resource is null) return;

            using var file = new FileStream(dst, FileMode.CreateNew, FileAccess.Write);
            resource.CopyTo(file);
        }
        catch (Exception) { /* a first-run nicety is never worth failing startup over */ }
    }

    private string? LoadSetting(string key)
    {
        try
        {
            if (!File.Exists(_settingsPath)) return null;
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath));
            return settings is not null && settings.TryGetValue(key, out var value) ? value : null;
        }
        catch (Exception) { return null; }
    }

    private void SaveSetting(string key, string value)
    {
        try
        {
            Dictionary<string, string>? settings = null;
            if (File.Exists(_settingsPath))
                settings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_settingsPath));
            settings ??= new Dictionary<string, string>();
            settings[key] = value;
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* non-fatal: setting just won't persist */ }
    }

    private void LoadClosed()
    {
        try
        {
            if (!File.Exists(_closedStatePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_closedStatePath));
            if (loaded is null) return;
            foreach (var (path, closedAt) in loaded) _closed[path] = closedAt;
        }
        catch (Exception) { /* corrupt/unreadable state file — start with nothing closed */ }
    }

    private void SaveClosed()
    {
        try
        {
            File.WriteAllText(_closedStatePath, JsonSerializer.Serialize(_closed));
        }
        catch (Exception) { /* non-fatal: worst case closes don't persist this session */ }
    }

    private static bool PathEq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsPinned(string path) => _pinnedPaths.Any(p => PathEq(p, path));

    private void LoadPinned()
    {
        try
        {
            if (!File.Exists(_pinnedStatePath)) return;
            var loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_pinnedStatePath));
            if (loaded is not null) _pinnedPaths.AddRange(loaded);
        }
        catch (Exception) { /* corrupt state — start with nothing pinned */ }
    }

    private void SavePinned()
    {
        try
        {
            File.WriteAllText(_pinnedStatePath, JsonSerializer.Serialize(_pinnedPaths));
        }
        catch (Exception) { /* non-fatal: pins just don't survive the session */ }
    }
    private bool _webReady;
    private string? _renderedPath;
    private DateTime _renderedWrite;

    // Rendering is asynchronous inside the page (highlight.js, mermaid, the CDN
    // office/csv renderers), so a PDF export has to wait for it to settle.
    // Pages we generate post 'render-done'; anything Chromium renders natively
    // (images, PDFs, user-authored HTML) only gives us NavigationCompleted.
    private TaskCompletionSource<bool>? _renderSignal;
    private TaskCompletionSource<bool>? _navSignal;
    private bool _expectRenderSignal;

    // Printing these to PDF is either meaningless or a lossy round-trip
    private static readonly string[] NonPrintableExtensions =
        { ".pdf", ".mp4", ".webm", ".mp3", ".wav" };

    public MainWindow()
    {
        InitializeComponent();

        var appDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer");
        Directory.CreateDirectory(appDataDir);
        _appDataDir = appDataDir;
        _settingsPath = System.IO.Path.Combine(appDataDir, "settings.json");

        _watchDir = ResolveWatchDir(Environment.GetCommandLineArgs());
        Directory.CreateDirectory(_watchDir);

        _renderDir = System.IO.Path.Combine(appDataDir, "render");
        Directory.CreateDirectory(_renderDir);

        _closedStatePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer", "closed-tabs.json");
        LoadClosed();

        _pinnedStatePath = System.IO.Path.Combine(appDataDir, "pinned-tabs.json");
        LoadPinned();

        ExtractVendoredLibs();

        // Before the first Rescan, so it's simply there as the newest artifact
        SeedWelcomeArtifact();

        TabStrip.ItemsSource = _files;
        PinnedStrip.ItemsSource = _pinnedFiles;
        SideList.ItemsSource = _allFiles;
        System.Windows.Interop.ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer");
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // The one failure a fresh install plausibly hits: WebView2 ships with
            // Windows 11 and most 10s, but not all. Without this the window just
            // comes up blank and unexplained.
            ShowStartupFailure(ex);
            return;
        }

        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, _watchDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            RenderHost, _renderDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;

        // WebView2 takes keyboard focus after every navigation, and Chromium then
        // eats the shortcuts: Alt+Arrow becomes browser back/forward, Ctrl+± becomes
        // browser zoom. Without this the app's own shortcuts work exactly once —
        // until the first artifact renders.
        Web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

        // Drops over the web content can't reach the WPF handlers (separate HWND),
        // so catch them in the page and post the file paths back to the host
        await Web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
            window.addEventListener('dragover', e => {
                e.preventDefault();
                e.dataTransfer.dropEffect = 'copy';
            }, true);
            window.addEventListener('drop', e => {
                e.preventDefault();
                if (e.dataTransfer?.files?.length)
                    chrome.webview.postMessageWithAdditionalObjects('files-dropped', e.dataTransfer.files);
            }, true);
            """);
        Web.CoreWebView2.WebMessageReceived += Web_WebMessageReceived;
        Web.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            _navSignal?.TrySetResult(true);
            ApplyZoom();
        };

        _webReady = true;

        if (double.TryParse(LoadSetting("zoom"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var savedZoom))
            _zoom = Math.Clamp(savedZoom, ZoomMin, ZoomMax);
        ApplyZoom();

        StartWatcher();
        StartCommandWatcher();
        Rescan(selectLatest: true);

        if (_initialFile is not null)
        {
            // CmdShow already handles reopening a closed tab and selecting it
            try { CmdShow(_initialFile); }
            catch (Exception) { /* unsupported type, or deleted between launch and scan */ }
            _initialFile = null;
        }

        // Deliberately last and un-awaited: nothing about startup waits on the network
        _ = CheckForUpdateAsync();
    }

    private const string WebView2DownloadUrl =
        "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// Explains a WebView2 initialization failure in the window itself, and offers
    /// the download page when the runtime is simply missing.
    /// </summary>
    private void ShowStartupFailure(Exception ex)
    {
        var missingRuntime = ex is WebView2RuntimeNotFoundException;

        Web.Visibility = Visibility.Collapsed;
        TxtEmpty.Visibility = Visibility.Visible;
        TxtEmpty.Text = missingRuntime
            ? "The Microsoft Edge WebView2 runtime isn't installed.\n\n" +
              "Artifact Viewer uses it to render artifacts.\n" +
              $"Install the Evergreen runtime from\n{WebView2DownloadUrl}\nthen restart this app."
            : $"WebView2 failed to start.\n\n{ex.Message}";
        TxtTitle.Text = missingRuntime ? "WebView2 runtime required" : "WebView2 failed to start";

        if (!missingRuntime) return;

        var answer = MessageBox.Show(this,
            "Artifact Viewer needs the Microsoft Edge WebView2 runtime, which isn't " +
            "installed on this PC.\n\nOpen the download page now?",
            "WebView2 runtime required", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Process.Start(new ProcessStartInfo(WebView2DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception) { /* no default browser — the URL is on screen anyway */ }
    }

    // ---------- Update check ----------
    // The only network request this app makes on its own behalf. It sends nothing
    // but a User-Agent, runs at most once a day, and is switched off entirely by
    // setting "checkForUpdates": "false" in settings.json.

    private const string ReleasesApiUrl =
        "https://api.github.com/repos/claudecraft/claude-artifact-viewer/releases/latest";
    private const string ReleasesPageUrl =
        "https://github.com/claudecraft/claude-artifact-viewer/releases/latest";

    private async Task CheckForUpdateAsync()
    {
        if (string.Equals(LoadSetting("checkForUpdates"), "false", StringComparison.OrdinalIgnoreCase)) return;

        if (DateTime.TryParse(LoadSetting("lastUpdateCheck"), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            && (DateTime.UtcNow - last).TotalHours < 24) return;

        // Stamped before the request: a failing network shouldn't retry every launch
        SaveSetting("lastUpdateCheck", DateTime.UtcNow.ToString("o"));

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ArtifactViewer");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesApiUrl));

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;

            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null || latest <= current) return;

            TxtUpdate.Text = $"Version {tag} is available — you have v{current.ToString(3)}.";
            BtnUpdateReveal.ToolTip = Environment.ProcessPath is { Length: > 0 } exe
                ? $"Show the running program in Explorer, so you know which file to replace:\n{exe}"
                : "Show the running program in Explorer";
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub down: staying quiet is the right answer
        }
    }

    private void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
        }
        catch (Exception) { /* no default browser */ }
    }

    /// <summary>
    /// Opens Explorer with the running executable selected. Updating means replacing
    /// that file, and someone who downloaded the exe months ago has no idea where it
    /// went — a second copy in Downloads is the usual outcome otherwise.
    /// </summary>
    private void BtnUpdateReveal_Click(object sender, RoutedEventArgs e)
    {
        var exe = Environment.ProcessPath;
        try
        {
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{exe}\"") { UseShellExecute = true });
        }
        catch (Exception) { /* Explorer unavailable — the tooltip still shows the path */ }
    }

    private void BtnUpdateDismiss_Click(object sender, RoutedEventArgs e) =>
        UpdateBanner.Visibility = Visibility.Collapsed;

    // ---------- Watching ----------

    private void StartWatcher()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan(selectLatest: false); };

        _watcher = new FileSystemWatcher(_watchDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler onChange = (_, _) => Dispatcher.Invoke(BumpDebounce);
        _watcher.Created += onChange;
        _watcher.Changed += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => Dispatcher.Invoke(BumpDebounce);
    }

    private void BumpDebounce()
    {
        _debounce!.Stop();
        _debounce.Start();
    }

    private void Rescan(bool selectLatest)
    {
        var scanned = Directory.EnumerateFiles(_watchDir)
            .Where(f => SupportedExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new FileInfo(f))
            .OrderBy(fi => fi.LastWriteTime)
            .Select(fi => new FileEntry(fi.FullName, fi.Name, fi.LastWriteTime))
            .ToList();

        // A closed file that has since been rewritten counts as a new artifact — reopen it
        var closedChanged = false;
        foreach (var f in scanned)
            if (_closed.TryGetValue(f.Path, out var closedAt) && f.LastWrite > closedAt)
                closedChanged |= _closed.Remove(f.Path);

        // Drop state for files that are gone — but only within the folder being
        // watched. Closed and pinned state is global while a scan only sees one
        // folder, so pruning on absence alone throws away state for every other
        // folder's artifacts the moment the app is pointed somewhere else.
        var onDisk = new HashSet<string>(scanned.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        bool GoneFromWatchedFolder(string path) =>
            !onDisk.Contains(path)
            && PathEq(System.IO.Path.GetDirectoryName(path) ?? "", _watchDir.TrimEnd('\\'));

        foreach (var gone in _closed.Keys.Where(GoneFromWatchedFolder).ToList())
            closedChanged |= _closed.Remove(gone);
        if (closedChanged) SaveClosed();

        if (_pinnedPaths.RemoveAll(GoneFromWatchedFolder) > 0) SavePinned();

        _scanned = scanned;
        var open = scanned.Where(f => !_closed.ContainsKey(f.Path)).ToList();

        // Pinned entries keep pin order; the rest stay chronological
        var pinned = _pinnedPaths
            .Select(p => open.FirstOrDefault(f => PathEq(f.Path, p)))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();
        var unpinned = open.Where(f => !IsPinned(f.Path)).ToList();

        var selectedPath = CurrentEntry?.Path;
        // A pinned tab shouldn't be yanked away by a newly written artifact
        var onPinned = selectedPath is not null && IsPinned(selectedPath);
        var wasAtLatest = selectLatest
            || (!onPinned && (_files.Count == 0 || selectedPath == _files[^1].Path));

        _syncingSelection = true;
        _files.Clear();
        foreach (var f in unpinned) _files.Add(f);
        _pinnedFiles.Clear();
        foreach (var f in pinned) _pinnedFiles.Add(f);
        _syncingSelection = false;
        PinnedStrip.Visibility = _pinnedFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RebuildSidebar();

        // Counts both rows: everything open could be pinned, leaving _files empty
        var order = NavOrder;
        TxtEmpty.Visibility = order.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = order.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (order.Count == 0)
        {
            TxtTitle.Text = "Waiting for artifacts…";
            TxtDate.Text = "";
            TxtCounter.Text = "";
            _renderedPath = null;
            UpdateNavButtons();
            return;
        }

        // "Latest" means the newest unpinned artifact, falling back to the pinned row
        var newest = _files.Count > 0 ? _files[^1] : order[^1];
        var target = wasAtLatest
            ? newest
            : order.FirstOrDefault(f => PathEq(f.Path, selectedPath ?? "")) ?? newest;

        // Selecting triggers the strip's SelectionChanged → ShowEntry. If the selection
        // is unchanged but the file was rewritten, force a re-render.
        if (!PathEq(CurrentEntry?.Path ?? "", target.Path))
            SelectEntry(target.Path);
        else if (target.LastWrite != _renderedWrite)
            _ = ShowEntry(target);

        UpdateNavButtons();
    }

    // ---------- Current-doc state (lets Claude Code "see" what's on screen) ----------

    private void WriteCurrentDocState(string path)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(_appDataDir, "current.txt"), path);
        }
        catch (Exception) { /* non-fatal: "look at current doc" just won't resolve */ }
    }

    private void WriteTabsState()
    {
        try
        {
            var tabs = _scanned.Select(f => new
            {
                name = f.Name,
                path = f.Path,
                lastWrite = f.LastWrite,
                open = !_closed.ContainsKey(f.Path),
                pinned = IsPinned(f.Path),
                current = string.Equals(f.Path, _renderedPath, StringComparison.OrdinalIgnoreCase)
            });
            File.WriteAllText(System.IO.Path.Combine(_appDataDir, "tabs.json"),
                JsonSerializer.Serialize(tabs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* non-fatal: tab listing just goes stale */ }
    }

    // ---------- Control channel (Claude Code drives the viewer via command.txt) ----------
    // Write "capture [png-path]" | "show <file>" | "scroll-to <heading-or-#id>" |
    // "pdf [pdf-path]" to %LOCALAPPDATA%\ArtifactViewer\command.txt; the outcome
    // lands in command-result.txt.

    private void StartCommandWatcher()
    {
        var cmdFile = System.IO.Path.Combine(_appDataDir, "command.txt");
        try { File.Delete(cmdFile); } catch (Exception) { /* stale command from a previous run */ }

        _cmdWatcher = new FileSystemWatcher(_appDataDir, "command.txt")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        FileSystemEventHandler onCmd = (_, _) => Dispatcher.InvokeAsync(() => _ = ProcessCommandFile());
        _cmdWatcher.Created += onCmd;
        _cmdWatcher.Changed += onCmd;
        _cmdWatcher.Renamed += (_, _) => Dispatcher.InvokeAsync(() => _ = ProcessCommandFile());
    }

    private async Task ProcessCommandFile()
    {
        if (_processingCommand) return;
        var cmdFile = System.IO.Path.Combine(_appDataDir, "command.txt");
        if (!File.Exists(cmdFile)) return;
        _processingCommand = true;
        try
        {
            var text = (await ReadTextWithRetry(cmdFile))?.Trim();
            try { File.Delete(cmdFile); } catch (Exception) { /* re-processing is harmless */ }
            if (string.IsNullOrEmpty(text)) return;

            var space = text.IndexOfAny(new[] { ' ', '\t' });
            var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
            var arg = space < 0 ? "" : text[(space + 1)..].Trim().Trim('"');

            string status = "ok", detail;
            try
            {
                detail = verb switch
                {
                    "capture" => await CmdCapture(arg),
                    "show" => CmdShow(arg),
                    "scroll-to" => await CmdScrollTo(arg),
                    "pdf" => await CmdPdf(arg),
                    "focus" => CmdFocus(arg),
                    _ => throw new InvalidOperationException(
                        $"unknown command '{verb}' (capture | show | scroll-to | pdf | focus)")
                };
            }
            catch (Exception ex)
            {
                status = "error";
                detail = ex.Message;
            }

            try
            {
                File.WriteAllText(System.IO.Path.Combine(_appDataDir, "command-result.txt"),
                    JsonSerializer.Serialize(new { command = text, status, detail, at = DateTime.Now },
                        new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception) { /* caller just won't see the ack */ }
        }
        finally { _processingCommand = false; }
    }

    private async Task<string> CmdCapture(string arg)
    {
        if (!_webReady || _renderedPath is null) throw new InvalidOperationException("nothing rendered yet");
        var output = string.IsNullOrEmpty(arg)
            ? System.IO.Path.Combine(_appDataDir, "capture.png")
            : System.IO.Path.GetFullPath(arg);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);
        using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
            await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return output;
    }

    /// <summary>Raises the window. Optional arg shows that artifact first.</summary>
    private string CmdFocus(string arg)
    {
        var shown = string.IsNullOrEmpty(arg) ? null : CmdShow(arg);

        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        if (!IsActive)
        {
            // Windows refuses SetForegroundWindow to a process that doesn't own the
            // foreground; a brief topmost flip is the usual way through it. Restores
            // whatever the 📌 pin was set to rather than clobbering it.
            var pinned = Topmost;
            Topmost = true;
            Topmost = pinned;
            Activate();
        }

        // IsActive can report true while the window still isn't in front, so the
        // caller gets the real answer from the OS rather than WPF's view of it
        var state = IsForeground() ? "focused" : "raised (foreground lock — taskbar flash only)";
        return shown is null ? state : $"{state}: {shown}";
    }

    private async Task<string> CmdPdf(string arg)
    {
        if (!_webReady || _renderedPath is null) throw new InvalidOperationException("nothing rendered yet");
        var entry = _scanned.FirstOrDefault(f =>
            string.Equals(f.Path, _renderedPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("the current artifact is no longer on disk");

        // Unlike the menu, the control channel never prompts — default beside the source
        var output = string.IsNullOrEmpty(arg)
            ? System.IO.Path.ChangeExtension(_renderedPath, ".pdf")
            : arg;
        return await ExportPdf(entry, output) ?? throw new InvalidOperationException("export cancelled");
    }

    private string CmdShow(string arg)
    {
        if (string.IsNullOrEmpty(arg)) throw new InvalidOperationException("show needs a file name");
        var wanted = arg.Contains('\\') || arg.Contains('/')
            ? System.IO.Path.GetFullPath(arg)
            : System.IO.Path.Combine(_watchDir, arg);
        var entry = _scanned.FirstOrDefault(f => string.Equals(f.Path, wanted, StringComparison.OrdinalIgnoreCase))
                 ?? _scanned.FirstOrDefault(f => string.Equals(f.Name, arg, StringComparison.OrdinalIgnoreCase));
        if (entry is null) throw new InvalidOperationException($"no artifact named '{arg}' in {_watchDir}");

        if (_closed.Remove(entry.Path))
        {
            SaveClosed();
            var i = 0;
            while (i < _files.Count && _files[i].LastWrite <= entry.LastWrite) i++;
            _files.Insert(i, entry);
            RebuildSidebar();
        }
        SelectEntry(entry.Path);
        return entry.Path;
    }

    private async Task<string> CmdScrollTo(string arg)
    {
        if (string.IsNullOrEmpty(arg)) throw new InvalidOperationException("scroll-to needs a heading or anchor id");
        if (!_webReady || _renderedPath is null) throw new InvalidOperationException("nothing rendered yet");
        var query = JsonSerializer.Serialize(arg.TrimStart('#'));
        var result = await Web.CoreWebView2.ExecuteScriptAsync($$"""
            (() => {
              const q = {{query}}.toLowerCase();
              const all = [...document.querySelectorAll('h1,h2,h3,h4,h5,h6,[id]')];
              const el = all.find(e => (e.id || '').toLowerCase() === q)
                      || all.find(e => /^H[1-6]$/.test(e.tagName) && e.textContent.trim().toLowerCase().includes(q));
              if (!el) return 'not-found';
              el.scrollIntoView({ behavior: 'smooth', block: 'start' });
              return 'ok';
            })()
            """);
        if (result.Contains("not-found")) throw new InvalidOperationException($"no heading or anchor matching '{arg}'");
        return $"scrolled to '{arg}'";
    }

    // ---------- PDF export ----------
    // Prints the live render through the page's @media print rules. No external
    // tooling: WebView2 already is the Chromium that would otherwise be shelled out to.

    /// <summary>Waits for the current artifact to finish rendering before it's printed.</summary>
    private async Task WaitForRenderAsync()
    {
        var primary = _expectRenderSignal ? _renderSignal : _navSignal;
        if (primary is not null)
        {
            // CDN-backed renderers (mermaid, docx-preview) can be slow or, offline,
            // never finish. Printing a partial page beats hanging on the export.
            var budget = TimeSpan.FromSeconds(_expectRenderSignal ? 12 : 5);
            await Task.WhenAny(primary.Task, Task.Delay(budget));
        }
        await Task.Delay(150); // let layout settle after the last script mutation
    }

    /// <summary>
    /// Exports an artifact to PDF. <paramref name="outputPath"/> null prompts for a
    /// destination; the control channel passes one. Returns null if cancelled.
    /// </summary>
    private async Task<string?> ExportPdf(FileEntry entry, string? outputPath)
    {
        if (!_webReady) throw new InvalidOperationException("viewer not ready yet");

        var ext = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
        if (NonPrintableExtensions.Contains(ext))
            throw new InvalidOperationException($"{ext} artifacts can't be exported to PDF");

        // PrintToPdfAsync prints what's on screen, so the tab has to be showing
        if (!string.Equals(_renderedPath, entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!NavOrder.Any(f => PathEq(f.Path, entry.Path)))
                throw new InvalidOperationException($"'{entry.Name}' has no open tab to render");
            SelectEntry(entry.Path);
        }
        await WaitForRenderAsync();

        string output;
        if (outputPath is null)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export to PDF",
                FileName = System.IO.Path.GetFileNameWithoutExtension(entry.Name) + ".pdf",
                DefaultExt = ".pdf",
                Filter = "PDF document (*.pdf)|*.pdf",
                // Defaults into the watch folder so the export lands as its own artifact
                InitialDirectory = _watchDir,
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) != true) return null;
            output = dlg.FileName;
        }
        else
        {
            output = System.IO.Path.GetFullPath(outputPath);
            if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);

        var settings = Web.CoreWebView2.Environment.CreatePrintSettings();
        settings.ShouldPrintBackgrounds = true;  // table shading, code blocks, mermaid fills
        settings.ShouldPrintHeaderAndFooter = true;
        settings.HeaderTitle = entry.Name;
        settings.FooterUri = "";                 // the render.viewer URL is noise; date + page number remain
        settings.MarginTop = settings.MarginBottom = 0.5;
        settings.MarginLeft = settings.MarginRight = 0.55;

        if (!await Web.CoreWebView2.PrintToPdfAsync(output, settings))
            throw new InvalidOperationException("the print job failed");
        return output;
    }

    private async void TabExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FileEntry entry) return;
        try
        {
            var output = await ExportPdf(entry, null);
            if (output is not null) TxtDate.Text = $"exported → {System.IO.Path.GetFileName(output)}";
        }
        catch (Exception ex)
        {
            TxtDate.Text = $"export failed: {ex.Message}";
        }
    }

    // ---------- Keep (promote to a durable folder) ----------

    private bool PickKeepDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your keep folder (where kept artifacts are copied)" };
        if (dlg.ShowDialog(this) != true) return false;
        SaveSetting("keepDir", dlg.FolderName);
        return true;
    }

    private void BtnKeep_Click(object sender, RoutedEventArgs e)
    {
        if (_renderedPath is null || !File.Exists(_renderedPath)) return;
        var keepDir = LoadSetting("keepDir");
        if (keepDir is null || !Directory.Exists(keepDir))
        {
            if (!PickKeepDir()) return;
            keepDir = LoadSetting("keepDir")!;
        }

        // Never overwrite in the keep folder — it's a store, not a scratchpad
        var name = System.IO.Path.GetFileNameWithoutExtension(_renderedPath);
        var ext = System.IO.Path.GetExtension(_renderedPath);
        var dst = System.IO.Path.Combine(keepDir, name + ext);
        for (var n = 2; File.Exists(dst); n++)
            dst = System.IO.Path.Combine(keepDir, $"{name} ({n}){ext}");

        try
        {
            File.Copy(_renderedPath, dst);
            TxtDate.Text = $"kept → {System.IO.Path.GetFileName(dst)}";
        }
        catch (Exception ex)
        {
            TxtDate.Text = $"keep failed: {ex.Message}";
        }
    }

    private void BtnKeep_RightClick(object sender, MouseButtonEventArgs e) => PickKeepDir();

    // ---------- Drag & drop ----------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) ImportFiles(dropped);
    }

    private void Web_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch (Exception) { return; } // non-string message from page content

        if (message == "render-done")
        {
            _renderSignal?.TrySetResult(true);
            return;
        }
        if (message != "files-dropped") return;

        var paths = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
        if (paths is { Length: > 0 }) ImportFiles(paths);
    }

    private void ImportFiles(string[] dropped)
    {
        string? lastAdded = null;
        foreach (var src in dropped)
        {
            if (!File.Exists(src)) continue; // skips directories too
            if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(src).ToLowerInvariant())) continue;

            var dst = System.IO.Path.Combine(_watchDir, System.IO.Path.GetFileName(src));
            if (string.Equals(System.IO.Path.GetFullPath(src), System.IO.Path.GetFullPath(dst),
                    StringComparison.OrdinalIgnoreCase))
            {
                lastAdded = dst; // already in the watch folder — just show it
                continue;
            }

            try
            {
                File.Copy(src, dst, overwrite: true);
                // Copy preserves the source timestamp; bump it so the file sorts newest
                File.SetLastWriteTime(dst, DateTime.Now);
                lastAdded = dst;
            }
            catch (Exception) { /* locked/unreadable source — skip it */ }
        }

        if (lastAdded is null) return;
        Rescan(selectLatest: false);
        SelectEntry(lastAdded);
    }

    // ---------- Navigation ----------

    private bool _syncingSelection;

    private void RebuildSidebar()
    {
        _syncingSelection = true;
        var selectedPath = (TabStrip.SelectedItem as FileEntry)?.Path;
        _allFiles.Clear();
        foreach (var f in _scanned)
            _allFiles.Add(new SidebarItem(f, _closed.ContainsKey(f.Path)));
        if (selectedPath is not null)
            SideList.SelectedItem = _allFiles.FirstOrDefault(s => s.Entry.Path == selectedPath);
        _syncingSelection = false;
        WriteTabsState();
    }

    /// <summary>
    /// The one logically selected artifact. Two rows hold one selection between
    /// them, so exactly one of the strips has a selected item at a time.
    /// </summary>
    private FileEntry? CurrentEntry =>
        (PinnedStrip.SelectedItem as FileEntry) ?? (TabStrip.SelectedItem as FileEntry);

    /// <summary>Navigation order: pinned row first, then the chronological row.</summary>
    private List<FileEntry> NavOrder => _pinnedFiles.Concat(_files).ToList();

    /// <summary>Selects an artifact in whichever row holds it, clearing the other.</summary>
    private void SelectEntry(string path)
    {
        var pinnedMatch = _pinnedFiles.FirstOrDefault(f => PathEq(f.Path, path));
        var openMatch = _files.FirstOrDefault(f => PathEq(f.Path, path));
        if (pinnedMatch is null && openMatch is null) return;

        // Clear the other row silently, so only the real selection raises an event
        _syncingSelection = true;
        if (pinnedMatch is not null) TabStrip.SelectedItem = null;
        else PinnedStrip.SelectedItem = null;
        _syncingSelection = false;

        if (pinnedMatch is not null) PinnedStrip.SelectedItem = pinnedMatch;
        else TabStrip.SelectedItem = openMatch;
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (TabStrip.SelectedItem is FileEntry entry)
        {
            _syncingSelection = true;
            PinnedStrip.SelectedItem = null;
            _syncingSelection = false;
            OnEntrySelected(entry, TabStrip);
        }
        UpdateNavButtons();
    }

    private void PinnedStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (PinnedStrip.SelectedItem is FileEntry entry)
        {
            _syncingSelection = true;
            TabStrip.SelectedItem = null;
            _syncingSelection = false;
            OnEntrySelected(entry, PinnedStrip);
        }
        UpdateNavButtons();
    }

    private void OnEntrySelected(FileEntry entry, ListBox strip)
    {
        _syncingSelection = true;
        var item = _allFiles.FirstOrDefault(s => s.Entry.Path == entry.Path);
        SideList.SelectedItem = item;
        if (item is not null) SideList.ScrollIntoView(item);
        _syncingSelection = false;

        strip.ScrollIntoView(entry);
        _ = ShowEntry(entry);
    }

    private void SideList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (SideList.SelectedItem is not SidebarItem item) return;

        if (item.IsClosed)
        {
            // Clicking a greyed (closed) doc reopens its tab
            _closed.Remove(item.Entry.Path);
            SaveClosed();
            var i = 0;
            while (i < _files.Count && _files[i].LastWrite <= item.Entry.LastWrite) i++;
            _files.Insert(i, item.Entry);
            RebuildSidebar();
        }
        SelectEntry(item.Entry.Path);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry entry) return;
        CloseTabs(new[] { entry });
    }

    /// <summary>Closes other tabs, keeping pinned ones — pinning means "this stays".</summary>
    private void TabCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry keep) return;
        CloseTabs(_files.Where(f => !PathEq(f.Path, keep.Path)).ToList());
    }

    private void TabCloseAll_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseTabs(_files.ToList());
    }

    /// <summary>
    /// Hides tabs until their files are rewritten. Non-destructive: everything stays
    /// on disk and in the sidebar, greyed, one click from coming back.
    /// </summary>
    private void CloseTabs(IReadOnlyCollection<FileEntry> entries)
    {
        if (entries.Count == 0) return;

        var order = NavOrder;
        var currentPath = CurrentEntry?.Path;
        var fallbackIndex = currentPath is null ? -1 : order.FindIndex(f => PathEq(f.Path, currentPath));

        var pinsChanged = false;
        foreach (var entry in entries)
        {
            _closed[entry.Path] = entry.LastWrite;
            // Closing a pinned tab unpins it, rather than leaving a pin pointing at a hidden tab
            pinsChanged |= _pinnedPaths.RemoveAll(p => PathEq(p, entry.Path)) > 0;
        }
        SaveClosed();
        if (pinsChanged) SavePinned();

        foreach (var entry in entries)
        {
            var inMain = _files.FirstOrDefault(f => PathEq(f.Path, entry.Path));
            if (inMain is not null) _files.Remove(inMain);
            var inPinned = _pinnedFiles.FirstOrDefault(f => PathEq(f.Path, entry.Path));
            if (inPinned is not null) _pinnedFiles.Remove(inPinned);
        }

        PinnedStrip.Visibility = _pinnedFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RebuildSidebar();

        var remaining = NavOrder;
        if (remaining.Count == 0)
        {
            TxtEmpty.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Collapsed;
            TxtTitle.Text = "Waiting for artifacts…";
            TxtDate.Text = "";
            Title = "Artifact Viewer";
            _renderedPath = null;
        }
        else if (CurrentEntry is null)
        {
            // Selection went with a closed tab — land on its nearest surviving neighbour
            var idx = Math.Clamp(fallbackIndex < 0 ? 0 : fallbackIndex, 0, remaining.Count - 1);
            SelectEntry(remaining[idx].Path);
        }
        UpdateNavButtons();
    }

    // ---------- Pinning ----------

    private void TabMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (menu.DataContext is not FileEntry entry) return;

        var pinItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "pin");
        if (pinItem is not null)
            pinItem.Header = IsPinned(entry.Path) ? "Unpin tab" : "Pin tab";
    }

    private void TabPin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry entry) return;

        if (IsPinned(entry.Path)) _pinnedPaths.RemoveAll(p => PathEq(p, entry.Path));
        else _pinnedPaths.Add(entry.Path);
        SavePinned();

        // Rescan rebuilds both rows from the pinned list; keep the same artifact showing
        Rescan(selectLatest: false);
        SelectEntry(entry.Path);
        UpdateNavButtons();
    }

    private void SideTrash_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not SidebarItem item) return;
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                item.Entry.Path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // The watcher's Deleted event triggers the rescan that removes it from both lists
    }

    private void BtnSidebar_Changed(object sender, RoutedEventArgs e) =>
        Sidebar.Visibility = BtnSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void BtnNext_Click(object sender, RoutedEventArgs e) => Step(+1);

    private void Step(int delta)
    {
        var order = NavOrder;
        if (order.Count == 0) return;
        var current = CurrentEntry;
        var at = current is null ? -1 : order.FindIndex(f => PathEq(f.Path, current.Path));
        var idx = Math.Clamp((at < 0 ? 0 : at) + delta, 0, order.Count - 1);
        SelectEntry(order[idx].Path);
    }

    private void UpdateNavButtons()
    {
        var order = NavOrder;
        var current = CurrentEntry;
        var at = current is null ? -1 : order.FindIndex(f => PathEq(f.Path, current.Path));

        BtnPrev.IsEnabled = at > 0;
        BtnNext.IsEnabled = at >= 0 && at < order.Count - 1;
        TxtCounter.Text = order.Count == 0 || at < 0 ? "" : $"{at + 1} / {order.Count}";
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private bool IsForeground()
    {
        try
        {
            return GetForegroundWindow() == new System.Windows.Interop.WindowInteropHelper(this).Handle;
        }
        catch (Exception) { return IsActive; }
    }

    private static bool IsKeyHeld(int vk) => (GetKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// App shortcuts, intercepted from the thread's message queue.
    /// <para>
    /// Needed because after every render, keyboard focus sits inside WebView2's own
    /// child window. Those key messages never reach WPF's input system, so
    /// <see cref="Window_PreviewKeyDown"/> alone means the shortcuts work exactly once
    /// — until the first artifact loads. Disabling the browser's accelerator keys
    /// stops Chromium acting on them but doesn't hand them to WPF either.
    /// </para>
    /// Only the app's own combinations are claimed, so typing inside an HTML artifact
    /// is unaffected.
    /// </summary>
    private void OnThreadPreprocessMessage(ref System.Windows.Interop.MSG msg, ref bool handled)
    {
        const int WmKeyDown = 0x0100, WmSysKeyDown = 0x0104;
        if (msg.message != WmKeyDown && msg.message != WmSysKeyDown) return;
        if (!IsActive) return;   // another window has focus; not ours to claim

        const int VkControl = 0x11, VkMenu = 0x12;
        const int VkLeft = 0x25, VkRight = 0x27, VkZero = 0x30, VkNumPad0 = 0x60;
        const int VkAdd = 0x6B, VkSubtract = 0x6D, VkOemPlus = 0xBB, VkOemMinus = 0xBD;

        // Modifiers via Win32: WPF's key state isn't updated by messages aimed at
        // the WebView2 child window
        var alt = IsKeyHeld(VkMenu);
        var ctrl = IsKeyHeld(VkControl);
        var vk = (int)msg.wParam;

        if (alt && !ctrl)
        {
            if (vk == VkLeft) { Step(-1); handled = true; }
            else if (vk == VkRight) { Step(+1); handled = true; }
        }
        else if (ctrl && !alt)
        {
            if (vk is VkOemPlus or VkAdd) { StepZoom(+0.1); handled = true; }
            else if (vk is VkOemMinus or VkSubtract) { StepZoom(-0.1); handled = true; }
            else if (vk is VkZero or VkNumPad0) { SetZoom(1.0); handled = true; }
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Left) { Step(-1); e.Handled = true; }
            else if (key == Key.Right) { Step(+1); e.Handled = true; }
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    StepZoom(+0.1); e.Handled = true; break;
                case Key.OemMinus or Key.Subtract:
                    StepZoom(-0.1); e.Handled = true; break;
                case Key.D0 or Key.NumPad0:
                    SetZoom(1.0); e.Handled = true; break;
            }
        }
    }

    // ---------- Zoom ----------

    private const double ZoomMin = 0.5, ZoomMax = 3.0;
    private double _zoom = 1.0;

    private void StepZoom(double delta) => SetZoom(_zoom + delta);

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(Math.Round(zoom, 2), ZoomMin, ZoomMax);
        ApplyZoom();
        TxtDate.Text = $"zoom {_zoom * 100:0}%";
        SaveSetting("zoom", _zoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Reapplied after every navigation — WebView2 doesn't carry it across.</summary>
    private void ApplyZoom()
    {
        if (_webReady) Web.ZoomFactor = _zoom;
    }

    private void BtnPin_Changed(object sender, RoutedEventArgs e) => Topmost = BtnPin.IsChecked == true;

    private void BtnFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_watchDir}\"") { UseShellExecute = true });

    private void BtnFolder_RightClick(object sender, MouseButtonEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose folder to watch",
            InitialDirectory = _watchDir
        };
        if (dlg.ShowDialog(this) != true || string.Equals(dlg.FolderName, _watchDir, StringComparison.OrdinalIgnoreCase))
            return;

        _watchDir = dlg.FolderName;
        Directory.CreateDirectory(_watchDir);
        SaveSetting("watchDir", _watchDir);

        if (_watcher is not null) _watcher.Path = _watchDir;
        if (_webReady)
        {
            Web.CoreWebView2.ClearVirtualHostNameToFolderMapping(VirtualHost);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, _watchDir, CoreWebView2HostResourceAccessKind.Allow);
        }
        Rescan(selectLatest: true);
    }

    // ---------- Rendering ----------

    private async Task ShowEntry(FileEntry entry)
    {
        if (!_webReady) return;

        // Rendering something means the empty state is over. Set here rather than at
        // the call sites: reopening a closed tab (sidebar click, or the show command)
        // adds it directly without a Rescan, and Rescan used to be the only place
        // that revealed the WebView again — so after Close all tabs, reopening a doc
        // rendered it behind the "No artifacts yet" message.
        TxtEmpty.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;

        TxtTitle.Text = entry.Name;
        TxtDate.Text = entry.LastWrite.ToString("MMM d, h:mm:ss tt");
        Title = $"{entry.Name} — Artifact Viewer";
        _renderedPath = entry.Path;
        _renderedWrite = entry.LastWrite;
        WriteCurrentDocState(entry.Path);
        WriteTabsState();

        var ext = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();

        // Arm the render-settled signals before navigating (see _renderSignal)
        _expectRenderSignal = ext is ".md" or ".markdown" or ".ipynb" or ".docx" or ".xlsx" or ".csv" or ".tsv"
            || CodeExtensions.Contains(ext);
        _renderSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (ext is ".md" or ".markdown")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (text is null) return;
            // Served from a cache file via its own virtual host: a real https origin
            // (NavigateToString's about:blank origin breaks dynamic ESM imports like mermaid)
            var rendered = System.IO.Path.Combine(_renderDir, "current.html");
            await File.WriteAllTextAsync(rendered, BuildMarkdownHtml(text));
            Web.CoreWebView2.Navigate($"https://{RenderHost}/current.html?v={entry.LastWrite.Ticks}");
        }
        else if (ext == ".ipynb")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (text is null) return;
            var rendered = System.IO.Path.Combine(_renderDir, "current.html");
            await File.WriteAllTextAsync(rendered, BuildNotebookHtml(text));
            Web.CoreWebView2.Navigate($"https://{RenderHost}/current.html?v={entry.LastWrite.Ticks}");
        }
        else if (ext is ".docx" or ".xlsx")
        {
            // Copied beside the render page so the fetch is same-origin
            // (cross-virtual-host fetches are blocked by CORS)
            var cached = System.IO.Path.Combine(_renderDir, "current" + ext);
            if (!await CopyWithRetry(entry.Path, cached)) return;
            var page = System.IO.Path.Combine(_renderDir, "office.html");
            await File.WriteAllTextAsync(page, BuildOfficeHtml(ext, entry.LastWrite.Ticks));
            Web.CoreWebView2.Navigate($"https://{RenderHost}/office.html?v={entry.LastWrite.Ticks}");
        }
        else if (ext is ".csv" or ".tsv")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (text is null) return;
            var page = System.IO.Path.Combine(_renderDir, "csv.html");
            await File.WriteAllTextAsync(page, BuildCsvHtml(text, ext == ".tsv" ? '\t' : ','));
            Web.CoreWebView2.Navigate($"https://{RenderHost}/csv.html?v={entry.LastWrite.Ticks}");
        }
        else if (CodeExtensions.Contains(ext))
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (text is null) return;
            var page = System.IO.Path.Combine(_renderDir, "code.html");
            await File.WriteAllTextAsync(page, BuildCodeHtml(ext, text));
            Web.CoreWebView2.Navigate($"https://{RenderHost}/code.html?v={entry.LastWrite.Ticks}");
        }
        else
        {
            // Served through the virtual host so Chromium handles it natively
            // (PDF viewer, images, media playback, HTML with relative asset paths, etc.)
            Web.CoreWebView2.Navigate($"https://{VirtualHost}/{Uri.EscapeDataString(entry.Name)}");
        }
    }

    /// <summary>Row cap — past this the table stops being readable and starts being slow.</summary>
    private const int CsvRowLimit = 5000;

    /// <summary>
    /// Splits delimited text per RFC 4180: quoted fields may contain the delimiter
    /// and newlines, and "" is a literal quote.
    /// </summary>
    private static List<List<string>> ParseDelimited(string text, char delimiter)
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
    private static string BuildCsvHtml(string text, char delimiter)
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
    private static string BuildNotebookHtml(string json)
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

    private static readonly Dictionary<string, string> HljsLanguage = new()
    {
        [".py"] = "python", [".ps1"] = "powershell", [".yml"] = "yaml",
        [".cs"] = "csharp", [".rs"] = "rust", [".sh"] = "bash",
        [".bat"] = "dos", [".cmd"] = "dos", [".h"] = "cpp", [".rb"] = "ruby",
        [".jsx"] = "javascript", [".ts"] = "typescript", [".tsx"] = "typescript",
        [".toml"] = "ini", [".cfg"] = "ini", [".conf"] = "ini",
        [".log"] = "plaintext"
    };

    private static string BuildCodeHtml(string ext, string text)
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

    private static async Task<bool> CopyWithRetry(string src, string dst)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var source = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dest = new FileStream(dst, FileMode.Create, FileAccess.Write);
                await source.CopyToAsync(dest);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
        }
        return false;
    }

    private static string BuildOfficeHtml(string ext, long ticks) => ext == ".docx"
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

    private static async Task<string?> ReadTextWithRetry(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                await Task.Delay(250);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

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

    private static string BuildMarkdownHtml(string markdown) =>
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
