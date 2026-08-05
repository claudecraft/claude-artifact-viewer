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

/// <summary>
/// Which folder an artifact came from. Origin is an attribute of the file, not of
/// the row it happens to sit in — a pinned team artifact moves to the pinned row
/// and keeps its badge — so it travels on the entry rather than on the collection.
/// </summary>
public enum ArtifactOrigin { Mine, Team }

public record FileEntry(string Path, string Name, DateTime LastWrite,
                        ArtifactOrigin Origin = ArtifactOrigin.Mine);

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
    private readonly ObservableCollection<FileEntry> _teamFiles = new();

    // Opt-in second watch folder — a folder shared with colleagues (Dropbox, a
    // network share). Null until set, and while it's null nothing about the app
    // changes: no extra watcher, no third row, a greyed toolbar button.
    private string? _teamDir;
    private FileSystemWatcher? _teamWatcher;
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
    private const string VendoredLibsStamp = "mermaid-11.16.0 hljs-11.9.0+ps+dos";

    private static readonly string[] VendoredLibs =
        { "mermaid.min.js", "highlight.min.js", "highlight-dark.min.css",
          "powershell.min.js", "dos.min.js" };

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

        _teamDir = LoadSetting("teamDir") is { Length: > 0 } t && Directory.Exists(t) ? t : null;

        TabStrip.ItemsSource = _files;
        PinnedStrip.ItemsSource = _pinnedFiles;
        TeamStrip.ItemsSource = _teamFiles;
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
        UpdateTeamButton();
        Rescan(selectLatest: true);

        if (_initialFile is not null)
        {
            // CmdShow already handles reopening a closed tab and selecting it
            try { CmdShow(_initialFile); }
            catch (Exception) { /* unsupported type, or deleted between launch and scan */ }
            _initialFile = null;
        }

        CleanupUpdateLeftovers();

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
    // The check is the only network request this app makes on its own initiative.
    // It sends nothing but a User-Agent, runs once per launch, and is switched
    // off entirely by setting "checkForUpdates": "false" in settings.json. The
    // download itself only ever happens when the user clicks Update now.

    private const string ReleasesApiUrl =
        "https://api.github.com/repos/claudecraft/claude-artifact-viewer/releases/latest";
    private const string ReleasesPageUrl =
        "https://github.com/claudecraft/claude-artifact-viewer/releases/latest";

    private string? _updateTag, _updateExeUrl, _updateShaUrl;

    private async Task CheckForUpdateAsync()
    {
        if (string.Equals(LoadSetting("checkForUpdates"), "false", StringComparison.OrdinalIgnoreCase)) return;

        // Every launch, not throttled: one anonymous API call against GitHub's 60/hour
        // allowance, and Update now makes staying current cheap enough to offer often
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

            _updateTag = tag;
            if (doc.RootElement.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == "ArtifactViewer.exe") _updateExeUrl = url;
                    else if (name == "ArtifactViewer.exe.sha256") _updateShaUrl = url;
                }
            }

            TxtUpdate.Text = $"Version {tag} is available — you have v{current.ToString(3)}.";
            BtnUpdateReveal.ToolTip = Environment.ProcessPath is { Length: > 0 } exe
                ? $"Show the running program in Explorer, so you know which file to replace:\n{exe}"
                : "Show the running program in Explorer";
            BtnUpdateNow.Visibility = CanSelfUpdate() ? Visibility.Visible : Visibility.Collapsed;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        catch (Exception)
        {
            // Offline, rate-limited, or GitHub down: staying quiet is the right answer
        }
    }

    // ---------- Self-update ----------
    // A running exe can't be overwritten, but it can be renamed: download the new
    // exe, verify it against the published SHA-256, rename the running file to
    // .old, move the download into its place, relaunch. Settings, tabs and the
    // watched folder all live in %LOCALAPPDATA% and carry over untouched — and
    // because the path never changes, taskbar pins and shortcuts keep working.
    // A bonus over the manual route: HttpClient downloads carry no Mark-of-the-Web,
    // so updates skip the SmartScreen warning the browser download gets.

    /// <summary>Self-update needs both release assets and a writable exe location.</summary>
    private bool CanSelfUpdate()
    {
        if (_updateExeUrl is null || _updateShaUrl is null) return false;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        try
        {
            // Probe rather than inspect ACLs: Program Files installs fall back to
            // the manual buttons instead of failing mid-swap or prompting for admin
            var probe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(exe)!,
                ".av-update-probe-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception) { return false; }
    }

    private void BtnUpdateNow_Click(object sender, RoutedEventArgs e) => _ = InstallUpdateAsync();

    private async Task InstallUpdateAsync()
    {
        var exe = Environment.ProcessPath!;
        var staged = System.IO.Path.Combine(_appDataDir, "ArtifactViewer.exe.update");
        BtnUpdateNow.IsEnabled = false;
        try
        {
            // 66 MB on hotel wifi outlives the default 100-second timeout
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ArtifactViewer");

            TxtUpdate.Text = $"Downloading {_updateTag}…";
            var expected = (await http.GetStringAsync(_updateShaUrl)).Trim()
                .Split(' ', '\t')[0];

            using (var response = await http.GetAsync(_updateExeUrl,
                       System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(staged, FileMode.Create, FileAccess.Write);
                var buffer = new byte[1 << 16];
                long done = 0, shownMb = -1;
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    var mb = done >> 20;
                    if (mb != shownMb && total is > 0)
                    {
                        shownMb = mb;
                        TxtUpdate.Text = $"Downloading {_updateTag} — {mb} / {total >> 20} MB…";
                    }
                }
            }

            TxtUpdate.Text = "Verifying download…";
            string actual;
            await using (var f = File.OpenRead(staged))
                actual = Convert.ToHexString(
                    await System.Security.Cryptography.SHA256.HashDataAsync(f));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("the download didn't match its published checksum");

            TxtUpdate.Text = "Installing…";
            var old = exe + ".old";
            try { File.Delete(old); } catch (Exception) { /* a previous version still exiting */ }
            if (File.Exists(old)) old = exe + "." + Environment.ProcessId + ".old";
            try { File.Move(exe, old); }
            catch (IOException) { await Task.Delay(500); File.Move(exe, old); } // AV/OneDrive brushing past
            try { File.Move(staged, exe); }
            catch (Exception)
            {
                File.Move(old, exe); // roll back rather than leave no exe at all
                throw;
            }

            TxtUpdate.Text = "Restarting…";
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch (Exception) { /* nothing staged */ }
            TxtUpdate.Text = $"Automatic update failed ({ex.Message}) — use Get it to update by hand.";
            BtnUpdateNow.IsEnabled = true;
        }
    }

    /// <summary>
    /// Deletes what the last self-update couldn't: the renamed .old exe (locked
    /// until the old process finished exiting) and any half-downloaded stage file.
    /// </summary>
    private void CleanupUpdateLeftovers()
    {
        try { File.Delete(System.IO.Path.Combine(_appDataDir, "ArtifactViewer.exe.update")); }
        catch (Exception) { /* nothing staged, or still writing — next launch gets it */ }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(
                         System.IO.Path.GetDirectoryName(exe)!,
                         System.IO.Path.GetFileName(exe) + "*.old"))
                try { File.Delete(f); } catch (Exception) { /* old process still exiting */ }
        }
        catch (Exception) { /* directory gone or unreadable — nothing to clean */ }
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

        StartTeamWatcher();
    }

    /// <summary>
    /// Watches the team folder, if one is configured. Separate watcher rather than a
    /// recursive one: the folders are unrelated paths, and a shared folder is somebody
    /// else's disk as far as reliability goes — losing it shouldn't take the main
    /// watcher with it. Shares the debounce, so a burst across both folders is one rescan.
    /// </summary>
    private void StartTeamWatcher()
    {
        _teamWatcher?.Dispose();
        _teamWatcher = null;
        if (_teamDir is null || !Directory.Exists(_teamDir)) return;

        try
        {
            _teamWatcher = new FileSystemWatcher(_teamDir)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler onChange = (_, _) => Dispatcher.Invoke(BumpDebounce);
            _teamWatcher.Created += onChange;
            _teamWatcher.Changed += onChange;
            _teamWatcher.Deleted += onChange;
            _teamWatcher.Renamed += (_, _) => Dispatcher.Invoke(BumpDebounce);
        }
        catch (Exception)
        {
            // An unreachable share shouldn't stop the app: the folder simply
            // contributes nothing until the next rescan finds it again.
            _teamWatcher = null;
        }
    }

    private void BumpDebounce()
    {
        _debounce!.Stop();
        _debounce.Start();
    }

    private static List<FileEntry> ScanFolder(string dir, ArtifactOrigin origin) =>
        Directory.EnumerateFiles(dir)
            .Where(f => SupportedExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new FileInfo(f))
            .OrderBy(fi => fi.LastWriteTime)
            .Select(fi => new FileEntry(fi.FullName, fi.Name, fi.LastWriteTime, origin))
            .ToList();

    private void Rescan(bool selectLatest)
    {
        var scanned = ScanFolder(_watchDir, ArtifactOrigin.Mine);
        // A shared folder can be offline, syncing, or gone; it must never take the
        // main folder down with it, so it's scanned defensively and separately.
        if (_teamDir is not null)
        {
            try { scanned = scanned.Concat(ScanFolder(_teamDir, ArtifactOrigin.Team)).ToList(); }
            catch (Exception) { /* unreachable share: contributes nothing this pass */ }
        }
        scanned = scanned.OrderBy(f => f.LastWrite).ToList();

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
        bool GoneFromWatchedFolder(string path)
        {
            if (onDisk.Contains(path)) return false;
            var dir = System.IO.Path.GetDirectoryName(path) ?? "";
            // The team folder is watched too, so its files earn the same pruning —
            // but only while it's actually configured, or unsetting it would discard
            // the closed and pinned state of everything that came from there.
            return PathEq(dir, _watchDir.TrimEnd('\\'))
                || (_teamDir is not null && PathEq(dir, _teamDir.TrimEnd('\\')));
        }

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
        // Pinning wins over origin: a pinned team artifact sits in the pinned row and
        // keeps its badge. Row says what kind of tab it is, the badge says whose it is.
        var unpinned = open.Where(f => !IsPinned(f.Path)).ToList();
        var mine = unpinned.Where(f => f.Origin == ArtifactOrigin.Mine).ToList();
        var team = unpinned.Where(f => f.Origin == ArtifactOrigin.Team).ToList();

        var selectedPath = CurrentEntry?.Path;
        // A pinned tab shouldn't be yanked away by a newly written artifact — and
        // neither should a team one, which is the whole reason team has its own row:
        // a colleague writing a file must never pull the view off what you're reading.
        var onPinned = selectedPath is not null && IsPinned(selectedPath);
        var onTeam = selectedPath is not null && team.Any(f => PathEq(f.Path, selectedPath));
        var wasAtLatest = selectLatest
            || (!onPinned && !onTeam && (_files.Count == 0 || selectedPath == _files[^1].Path));

        _syncingSelection = true;
        _files.Clear();
        foreach (var f in mine) _files.Add(f);
        _pinnedFiles.Clear();
        foreach (var f in pinned) _pinnedFiles.Add(f);
        _teamFiles.Clear();
        foreach (var f in team) _teamFiles.Add(f);
        _syncingSelection = false;
        PinnedStrip.Visibility = _pinnedFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TeamStrip.Visibility = _teamFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RebuildSidebar();

        // Counts every row: everything open could be pinned or shared, leaving _files empty
        var order = NavOrder;
        var allTabs = AllTabs;
        TxtEmpty.Visibility = allTabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = allTabs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (allTabs.Count == 0)
        {
            TxtTitle.Text = "Waiting for artifacts…";
            TxtDate.Text = "";
            TxtCounter.Text = "";
            _renderedPath = null;
            UpdateNavButtons();
            return;
        }

        // "Latest" means the newest artifact of your own, falling back to the pinned
        // row, and only then to team — so a folder holding nothing but shared files
        // still shows something rather than an empty pane.
        var newest = _files.Count > 0 ? _files[^1]
                   : order.Count > 0 ? order[^1]
                   : allTabs[^1];
        var target = wasAtLatest
            ? newest
            : allTabs.FirstOrDefault(f => PathEq(f.Path, selectedPath ?? "")) ?? newest;

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
                // So an assistant reading this can tell whose artifact it is — and be
                // told to leave the team ones alone
                origin = f.Origin == ArtifactOrigin.Team ? "team" : "mine",
                current = string.Equals(f.Path, _renderedPath, StringComparison.OrdinalIgnoreCase)
            });
            File.WriteAllText(System.IO.Path.Combine(_appDataDir, "tabs.json"),
                JsonSerializer.Serialize(tabs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* non-fatal: tab listing just goes stale */ }
    }

    // ---------- Control channel (Claude Code drives the viewer via command.txt) ----------
    // Write "capture [png-path]" | "show <file>" | "scroll-to <heading|#id|line[-line]|text>" |
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
                        "share" => CmdShare(arg),
                        "focus" => CmdFocus(arg),
                        _ => throw new InvalidOperationException(
                            $"unknown command '{verb}' (capture | show | scroll-to | pdf | copy | share | focus)")
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
    /// <summary>
    /// "share [--move] [file]" — puts the current artifact, or a named one, into the
    /// team folder. Copies by default; --move is opt-in, because an assistant asked to
    /// share something shouldn't quietly remove it from your own folder. Deliberately
    /// never prompts for a folder: running unattended, it must fail with a message
    /// rather than open a dialog nobody is there to see.
    /// </summary>
    private string CmdShare(string arg)
    {
        var move = false;
        if (arg.StartsWith("--move", StringComparison.OrdinalIgnoreCase))
        {
            move = true;
            arg = arg[6..].Trim().Trim('"');
        }

        var path = string.IsNullOrEmpty(arg)
            ? _renderedPath ?? throw new InvalidOperationException("nothing rendered yet")
            : CmdShow(arg);

        var dst = ShareToTeam(path, move);
        TxtDate.Text = $"{(move ? "moved" : "shared")} → {System.IO.Path.GetFileName(dst)}";
        if (move)
        {
            Rescan(selectLatest: false);
            SelectEntry(dst);
        }
        return dst;
    }

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
            var row = entry.Origin == ArtifactOrigin.Team ? _teamFiles : _files;
            var i = 0;
            while (i < row.Count && row[i].LastWrite <= entry.LastWrite) i++;
            row.Insert(i, entry);
            if (entry.Origin == ArtifactOrigin.Team) TeamStrip.Visibility = Visibility.Visible;
            RebuildSidebar();
        }
        SelectEntry(entry.Path);
        return entry.Path;
    }

    private async Task<string> CmdScrollTo(string arg)
    {
        if (string.IsNullOrEmpty(arg)) throw new InvalidOperationException("scroll-to needs a heading, #anchor, line number, or text to find");
        if (!_webReady || _renderedPath is null) throw new InvalidOperationException("nothing rendered yet");
        var query = JsonSerializer.Serialize(arg.TrimStart('#'));
        var result = await Web.CoreWebView2.ExecuteScriptAsync($$"""
            (() => {
              const raw = {{query}};
              const q = raw.toLowerCase();
              const FLASH_MS = 2600, FADE_MS = 1300;

              if (!document.getElementById('av-scroll-style')) {
                const st = document.createElement('style');
                st.id = 'av-scroll-style';
                st.textContent =
                  '::highlight(av-scroll) { background-color: rgba(232,179,57,.32); }' +
                  '.av-flash { outline: 2px solid rgba(232,179,57,.9); outline-offset: 3px; border-radius: 3px; transition: outline-color 1.2s ease; }' +
                  '.av-flash-fade { outline-color: transparent; }';
                document.head.appendChild(st);
              }
              const flashEl = el => {
                el.classList.add('av-flash');
                setTimeout(() => el.classList.add('av-flash-fade'), FLASH_MS);
                setTimeout(() => el.classList.remove('av-flash', 'av-flash-fade'), FLASH_MS + FADE_MS);
              };
              const flashRange = r => {
                // Custom Highlight API paints the range without touching the DOM;
                // if this Chromium somehow lacks it, flash the containing element instead.
                if (window.Highlight && CSS.highlights) {
                  CSS.highlights.set('av-scroll', new Highlight(r));
                  setTimeout(() => CSS.highlights.delete('av-scroll'), FLASH_MS + FADE_MS);
                } else if (r.startContainer.parentElement) {
                  flashEl(r.startContainer.parentElement);
                }
              };

              // 1+2: anchor id, then heading substring — the original contract
              const all = [...document.querySelectorAll('h1,h2,h3,h4,h5,h6,[id]')];
              let el = all.find(e => (e.id || '').toLowerCase() === q);
              let kind = el ? 'anchor' : '';
              if (!el) {
                el = all.find(e => /^H[1-6]$/.test(e.tagName) && e.textContent.trim().toLowerCase().includes(q));
                kind = el ? 'heading' : '';
              }
              if (el) {
                el.scrollIntoView({ behavior: 'smooth', block: 'start' });
                flashEl(el);
                return kind;
              }

              // Flatten rendered text, remembering which node covers which span.
              // Block boundaries become synthetic newlines so text can't false-match
              // across blocks and "the line containing the match" means the block.
              const BLOCK = /^(ADDRESS|ARTICLE|ASIDE|BLOCKQUOTE|BR|DD|DETAILS|DIV|DL|DT|FIGCAPTION|FIGURE|FOOTER|H[1-6]|HEADER|HR|LI|MAIN|NAV|OL|P|PRE|SECTION|SUMMARY|TABLE|TD|TH|TR|UL)$/;
              const mapText = root => {
                const t = { nodes: [], full: '' };
                const walk = node => {
                  for (const c of node.childNodes) {
                    if (c.nodeType === Node.TEXT_NODE) {
                      t.nodes.push({ n: c, start: t.full.length });
                      t.full += c.nodeValue;
                    } else if (c.nodeType === Node.ELEMENT_NODE && !/^(SCRIPT|STYLE|NOSCRIPT)$/.test(c.tagName)) {
                      walk(c);
                      if (BLOCK.test(c.tagName) && !t.full.endsWith('\n')) t.full += '\n';
                    }
                  }
                };
                walk(root);
                return t;
              };

              const rangeFor = (t, s, e) => {
                const r = document.createRange();
                let startSet = false;
                for (const { n, start } of t.nodes) {
                  const end = start + n.nodeValue.length;
                  if (!startSet) {
                    if (end <= s) continue;
                    r.setStart(n, Math.max(0, s - start));
                    startSet = true;
                  }
                  if (start >= e) break;
                  r.setEnd(n, Math.min(n.nodeValue.length, Math.max(0, e - start)));
                }
                return startSet ? r : null;
              };
              const goTo = r => {
                // Not scrollIntoView: the range's parent element can be the whole
                // <code> block, which would scroll to the top of the file.
                const rect = r.getBoundingClientRect();
                window.scrollTo({ top: window.scrollY + rect.top - Math.max(60, window.innerHeight / 3), behavior: 'smooth' });
                flashRange(r);
              };

              // 3: "355" or "355-367" on a code/log artifact = source line numbers.
              // Line offsets come from the code element's own subtree — the body has
              // stray whitespace nodes around <pre> that would shift every line by one.
              const m = raw.trim().match(/^(\d+)(?:\s*-\s*(\d+))?$/);
              const codes = document.querySelectorAll('body > pre > code');
              if (m && codes.length === 1) {
                let a = +m[1], b = m[2] ? +m[2] : a;
                if (a > b) { const sw = a; a = b; b = sw; }
                const ct = mapText(codes[0]);
                const starts = [0];
                for (let i = 0; i < ct.full.length; i++) if (ct.full.charCodeAt(i) === 10) starts.push(i + 1);
                if (a <= starts.length) {
                  const s = starts[a - 1];
                  const e = b < starts.length ? starts[b] - 1 : ct.full.length;
                  const r = rangeFor(ct, s, Math.max(e, s + 1));
                  if (r) { goTo(r); return 'lines'; }
                }
              }

              // 4: plain text — first case-insensitive match, highlight its whole line/block
              const bt = mapText(document.body);
              const idx = bt.full.toLowerCase().indexOf(q);
              if (idx >= 0) {
                const ls = bt.full.lastIndexOf('\n', idx) + 1;
                let le = bt.full.indexOf('\n', idx + q.length);
                if (le < 0) le = bt.full.length;
                const r = rangeFor(bt, ls, le);
                if (r) { goTo(r); return 'text'; }
              }
              return 'not-found';
            })()
            """);
        return result.Trim('"') switch
        {
            "anchor" => $"scrolled to anchor '{arg}'",
            "heading" => $"scrolled to heading '{arg}'",
            "lines" => $"scrolled to line(s) {arg}",
            "text" => $"scrolled to first match of '{arg}'",
            _ => throw new InvalidOperationException($"nothing matching '{arg}' — no heading, anchor, line number, or text hit")
        };
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

    // ---------- Team folder ----------
    // A second, opt-in watch folder shared with colleagues — a synced folder both
    // machines already have, so there's no server, no port and no discovery here.
    // Unset by default, and while unset the app behaves exactly as it did before it.

    /// <summary>Greys the button until a team folder exists, and names it in the tooltip.</summary>
    private void UpdateTeamButton()
    {
        var set = _teamDir is not null;
        BtnTeam.Opacity = set ? 1.0 : 0.4;
        BtnTeam.ToolTip = set
            ? $"Team folder: {_teamDir}\nClick to open · right-click to change or clear"
            : "Team folder (not set) — click to choose a shared folder to watch alongside yours";

        // Nothing to share into until there's a team folder, so the button isn't there
        BtnShare.Visibility = set ? Visibility.Visible : Visibility.Collapsed;

        // Team artifacts are served from their own origin; without this a team PDF,
        // image or HTML resolves against the watch folder and 404s.
        if (_webReady)
        {
            try
            {
                Web.CoreWebView2.ClearVirtualHostNameToFolderMapping(Renderers.TeamHost);
                if (set)
                    Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                        Renderers.TeamHost, _teamDir!, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception) { /* nothing mapped yet: Clear on an absent host throws */ }
        }
    }

    private bool PickTeamDir()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a shared team folder to watch alongside your own"
        };
        if (dlg.ShowDialog(this) != true) return false;

        // Watching your own folder twice would double every tab
        if (PathEq(dlg.FolderName, _watchDir.TrimEnd('\\')))
        {
            MessageBox.Show(this,
                "That's the folder already being watched. Pick a different one to share with your team.",
                "Team folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        SetTeamDir(dlg.FolderName);
        return true;
    }

    private void SetTeamDir(string? dir)
    {
        _teamDir = dir;
        SaveSetting("teamDir", dir ?? "");
        StartTeamWatcher();
        UpdateTeamButton();
        Rescan(selectLatest: false);
    }

    private void BtnTeam_Click(object sender, RoutedEventArgs e)
    {
        if (_teamDir is null || !Directory.Exists(_teamDir)) { PickTeamDir(); return; }
        try { Process.Start(new ProcessStartInfo(_teamDir) { UseShellExecute = true }); }
        catch (Exception) { /* the folder went away; the next rescan notices */ }
    }

    private void BtnTeam_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_teamDir is null) { PickTeamDir(); return; }

        var answer = MessageBox.Show(this,
            $"Team folder is:\n{_teamDir}\n\nYes — choose a different folder\nNo — stop watching it\nCancel — leave it alone",
            "Team folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes) PickTeamDir();
        // Clearing only stops watching. Nothing on disk is touched, and the closed
        // and pinned state of those artifacts survives for if it's set again.
        else if (answer == MessageBoxResult.No) SetTeamDir(null);
    }

    /// <summary>
    /// Copies an artifact into the team folder — the outbound half of sharing, and
    /// the twin of Keep. Overwrites by name, matching drag &amp; drop: sharing a
    /// revised version of the same deliverable is the common case, and a synced
    /// folder makes its own conflicted copy if two people write at once.
    /// </summary>
    private string ShareToTeam(string sourcePath, bool move = false)
    {
        if (_teamDir is null) throw new InvalidOperationException(
            "no team folder set — click the team folder button in the toolbar to choose one");
        if (!Directory.Exists(_teamDir)) throw new InvalidOperationException(
            $"team folder is unreachable: {_teamDir}");
        if (PathEq(System.IO.Path.GetDirectoryName(sourcePath) ?? "", _teamDir.TrimEnd('\\')))
            throw new InvalidOperationException("that artifact is already in the team folder");

        var dst = System.IO.Path.Combine(_teamDir, System.IO.Path.GetFileName(sourcePath));
        if (move) File.Move(sourcePath, dst, overwrite: true);
        else File.Copy(sourcePath, dst, overwrite: true);
        return dst;
    }

    /// <summary>
    /// Left-click copies, right-click moves. Copy is the default because the watch
    /// folder is where your own history lives and sharing shouldn't empty it; move is
    /// there for the handoff case, where the artifact was only ever for them.
    /// </summary>
    private void BtnShare_Click(object sender, RoutedEventArgs e) => Share(move: false);

    private void BtnShare_RightClick(object sender, MouseButtonEventArgs e) => Share(move: true);

    private void Share(bool move)
    {
        if (_renderedPath is null || !File.Exists(_renderedPath)) return;
        if (_teamDir is null && !PickTeamDir()) return;

        try
        {
            var dst = ShareToTeam(_renderedPath, move);
            TxtDate.Text = $"{(move ? "moved" : "shared")} → {System.IO.Path.GetFileName(dst)}";
            // Only a move needs following: it leaves the old path dead, so the
            // selection has to land on the team copy. A copy leaves you on your own
            // artifact, which is where you were working.
            if (move)
            {
                Rescan(selectLatest: false);
                SelectEntry(dst);
            }
        }
        catch (Exception ex) { TxtDate.Text = $"share failed: {ex.Message}"; }
    }

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
        (PinnedStrip.SelectedItem as FileEntry)
        ?? (TabStrip.SelectedItem as FileEntry)
        ?? (TeamStrip.SelectedItem as FileEntry);

    /// <summary>
    /// Arrow-key and ◀ ▶ order: pinned row, then your own chronological row.
    /// Team artifacts are deliberately absent — stepping back through your own work
    /// shouldn't walk through a colleague's. They're reachable by clicking their tab.
    /// </summary>
    private List<FileEntry> NavOrder => _pinnedFiles.Concat(_files).ToList();

    /// <summary>Every open tab across all three rows, for "is anything showing at all".</summary>
    private List<FileEntry> AllTabs => _pinnedFiles.Concat(_files).Concat(_teamFiles).ToList();

    /// <summary>Selects an artifact in whichever row holds it, clearing the others.</summary>
    private void SelectEntry(string path)
    {
        var pinnedMatch = _pinnedFiles.FirstOrDefault(f => PathEq(f.Path, path));
        var openMatch = _files.FirstOrDefault(f => PathEq(f.Path, path));
        var teamMatch = _teamFiles.FirstOrDefault(f => PathEq(f.Path, path));
        if (pinnedMatch is null && openMatch is null && teamMatch is null) return;

        // Clear the other rows silently, so only the real selection raises an event
        _syncingSelection = true;
        if (pinnedMatch is null) PinnedStrip.SelectedItem = null;
        if (pinnedMatch is not null || teamMatch is not null) TabStrip.SelectedItem = null;
        if (pinnedMatch is not null || openMatch is not null) TeamStrip.SelectedItem = null;
        _syncingSelection = false;

        if (pinnedMatch is not null) PinnedStrip.SelectedItem = pinnedMatch;
        else if (openMatch is not null) TabStrip.SelectedItem = openMatch;
        else TeamStrip.SelectedItem = teamMatch;
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (TabStrip.SelectedItem is FileEntry entry)
        {
            _syncingSelection = true;
            PinnedStrip.SelectedItem = null;
            TeamStrip.SelectedItem = null;
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
            TeamStrip.SelectedItem = null;
            _syncingSelection = false;
            OnEntrySelected(entry, PinnedStrip);
        }
        UpdateNavButtons();
    }

    private void TeamStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (TeamStrip.SelectedItem is FileEntry entry)
        {
            _syncingSelection = true;
            TabStrip.SelectedItem = null;
            PinnedStrip.SelectedItem = null;
            _syncingSelection = false;
            OnEntrySelected(entry, TeamStrip);
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
            // Reopen into the row the artifact belongs to, not always the main one
            var row = item.Entry.Origin == ArtifactOrigin.Team ? _teamFiles : _files;
            var i = 0;
            while (i < row.Count && row[i].LastWrite <= item.Entry.LastWrite) i++;
            row.Insert(i, item.Entry);
            if (item.Entry.Origin == ArtifactOrigin.Team) TeamStrip.Visibility = Visibility.Visible;
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
            var inTeam = _teamFiles.FirstOrDefault(f => PathEq(f.Path, entry.Path));
            if (inTeam is not null) _teamFiles.Remove(inTeam);
        }

        PinnedStrip.Visibility = _pinnedFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TeamStrip.Visibility = _teamFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RebuildSidebar();

        var remaining = AllTabs;
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
            if (TeamStrip.SelectedItem is FileEntry shared) TeamStrip.ScrollIntoView(shared);
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

        // A team artifact isn't in the navigable order, so `at` is -1 while one is
        // showing. Leave the arrows live anyway — they're the way back into your own
        // stream, and disabling them would strand you on a colleague's tab.
        var offOrder = at < 0 && current is not null && order.Count > 0;
        BtnPrev.IsEnabled = at > 0 || offOrder;
        BtnNext.IsEnabled = (at >= 0 && at < order.Count - 1) || offOrder;
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
            var host = entry.Origin == ArtifactOrigin.Team ? Renderers.TeamHost : Renderers.VirtualHost;
            url = $"https://{host}/{Uri.EscapeDataString(entry.Name)}";
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
