using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace ArtifactViewer;

public record FileEntry(string Path, string Name, DateTime LastWrite);

public record SidebarItem(FileEntry Entry, bool IsClosed);

public partial class MainWindow : Window
{
    // Everything rendered through the code shell — which is about presentation, not
    // about being code: .log, .txt and .json are here because Chromium's native view
    // of a plain-text file is unstyled black-on-white with a "Pretty-print" checkbox,
    // jarring against the app's own chrome. The shell gives them the dark page and
    // highlighting the rest of the viewer already has.
    private static readonly string[] CodeExtensions =
    {
        ".py", ".js", ".ts", ".jsx", ".tsx", ".cs", ".sql", ".yaml", ".yml",
        ".toml", ".xml", ".log", ".txt", ".json", ".ps1", ".sh", ".bat", ".cmd",
        ".c", ".cpp", ".h", ".java", ".rb", ".go", ".rs", ".php", ".css",
        ".ini", ".cfg", ".conf"
    };

    // Artifacts whose source is worth putting on the clipboard as text — the point is
    // getting a script or a table out of the viewer and into something else (SSMS, an
    // editor, an email). Binary and rendered-only formats are excluded; images copy as
    // an image instead, and .pdf/.docx/.xlsx/media copy as nothing.
    private static readonly string[] TextCopyExtensions = new[]
    {
        ".md", ".markdown", ".csv", ".tsv", ".html", ".htm", ".svg", ".ipynb"
    }.Concat(CodeExtensions).ToArray();

    private static readonly string[] ImageCopyExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private static readonly string[] SupportedExtensions = new[]
    {
        ".md", ".markdown", ".ipynb", ".html", ".htm", ".pdf", ".svg",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".avif",
        ".docx", ".xlsx", ".csv", ".tsv",
        ".mp4", ".webm", ".mp3", ".wav"
    }.Concat(CodeExtensions).ToArray();

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

