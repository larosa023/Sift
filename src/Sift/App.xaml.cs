using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Sift.Core;
using Sift.Themes;
using Sift.ViewModels;

namespace Sift;

public partial class App : Application
{
    private SmokeTest? _smoke;
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        _smoke = SmokeTest.FromArgs(args);
        bool updateTest = Array.IndexOf(args, "--update-test") >= 0;
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        // ---- Hangar's conventions: --quit closes a running Sift gracefully, --uninstall also removes Sift's data.
        if (Has("--quit") || Has("--uninstall"))
        {
            int code = QuitRunning();
            if (Has("--uninstall"))
            {
                ExplorerMenu.Remove();
                try { if (Directory.Exists(AppSettings.Folder)) Directory.Delete(AppSettings.Folder, recursive: true); } catch { /* in use */ }
                code = 0;
            }
            Shutdown(code);
            return;
        }

        // ---- A folder from the command line or Explorer's "Analyze with Sift".
        string? path = null;
        if (_smoke is null && !updateTest)
        {
            path = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.Trim().Trim('"'))
                       .FirstOrDefault(a => a.Length > 0 && Directory.Exists(a));
            if (path is { Length: 2 } && path[1] == ':') path += "\\"; // "D:" from a drive's menu

            // One Sift at a time: hand the request to the running copy. After an update the old copy is still
            // closing, so wait for it instead.
            _instance = SingleInstance.TryClaim(Has("--updated") ? TimeSpan.FromSeconds(15) : TimeSpan.Zero);
            if (_instance is null)
            {
                var reply = SingleInstance.Send(path is null ? new SingleInstance.Request("show") : new SingleInstance.Request("scan", path));
                if (reply is not null) { Shutdown(0); return; }
                // The running copy didn't answer (stuck?): carry on as a second window rather than doing nothing.
            }
        }

        Updater.CleanupAfterUpdate();

        var settings = _smoke is null ? AppSettings.Load() : new AppSettings { AutoCheckUpdates = false, ThemeSchema = 2 };
        ThemeManager.Apply(settings.Theme, settings.Accent);
        if (_smoke is null && settings.ExplorerMenu) { try { ExplorerMenu.Install(); } catch { /* ignore */ } } // keeps the path current

        var vm = new MainViewModel(settings);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        _instance?.Serve(req => Task.FromResult(Dispatcher.Invoke(() => Handle(vm, window, req))));

        int ut = Array.IndexOf(args, "--update-test");
        if (ut >= 0 && ut + 1 < args.Length) RunUpdateTest(vm, args[ut + 1]);
        else if (_smoke is not null) _smoke.Run(vm, window);
        else vm.OnStartup(path);
    }

    /// <summary>A request from a later launch of Sift.</summary>
    private SingleInstance.Reply Handle(MainViewModel vm, Window window, SingleInstance.Request req)
    {
        switch (req.Cmd)
        {
            case "quit":
                if (vm.Review.IsWorking) return new SingleInstance.Reply(false, "busy");
                Dispatcher.InvokeAsync(() => { window.Close(); Shutdown(0); });
                return new SingleInstance.Reply(true);

            case "scan" when req.Path is not null:
                BringToFront(window);
                if (vm.Overview.IsScanning) return new SingleInstance.Reply(false, "busy");
                vm.CurrentPage = Page.Overview;
                _ = vm.Overview.ScanAsync(req.Path);
                return new SingleInstance.Reply(true);

            default:
                BringToFront(window);
                return new SingleInstance.Reply(true);
        }
    }

    private static void BringToFront(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
        w.Topmost = true;  // nudge past focus-stealing prevention, then let go
        w.Topmost = false;
        w.Focus();
    }

    /// <summary>Asks a running Sift to close; waits for it. 0 = closed or not running, 1 = it refused (busy).</summary>
    private static int QuitRunning()
    {
        var reply = SingleInstance.Send(new SingleInstance.Request("quit"));
        if (reply is null) return 0;
        if (!reply.Ok) return 1;
        try
        {
            using var p = Process.GetProcessById(reply.Pid);
            p.WaitForExit(15000);
        }
        catch { /* already gone */ }
        return 0;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>CI: an old build checks for, downloads, verifies and installs the latest release.</summary>
    private static async void RunUpdateTest(MainViewModel vm, string log)
    {
        void W(string l) { try { File.AppendAllText(log, l + Environment.NewLine); } catch { } }
        W($"running {Updater.CurrentVersionText} from {Environment.ProcessPath}");
        await vm.Update.CheckAsync(manual: true);
        W($"check: {vm.Update.State} {vm.Update.StatusText}");
        if (vm.Update.State != UpdateState.Available) { Current.Shutdown(1); return; }
        await vm.Update.InstallAsync(); // on success this relaunches the new exe and shuts down
        W($"install: {vm.Update.State} {vm.Update.StatusText}");
        if (vm.Update.State == UpdateState.Error) Current.Shutdown(1);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        if (_smoke is not null)
        {
            _smoke.Fail(e.Exception);
            return;
        }
        MessageBox.Show($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {Path.Combine(AppSettings.Folder, "crash.log")}.",
            "Sift", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Log(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            var log = Path.Combine(AppSettings.Folder, "crash.log");
            // Keep the log small: start over once it passes 1 MB.
            if (File.Exists(log) && new FileInfo(log).Length > 1 << 20) File.Delete(log);
            File.AppendAllText(log,
                $"[{DateTime.Now:u}] Sift {Updater.CurrentVersionText}\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }
}

/// <summary>
/// CI smoke test: <c>Sift.exe --smoke &lt;folder&gt; &lt;logfile&gt;</c>. Drives every page, a scan, all cleanup
/// checks, a Recycle Bin delete and an update check, then exits 0 (pass) or 1 (fail).
/// </summary>
internal sealed class SmokeTest
{
    private readonly string _folder;
    private readonly string _log;
    private readonly List<string> _bindingErrors = new();
    private bool _failed;

    private SmokeTest(string folder, string log) { _folder = folder; _log = log; }

    public static SmokeTest? FromArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--smoke");
        if (i < 0 || i + 2 >= args.Length) return null;
        return new SmokeTest(args[i + 1], args[i + 2]);
    }

    private void Write(string line)
    {
        try { File.AppendAllText(_log, line + Environment.NewLine); } catch { /* ignore */ }
    }

    public void Fail(Exception ex)
    {
        if (_failed) return;
        _failed = true;
        Write("FAIL: " + ex);
        Application.Current.Shutdown(1);
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(150);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private int _shot;
    private Window? _window;

    /// <summary>Saves a PNG of the window so CI can show what each page looks like.</summary>
    private async Task Snap(string name)
    {
        if (_window?.Content is not FrameworkElement root) return;
        await Settle();
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(_log) ?? Path.GetTempPath(), "shots");
            Directory.CreateDirectory(dir);
            int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
            if (w <= 0 || h <= 0) return;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            var bg = new System.Windows.Media.DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(_window.Background, null, new Rect(0, 0, w, h));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, $"{++_shot:00}-{name}.png"));
            enc.Save(fs);
        }
        catch (Exception ex) { Write("     snap failed: " + ex.Message); }
    }

    /// <summary>Scan history, comparisons, the new finders and searching, on a small folder of known files.</summary>
    private async Task FixtureTests(MainViewModel vm)
    {
        var ov = vm.Overview;
        var fx = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "SiftSmoke");
        if (Directory.Exists(fx)) Directory.Delete(fx, true);
        Directory.CreateDirectory(Path.Combine(fx, "a"));
        Directory.CreateDirectory(Path.Combine(fx, "b"));
        Directory.CreateDirectory(Path.Combine(fx, "c"));
        var rnd = new Random(42);
        byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        var one = Bytes(1_500_000);
        File.WriteAllBytes(Path.Combine(fx, "a", "one.bin"), one);
        File.WriteAllBytes(Path.Combine(fx, "a", "two.bin"), one); // duplicate of one.bin
        File.WriteAllBytes(Path.Combine(fx, "b", "big.bin"), Bytes(3_000_000));
        var old = Path.Combine(fx, "c", "old-video.mkv");
        File.WriteAllBytes(old, Bytes(2_000_000));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddYears(-2));
        File.WriteAllText(Path.Combine(fx, "c", "notes.txt"), "hello");

        await ov.ScanAsync(fx);
        Check(ov.Root?.FileCount == 5, $"fixture scan ({ov.ScanSummary})");

        var ctx = new Core.Finders.FinderContext { Settings = vm.Settings, Scan = ov.Root };
        var dups = await new Core.Finders.DuplicateFinder().FindAsync(ctx, CancellationToken.None);
        Check(dups.Count == 2 && dups.Count(d => d.Suggested) == 1, $"duplicates found ({dups.Count})");
        dups = await new Core.Finders.DuplicateFinder().FindAsync(ctx, CancellationToken.None);
        Check(dups.Count == 2 && File.Exists(Path.Combine(AppSettings.Folder, "hashes.bin")), "duplicates again (hash cache)");

        vm.Settings.LargeOldMinBytes = 1 << 20;
        var large = await new Core.Finders.LargeOldFilesFinder().FindAsync(ctx, CancellationToken.None);
        Check(large.Count == 1 && large[0].Title == "old-video.mkv", $"large old files ({string.Join(", ", large.Select(l => l.Title))})");
        var extras = await new Core.Finders.WindowsExtrasFinder().FindAsync(ctx, CancellationToken.None);
        Write($"     windows extras: {string.Join(", ", extras.Select(x => $"{x.Title} {x.SizeText}"))}");

        // Change things, rescan, and compare.
        File.WriteAllBytes(Path.Combine(fx, "a", "new.bin"), Bytes(2_000_000));
        File.Delete(Path.Combine(fx, "b", "big.bin"));
        await ov.ScanAsync(fx);
        for (int i = 0; i < 50 && !ov.HasCompare; i++) await Task.Delay(100);
        Check(ov.HasCompare, "previous scan loaded for comparison");
        ov.Mode = ListMode.Changes;
        await Settle();
        foreach (var r in ov.Rows) Write($"     change: {r.Name} {r.DeltaText} ({r.Location})");
        Check(ov.Rows.Any(r => r.Delta >= 1_900_000 && (r.Name == "new.bin" || r.Name == "a")) &&
              ov.Rows.Any(r => r.Kind == ChangeKind.Removed && r.Name == "big.bin"), "changes since the previous scan");
        await Snap("changes");
        ov.Mode = ListMode.Contents;

        // File types, wildcard search through subfolders.
        ov.Mode = ListMode.Types;
        await Settle();
        Check(ov.Rows.Any(r => r.TypeKey == ".bin") && ov.Rows.All(r => r.IsTypeRow), "file types view");
        ov.ShowType(".bin");
        await Settle();
        Check(ov.Mode == ListMode.LargestFiles && ov.Rows.Count == 3 && ov.Rows.All(r => r.Name.EndsWith(".bin")), "type → largest files of that type");
        ov.FilterText = ""; ov.Mode = ListMode.Contents;
        ov.SearchSubfolders = true;
        ov.FilterText = "*.mkv";
        await Task.Delay(400); await Settle();
        Check(ov.Rows.Count == 1 && ov.Rows[0].Name == "old-video.mkv" && ov.Rows[0].HasLocation, "wildcard search through subfolders");
        ov.FilterText = "";
        await Task.Delay(400);

        ov.ColorMode = Controls.TreemapColorMode.Age;
        for (int i = 0; i < 20 && ov.LegendTitle != "Last modified"; i++) await Task.Delay(50);
        await Task.Delay(300); await Settle();
        Check(ov.Categories.Any(c => c.Name == "1–3 years"), $"colour by age legend ({string.Join(", ", ov.Categories.Select(c => c.Name))})");
        await Snap("overview-age");
        ov.ColorMode = Controls.TreemapColorMode.Type;

        // Reopening shows the saved scan straight away.
        await Task.Delay(1500);
        ov.NewScanCommand.Execute(null);
        await ov.RestoreLastAsync();
        Check(ov.IsFromCache && ov.Root?.FileCount == 5 && ov.HasCompare, $"saved scan reopened ({ov.ScanSummary})");
        await Snap("overview-saved");

        // Undo a cleanup: recycle a file, then put it back.
        var victim = Path.Combine(fx, "c", "notes.txt");
        vm.Review.Add(new ReviewItem { Path = victim, Title = "notes.txt", Size = 5, Source = "Smoke" });
        vm.Review.ConfirmCommand.Execute(null);
        for (int i = 0; i < 100 && (vm.Review.IsWorking || !vm.Review.CanUndo); i++) await Task.Delay(100);
        Check(!File.Exists(victim) && vm.Review.CanUndo, "recycled (undo available)");
        await vm.Review.UndoAsync();
        Write($"     undo: {vm.Review.ResultText}");
        Check(File.Exists(victim), "undo restored the file from the Recycle Bin");
        vm.Review.ClearCommand.Execute(null);

        try { Directory.Delete(fx, true); } catch { /* ignore */ }
    }

    private void Check(bool ok, string what)
    {
        Write((ok ? "ok   " : "FAIL ") + what);
        if (!ok) throw new InvalidOperationException("Check failed: " + what);
    }

    public async void Run(MainViewModel vm, Window window)
    {
        try
        {
            File.WriteAllText(_log, $"Sift {Updater.CurrentVersionText} smoke test\n");
            _window = window;
            window.WindowState = WindowState.Normal;
            window.Width = 1440; window.Height = 900;
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener(_bindingErrors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            await Settle();
            foreach (var p in Enum.GetValues<Page>()) { vm.CurrentPage = p; await Settle(); }
            Check(true, "all pages rendered");
            vm.CurrentPage = Page.Overview;
            await Snap("start");

            vm.CurrentPage = Page.Settings;
            foreach (var t in vm.SettingsPage.Themes)
            {
                vm.SettingsPage.SelectTheme(t.Id);
                await Settle();
                Check(ThemeManager.CurrentThemeId == t.Id, $"theme {t.Id}");
            }
            foreach (var a in vm.SettingsPage.Accents) { a.IsSelected = true; await Settle(); }
            Check(true, "all accents incl. Windows accent");
            vm.SettingsPage.SelectTheme("dark");
            vm.SettingsPage.Accents[0].IsSelected = true; // theme's own
            await Settle();
            await Snap("settings-themes");

            // ---- Custom theme file in Hangar's format.
            Directory.CreateDirectory(ThemeCatalog.SiftThemesFolder);
            var themeFile = Path.Combine(ThemeCatalog.SiftThemesFolder, "smoke.json");
            File.WriteAllText(themeFile, """
                { // comments are allowed, like Hangar
                  "schema": 1, "name": "Smoke Test", "mode": "dark",
                  "colors": { "background": "#102030", "surface": "#203040", "text": "#F0F0F0", "accent": "#FF00AA" }
                }
                """);
            vm.SettingsPage.ReloadThemesCommand.Execute(null);
            Check(vm.SettingsPage.Themes.Any(t => t.Id == "custom:smoke test"), "custom theme file loaded");
            vm.SettingsPage.SelectTheme("custom:smoke test");
            await Settle();
            Check(((System.Windows.Media.SolidColorBrush)Application.Current.Resources.MergedDictionaries[0]["WindowBg"]).Color.ToString() == "#FF102030", "custom theme applied");
            Check(((System.Windows.Media.SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color.ToString() == "#FFFF00AA", "custom theme's own accent");
            File.Delete(themeFile);

            // ---- Following Hangar live (a stand-in Hangar settings file; CI has no Hangar).
            bool hadHangar = Directory.Exists(ThemeCatalog.HangarFolder);
            if (!hadHangar)
            {
                Directory.CreateDirectory(ThemeCatalog.HangarFolder);
                var hs = Path.Combine(ThemeCatalog.HangarFolder, "settings.json");
                File.WriteAllText(hs, "{ \"theme\": \"ocean\", \"accent\": \"#FF5D5D\" }");
                vm.SettingsPage.ReloadThemesCommand.Execute(null);
                vm.SettingsPage.SelectTheme(ThemeCatalog.HangarId);
                await Settle();
                Check(ThemeManager.Current.Id == "ocean", $"follows Hangar's theme ({ThemeManager.Current.Id})");
                File.WriteAllText(hs, "{ \"theme\": \"rose\", \"accent\": \"theme\" }");
                for (int i = 0; i < 30 && ThemeManager.Current.Id != "rose"; i++) await Task.Delay(100);
                Check(ThemeManager.Current.Id == "rose", "picks up Hangar theme changes live");
                vm.CurrentPage = Page.Settings;
                await Snap("settings-hangar-rose");
                vm.SettingsPage.SelectTheme("dark");
                await Settle();
                Directory.Delete(ThemeCatalog.HangarFolder, true);
                vm.SettingsPage.ReloadThemesCommand.Execute(null);
            }

            // ---- Explorer right-click menu.
            vm.SettingsPage.ExplorerMenuEnabled = true;
            Check(ExplorerMenu.IsInstalled(), "Explorer menu added");
            vm.SettingsPage.ExplorerMenuEnabled = false;
            Check(!ExplorerMenu.IsInstalled(), "Explorer menu removed");

            await FixtureTests(vm);

            vm.CurrentPage = Page.Overview;
            var sw = Stopwatch.StartNew();
            await vm.Overview.ScanAsync(_folder);
            Check(vm.Overview.Root is not null && vm.Overview.Error is null, $"scan {_folder}: {vm.Overview.ScanSummary} {vm.Overview.Error}");
            Write($"     scan took {sw.ElapsedMilliseconds} ms (cold)");
            sw.Restart();
            await vm.Overview.ScanAsync(_folder);
            Write($"     rescan took {sw.ElapsedMilliseconds} ms (warm) · {vm.Overview.ScanSummary}");
            await Snap("overview");

            // ---- Fast (MFT) scanner vs the folder-by-folder scanner on the same folder.
            Write($"     elevated: {Native.IsElevated()} · fast scan possible: {MftScanner.CanScan(_folder, out var whyNot)} {whyNot}");
            if (MftScanner.CanScan(_folder, out _))
            {
                var slow = await new DiskScanner().ScanAsync(_folder, CancellationToken.None);
                sw.Restart();
                var fast = await new MftScanner().ScanAsync(_folder, CancellationToken.None);
                long fastMs = sw.ElapsedMilliseconds;
                Write($"     normal: {slow.FileCount:N0} files {slow.FolderCount:N0} folders {slow.Size:N0} bytes");
                Write($"     fast:   {fast.FileCount:N0} files {fast.FolderCount:N0} folders {fast.Size:N0} bytes ({fastMs} ms incl. reading the whole C: table)");
                if (fast.FileCount != slow.FileCount || fast.Size != slow.Size)
                {
                    var slowKids = slow.Children!.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
                    foreach (var c in fast.Children!)
                    {
                        slowKids.TryGetValue(c.Name, out var o);
                        if (o is null || o.Size != c.Size || o.FileCount != c.FileCount)
                            Write($"     diff {c.Name}: fast {c.FileCount}/{c.Size} normal {o?.FileCount}/{o?.Size}");
                    }
                }
                double sizeDiff = Math.Abs(fast.Size - slow.Size) / (double)Math.Max(1, slow.Size);
                Check(fast.FileCount == slow.FileCount && fast.FolderCount == slow.FolderCount && sizeDiff < 0.001,
                      "fast scan matches normal scan");
                var anyFile = slow.EnumerateFiles().Skip(slow.FileCount > 10 ? 7 : 0).First();
                var same = fast.Find(anyFile.FullPath);
                Check(same is not null && same.Size == anyFile.Size && Math.Abs(same.LastWriteTicks - anyFile.LastWriteTicks) < TimeSpan.TicksPerSecond * 2,
                      $"fast scan file details match ({anyFile.Name})");

                sw.Restart();
                var drive = new MftScanner();
                var whole = await drive.ScanAsync("C:\\", CancellationToken.None);
                Write($"     fast scan of all of C:\\ — {whole.FileCount:N0} files, {whole.FolderCount:N0} folders, {Format.Bytes(whole.Size)} in {sw.ElapsedMilliseconds:N0} ms");
                var di = new DriveInfo("C");
                Write($"     drive reports {Format.Bytes(di.TotalSize - di.TotalFreeSpace)} used");
                Check(whole.FileCount > 100_000, "fast whole-drive scan");
            }
            await Settle();
            var firstDir = vm.Overview.Root!.Children?.FirstOrDefault(c => c.IsDirectory);
            if (firstDir is not null) { vm.Overview.NavigateTo(firstDir); await Settle(); vm.Overview.UpCommand.Execute(null); await Settle(); }
            Check(true, "treemap navigation");

            var ov = vm.Overview;
            int all = ov.Rows.Count;
            ov.SortBy(Sift.ViewModels.SortKey.Name); await Settle();
            Check(ov.Rows.Count == all && ov.NameHeader.Contains('↑'), "sort by name");
            ov.SortBy(Sift.ViewModels.SortKey.Size); await Settle();
            ov.Mode = Sift.ViewModels.ListMode.LargestFiles; await Settle();
            Check(ov.Rows.Count > 0 && ov.Rows.All(r => !r.IsDirectory), $"largest files mode ({ov.Rows.Count} rows, biggest {ov.Rows.FirstOrDefault()?.SizeText})");
            ov.FilterText = ".dll";
            await Task.Delay(400); await Settle();
            Check(ov.Rows.All(r => r.Name.Contains(".dll", StringComparison.OrdinalIgnoreCase)), $"filter ({ov.Rows.Count} matches)");
            var csv = Path.Combine(Path.GetTempPath(), "sift-smoke.csv");
            ov.ExportTo(csv);
            Check(File.Exists(csv) && File.ReadAllLines(csv).Length == ov.Rows.Count + 1, "CSV export");
            ov.FilterText = ""; ov.Mode = Sift.ViewModels.ListMode.Contents;
            await Task.Delay(400); await Settle();
            if (ov.Categories.Count > 0)
            {
                ov.HighlightCommand.Execute(ov.Categories[0]); await Settle();
                Check(ov.HasHighlight && ov.Categories.Skip(1).All(c => c.IsDimmed), "legend highlight");
                Check(ov.ClearOne() && !ov.HasHighlight, "Esc clears highlight");
            }

            // Exclusions: skip the first sub-folder and rescan.
            var skip = ov.Root!.Children!.FirstOrDefault(c => c.IsDirectory && c.FileCount > 0);
            if (skip is not null)
            {
                var skipPath = skip.FullPath;
                long before = ov.Root.FileCount;
                vm.SettingsPage.AddExclusion(skipPath);
                await ov.ScanAsync(_folder);
                Check(ov.Root!.Find(skipPath) is null && ov.Root.FileCount < before, $"exclusion skips {skipPath}");
                vm.SettingsPage.RemoveExclusionCommand.Execute(skipPath);
                await ov.ScanAsync(_folder);
            }
            Check(vm.Settings.RecentScans.Contains(_folder), "recent scans recorded");

            vm.CurrentPage = Page.Cleanup;
            await vm.Cleanup.RunAllAsync();
            foreach (var f in vm.Cleanup.Finders)
            {
                Write($"     {f.Title}: {f.StatusText}{(f.Error is null ? "" : " — " + f.Error)}");
                Check(f.Error is null, $"finder {f.Id} ran");
            }
            foreach (var f in vm.Cleanup.Finders) { vm.Cleanup.Selected = f; await Settle(); }
            vm.Cleanup.Selected = vm.Cleanup.Finders.OrderByDescending(f => f.Count).First();
            await Snap("cleanup");

            // Review + Recycle Bin round trip on a throwaway file.
            var tmpDir = Path.Combine(Path.GetTempPath(), "sift-smoke-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, "delete-me.bin");
            File.WriteAllBytes(tmp, new byte[4096]);
            vm.Review.Add(new ReviewItem { Path = tmp, Title = "delete-me.bin", Size = 4096, Source = "Smoke" });
            vm.CurrentPage = Page.Review;
            await Settle();
            vm.Review.DeleteCommand.Execute(null);
            await Settle();
            Check(vm.Review.IsConfirming, "confirmation shown before delete");
            vm.Review.ConfirmCommand.Execute(null);
            for (int i = 0; i < 100 && (vm.Review.IsWorking || vm.Review.Items.Any(x => x.Status == ReviewStatus.Pending)); i++) await Task.Delay(100);
            await Settle();
            var item = vm.Review.Items.FirstOrDefault();
            Write($"     review result: {vm.Review.ResultText} / {item?.StatusText}");
            Check(!File.Exists(tmp), "file moved to Recycle Bin");
            await Snap("review");
            try { Directory.Delete(tmpDir, true); } catch { /* ignore */ }

            var (binSize, binItems) = Native.RecycleBinInfo();
            Write($"     recycle bin: {binItems} items, {Format.Bytes(binSize)} · {vm.Review.RecycleBinText}");
            Check(binItems > 0, "Recycle Bin size query");

            vm.CurrentPage = Page.Settings;
            await vm.Update.CheckAsync(manual: true);
            Write($"     updater: {vm.Update.StatusText}");
            Check(vm.Update.State is UpdateState.UpToDate or UpdateState.Available, "update check");
            await Snap("settings");
            vm.SettingsPage.SelectTheme("light"); vm.CurrentPage = Page.Overview;
            await Snap("overview-light");
            vm.SettingsPage.SelectTheme("dark");
            await Settle();

            foreach (var b in _bindingErrors.Distinct().Take(40)) Write("     binding: " + b);
            Write(_bindingErrors.Count == 0 ? "ok   no binding errors" : $"WARN {_bindingErrors.Count} binding error(s)");
            Write("SMOKE OK");
            window.Close();
            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            foreach (var b in _bindingErrors.Distinct().Take(40)) Write("     binding: " + b);
            Fail(ex);
        }
    }

    private sealed class BindingListener : TraceListener
    {
        private readonly List<string> _sink;
        public BindingListener(List<string> sink) => _sink = sink;
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) lock (_sink) _sink.Add(message); }
    }
}
