using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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

    private static readonly string[] SupportedExtensions =
    {
        ".md", ".markdown", ".html", ".htm", ".pdf", ".svg",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".txt", ".json"
    };

    private static readonly MarkdownPipeline MarkdownPipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private string _watchDir;
    private readonly string _renderDir;
    private readonly string _settingsPath;
    private readonly ObservableCollection<FileEntry> _files = new();
    private readonly ObservableCollection<SidebarItem> _allFiles = new();
    private List<FileEntry> _scanned = new();
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private readonly Dictionary<string, DateTime> _closed = new(StringComparer.OrdinalIgnoreCase);
    private string _closedStatePath = "";

    private string ResolveWatchDir(string[] args)
    {
        // Command-line arg is a per-launch override and is not persisted
        if (args.Length > 1) return System.IO.Path.GetFullPath(args[1]);

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
    private bool _webReady;
    private string? _renderedPath;
    private DateTime _renderedWrite;

    public MainWindow()
    {
        InitializeComponent();

        var appDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer");
        Directory.CreateDirectory(appDataDir);
        _settingsPath = System.IO.Path.Combine(appDataDir, "settings.json");

        _watchDir = ResolveWatchDir(Environment.GetCommandLineArgs());
        Directory.CreateDirectory(_watchDir);

        _renderDir = System.IO.Path.Combine(appDataDir, "render");
        Directory.CreateDirectory(_renderDir);

        _closedStatePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer", "closed-tabs.json");
        LoadClosed();

        TabStrip.ItemsSource = _files;
        SideList.ItemsSource = _allFiles;
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var userData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArtifactViewer");
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await Web.EnsureCoreWebView2Async(env);

        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHost, _watchDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            RenderHost, _renderDir, CoreWebView2HostResourceAccessKind.Allow);
        Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        // Let file drops over the web content bubble up to the window handlers
        Web.AllowExternalDrop = false;

        _webReady = true;

        StartWatcher();
        Rescan(selectLatest: true);
    }

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

        // Drop closed-list entries for files no longer on disk
        var onDisk = new HashSet<string>(scanned.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _closed.Keys.Where(k => !onDisk.Contains(k)).ToList())
            closedChanged |= _closed.Remove(gone);
        if (closedChanged) SaveClosed();

        _scanned = scanned;
        var open = scanned.Where(f => !_closed.ContainsKey(f.Path)).ToList();

        var selectedPath = (TabStrip.SelectedItem as FileEntry)?.Path;
        var wasAtLatest = selectLatest
            || _files.Count == 0
            || selectedPath == _files[^1].Path;

        _syncingSelection = true;
        _files.Clear();
        foreach (var f in open) _files.Add(f);
        _syncingSelection = false;
        RebuildSidebar();

        TxtEmpty.Visibility = _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Web.Visibility = _files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_files.Count == 0)
        {
            TxtTitle.Text = "Waiting for artifacts…";
            TxtDate.Text = "";
            TxtCounter.Text = "";
            _renderedPath = null;
            UpdateNavButtons();
            return;
        }

        FileEntry target;
        if (wasAtLatest)
            target = _files[^1];
        else
            target = _files.FirstOrDefault(f => f.Path == selectedPath) ?? _files[^1];

        // Setting SelectedItem triggers TabStrip_SelectionChanged → ShowEntry.
        // If the selection is unchanged but the file was rewritten, force a re-render.
        if (!ReferenceEquals(TabStrip.SelectedItem, target) || (TabStrip.SelectedItem as FileEntry)?.Path != target.Path)
            TabStrip.SelectedItem = target;
        else if (target.LastWrite != _renderedWrite)
            _ = ShowEntry(target);

        UpdateNavButtons();
    }

    // ---------- Current-doc state (lets Claude Code "see" what's on screen) ----------

    private void WriteCurrentDocState(string path)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArtifactViewer", "current.txt"), path);
        }
        catch (Exception) { /* non-fatal: "look at current doc" just won't resolve */ }
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
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] dropped) return;

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
        var entry = _files.FirstOrDefault(f =>
            string.Equals(f.Path, lastAdded, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) TabStrip.SelectedItem = entry;
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
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection) return;
        if (TabStrip.SelectedItem is FileEntry entry)
        {
            _syncingSelection = true;
            var item = _allFiles.FirstOrDefault(s => s.Entry.Path == entry.Path);
            SideList.SelectedItem = item;
            if (item is not null) SideList.ScrollIntoView(item);
            _syncingSelection = false;

            TabStrip.ScrollIntoView(entry);
            _ = ShowEntry(entry);
        }
        UpdateNavButtons();
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
        TabStrip.SelectedItem = _files.FirstOrDefault(f => f.Path == item.Entry.Path);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry entry) return;

        _closed[entry.Path] = entry.LastWrite;
        SaveClosed();
        var index = _files.IndexOf(entry);
        if (index < 0) return;
        var wasSelected = ReferenceEquals(TabStrip.SelectedItem, entry);
        _files.RemoveAt(index);
        RebuildSidebar();

        if (_files.Count == 0)
        {
            TxtEmpty.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Collapsed;
            TxtTitle.Text = "Waiting for artifacts…";
            TxtDate.Text = "";
            Title = "Artifact Viewer";
            _renderedPath = null;
        }
        else if (wasSelected)
        {
            TabStrip.SelectedIndex = Math.Min(index, _files.Count - 1);
        }
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
        if (_files.Count == 0) return;
        var idx = Math.Clamp(TabStrip.SelectedIndex + delta, 0, _files.Count - 1);
        TabStrip.SelectedIndex = idx;
    }

    private void UpdateNavButtons()
    {
        BtnPrev.IsEnabled = TabStrip.SelectedIndex > 0;
        BtnNext.IsEnabled = TabStrip.SelectedIndex >= 0 && TabStrip.SelectedIndex < _files.Count - 1;
        TxtCounter.Text = _files.Count == 0 ? "" : $"{TabStrip.SelectedIndex + 1} / {_files.Count}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Left) { Step(-1); e.Handled = true; }
            else if (key == Key.Right) { Step(+1); e.Handled = true; }
        }
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

        TxtTitle.Text = entry.Name;
        TxtDate.Text = entry.LastWrite.ToString("MMM d, h:mm:ss tt");
        Title = $"{entry.Name} — Artifact Viewer";
        _renderedPath = entry.Path;
        _renderedWrite = entry.LastWrite;
        WriteCurrentDocState(entry.Path);

        var ext = System.IO.Path.GetExtension(entry.Path).ToLowerInvariant();
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
        else
        {
            // Served through the virtual host so Chromium handles it natively
            // (PDF viewer, images, HTML with relative asset paths, etc.)
            Web.CoreWebView2.Navigate($"https://{VirtualHost}/{Uri.EscapeDataString(entry.Name)}");
        }
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

    private static string BuildMarkdownHtml(string markdown)
    {
        var body = Markdown.ToHtml(markdown, MarkdownPipeline);
        return $$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<base href="https://{{VirtualHost}}/">
<link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css">
<style>
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
</style>
</head>
<body>
{{body}}
<script src="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js"></script>
<script type="module">
  document.querySelectorAll('pre code:not(.language-mermaid)').forEach(el => hljs.highlightElement(el));
  // Turn ```mermaid fences into rendered diagrams
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
      const { default: mermaid } = await import('https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs');
      mermaid.initialize({ startOnLoad: false, theme: 'dark' });
      await mermaid.run();
    }
  } catch (err) {
    const banner = document.createElement('div');
    banner.style.cssText = 'background:#5a1d1d;color:#ffb3b3;padding:10px 16px;border-radius:8px;margin:12px 0;font-family:monospace;white-space:pre-wrap;';
    banner.textContent = 'mermaid failed: ' + (err && err.message ? err.message : err);
    document.body.prepend(banner);
  }
</script>
</body>
</html>
""";
    }
}