    // ShowEntry is called fire-and-forget from several places and every text renderer
    // writes the *same* file in the render folder, so two quick selections could
    // interleave at the awaits — the loser overwriting the winner's render and
    // navigating on top of it, leaving current.txt and tabs.json naming one document
    // while the screen showed another. The lock keeps one render in flight at a time
    // (two concurrent writes to one render file can also just fail); the generation
    // counter lets a superseded render drop out instead of doing work nobody sees.
    private int _showGeneration;
    private readonly SemaphoreSlim _showLock = new(1, 1);

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
            Renderers.VirtualHost, _watchDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Renderers.RenderHost, _renderDir, CoreWebView2HostResourceAccessKind.Allow);
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
    // "pdf [pdf-path]" | "copy [file]" | "focus [file]" to
    // %LOCALAPPDATA%\ArtifactViewer\command.txt; the outcome lands in command-result.txt.

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

    /// <summary>Backstop for a command file that can't be deleted — bounded work per event.</summary>
    private const int MaxCommandsPerBatch = 10;

    private async Task ProcessCommandFile()
    {
        if (_processingCommand) return;
        var cmdFile = System.IO.Path.Combine(_appDataDir, "command.txt");
        if (!File.Exists(cmdFile)) return;
        _processingCommand = true;
        try
        {
            // A command written while an earlier one is still processing — easily done,
            // since capture/pdf/scroll-to await for seconds — fires a watcher event that
            // this latch swallows, and nothing would ever revisit the file. Under the
            // documented "write a line, wait, read the result" contract that dropped
            // command is indistinguishable from a hang, so keep going until the file
            // stays gone. Handlers are all dispatched here, so a write landing after the
            // last File.Exists queues a fresh callback that runs once this work item
            // completes — by which point the finally below has cleared the latch.
            for (var n = 0; n < MaxCommandsPerBatch && File.Exists(cmdFile); n++)
            {
                var text = (await ReadTextWithRetry(cmdFile))?.Trim();
                try { File.Delete(cmdFile); } catch (Exception) { /* re-processing is harmless */ }
                if (string.IsNullOrEmpty(text)) continue;

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
                        "copy" => CmdCopy(arg),
                        "focus" => CmdFocus(arg),
                        _ => throw new InvalidOperationException(
                            $"unknown command '{verb}' (capture | show | scroll-to | pdf | copy | focus)")
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

    /// <summary>
    /// "copy [file]" — clipboard the named artifact (showing it on the way, like focus
    /// does) or the current one. Same rules as the tab menu: source text for text-ish
    /// artifacts, a bitmap for images, an error for anything else.
    /// </summary>
    private string CmdCopy(string arg)
    {
        var path = string.IsNullOrEmpty(arg)
            ? _renderedPath ?? throw new InvalidOperationException("nothing rendered yet")
            : CmdShow(arg);

        var detail = CopyArtifact(path);
        TxtDate.Text = $"{detail} → clipboard";
        return $"{detail}: {path}";
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

    // ---------- Copy to clipboard ----------

    private static bool CanCopyText(string path) =>
        TextCopyExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    private static bool CanCopyImage(string path) =>
        ImageCopyExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// WPF's Clipboard has no retrying overload (that one is WinForms), and the single-shot
    /// call throws CLIPBRD_E_CANT_OPEN whenever another process has the clipboard open.
    /// </summary>
    private static void SetClipboard(object payload)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { Clipboard.SetDataObject(payload, true); return; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Puts an artifact on the clipboard and describes what landed there. Shared by the
    /// tab menu and the control channel's "copy" so both behave identically.
    /// </summary>
    private static string CopyArtifact(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("the artifact is no longer on disk");

        if (CanCopyText(path))
        {
            var text = File.ReadAllText(path);
            SetClipboard(text);
            var lines = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;
            return $"copied {lines:N0} lines";
        }

        if (CanCopyImage(path))
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // don't hold the file open
            image.UriSource = new Uri(path);
            image.EndInit();
            SetClipboard(image);
            return "copied image";
        }

        throw new InvalidOperationException(
            $"nothing to copy from a {System.IO.Path.GetExtension(path)} artifact");
    }

    private void TabCopy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry entry) return;
        try { TxtDate.Text = $"{CopyArtifact(entry.Path)} → clipboard"; }
        catch (Exception ex) { TxtDate.Text = $"copy failed: {ex.Message}"; }
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

        // Copy says what it will actually put on the clipboard, and greys out where
        // there is nothing sensible to copy (.pdf, Office files, audio, video)
        var copyItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Tag as string) == "copy");
        if (copyItem is not null)
        {
            copyItem.Header = CanCopyImage(entry.Path) ? "Copy image" : "Copy contents";
            copyItem.IsEnabled = CanCopyText(entry.Path) || CanCopyImage(entry.Path);
        }
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

    /// <summary>
    /// Showing the sidebar takes its width out of the tab strip beside it, so a strip
    /// that was scrolled to the selected tab can end up with that tab half off the
    /// right edge — and the selected tab is usually the newest one, at the far right.
    /// Re-scroll after the layout settles, or it stays clipped until the next selection.
    /// </summary>
    private void BtnSidebar_Changed(object sender, RoutedEventArgs e)
    {
        Sidebar.Visibility = BtnSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.BeginInvoke(() =>
        {
            if (TabStrip.SelectedItem is FileEntry tab) TabStrip.ScrollIntoView(tab);
            if (PinnedStrip.SelectedItem is FileEntry pinned) PinnedStrip.ScrollIntoView(pinned);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

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
            Web.CoreWebView2.ClearVirtualHostNameToFolderMapping(Renderers.VirtualHost);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                Renderers.VirtualHost, _watchDir, CoreWebView2HostResourceAccessKind.Allow);
        }
        Rescan(selectLatest: true);
    }

    // ---------- Rendering ----------

    private async Task ShowEntry(FileEntry entry)
    {
        if (!_webReady) return;

        var ext = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();

        // Arm the render-settled signals before navigating (see _renderSignal) — and
        // synchronously, before this method first yields. ExportPdf calls SelectEntry
        // and then immediately awaits WaitForRenderAsync, so arming any later would
        // leave the export waiting on the *previous* render's already-completed
        // signal and printing the page it replaced.
        _expectRenderSignal = ext is ".md" or ".markdown" or ".ipynb" or ".docx" or ".xlsx" or ".csv" or ".tsv" or ".svg"
            || CodeExtensions.Contains(ext);
        _renderSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var gen = ++_showGeneration;
        await _showLock.WaitAsync();
        try
        {
            // Superseded while queued — the newer selection is already on its way
            if (gen == _showGeneration) await ShowEntryCore(entry, ext, gen);
        }
        finally { _showLock.Release(); }
    }

    /// <summary>
    /// Builds the render, then commits to it. Nothing user-visible moves until the
    /// navigation is certain: a read that fails (file locked by its writer, or deleted
    /// between the scan and here) used to leave the title bar, current.txt and
    /// tabs.json all naming a document that never reached the screen.
    /// </summary>
    private async Task ShowEntryCore(FileEntry entry, string ext, int gen)
    {
        var v = entry.LastWrite.Ticks;
        string url;

        if (ext is ".md" or ".markdown")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (gen != _showGeneration) return;
            if (text is null) { ShowUnreadable(entry); return; }
            // Served from a cache file via its own virtual host: a real https origin
            // (NavigateToString's about:blank origin breaks dynamic ESM imports like mermaid)
            var rendered = System.IO.Path.Combine(_renderDir, "current.html");
            await File.WriteAllTextAsync(rendered, Renderers.BuildMarkdownHtml(text));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/current.html?v={v}";
        }
        else if (ext == ".ipynb")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (gen != _showGeneration) return;
            if (text is null) { ShowUnreadable(entry); return; }
            var rendered = System.IO.Path.Combine(_renderDir, "current.html");
            await File.WriteAllTextAsync(rendered, Renderers.BuildNotebookHtml(text));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/current.html?v={v}";
        }
        else if (ext is ".docx" or ".xlsx")
        {
            // Copied beside the render page so the fetch is same-origin
            // (cross-virtual-host fetches are blocked by CORS)
            var cached = System.IO.Path.Combine(_renderDir, "current" + ext);
            var copied = await CopyWithRetry(entry.Path, cached);
            if (gen != _showGeneration) return;
            if (!copied) { ShowUnreadable(entry); return; }
            var page = System.IO.Path.Combine(_renderDir, "office.html");
            await File.WriteAllTextAsync(page, Renderers.BuildOfficeHtml(ext, v));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/office.html?v={v}";
        }
        else if (ext is ".csv" or ".tsv")
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (gen != _showGeneration) return;
            if (text is null) { ShowUnreadable(entry); return; }
            var page = System.IO.Path.Combine(_renderDir, "csv.html");
            await File.WriteAllTextAsync(page, Renderers.BuildCsvHtml(text, ext == ".tsv" ? '\t' : ','));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/csv.html?v={v}";
        }
        else if (ext == ".sql")
        {
            // Scripts are read by scanning for the next definition, so batches get
            // banded backgrounds, a sticky object header, and a jump index. Falls back
            // to the plain code shell on its own when there's nothing to delimit.
            var text = await ReadTextWithRetry(entry.Path);
            if (gen != _showGeneration) return;
            if (text is null) { ShowUnreadable(entry); return; }
            var page = System.IO.Path.Combine(_renderDir, "sql.html");
            await File.WriteAllTextAsync(page, Renderers.BuildSqlHtml(text));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/sql.html?v={v}";
        }
        else if (CodeExtensions.Contains(ext))
        {
            var text = await ReadTextWithRetry(entry.Path);
            if (gen != _showGeneration) return;
            if (text is null) { ShowUnreadable(entry); return; }
            var page = System.IO.Path.Combine(_renderDir, "code.html");
            await File.WriteAllTextAsync(page, Renderers.BuildCodeHtml(ext, text));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/code.html?v={v}";
        }
        else if (ext == ".svg")
        {
            // Chromium renders a bare .svg at whatever size the file declares, top-left,
            // against a white page — a fixed-size drawing sits in the corner of a white
            // field. Framing it in a page of our own centres it on the document
            // background and fits it to the window (vector, so scaling costs nothing).
            var page = System.IO.Path.Combine(_renderDir, "svg.html");
            await File.WriteAllTextAsync(page, Renderers.BuildSvgHtml(Uri.EscapeDataString(entry.Name), v));
            if (gen != _showGeneration) return;
            url = $"https://{Renderers.RenderHost}/svg.html?v={v}";
        }
        else
        {
            // Served through the virtual host so Chromium handles it natively
            // (PDF viewer, images, media playback, HTML with relative asset paths, etc.)
            url = $"https://{Renderers.VirtualHost}/{Uri.EscapeDataString(entry.Name)}";
        }

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

        Web.CoreWebView2.Navigate(url);
    }

    /// <summary>
    /// The artifact couldn't be read — locked by whatever is writing it, or gone
    /// between the scan and here. Reported in the header (where "kept →" reports too)
    /// with the previous document left rendered, so the title bar and the state files
    /// keep describing what is actually on screen.
    /// </summary>
    private void ShowUnreadable(FileEntry entry) => TxtDate.Text = $"could not read {entry.Name}";

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

}
