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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _smoke = SmokeTest.FromArgs(e.Args);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception); a.SetObserved(); };

        Updater.CleanupAfterUpdate();

        var settings = _smoke is null ? AppSettings.Load() : new AppSettings { AutoCheckUpdates = false };
        ThemeManager.Apply(settings.Theme, settings.Accent);

        var vm = new MainViewModel(settings);
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();

        int ut = Array.IndexOf(e.Args, "--update-test");
        if (ut >= 0 && ut + 1 < e.Args.Length) RunUpdateTest(vm, e.Args[ut + 1]);
        else if (_smoke is not null) _smoke.Run(vm, window);
        else vm.OnStartup();
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
            File.AppendAllText(Path.Combine(AppSettings.Folder, "crash.log"),
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
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener(_bindingErrors));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            await Settle();
            foreach (var p in Enum.GetValues<Page>()) { vm.CurrentPage = p; await Settle(); }
            Check(true, "all pages rendered");

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
            vm.SettingsPage.Accents[1].IsSelected = true;
            await Settle();

            vm.CurrentPage = Page.Overview;
            var sw = Stopwatch.StartNew();
            await vm.Overview.ScanAsync(_folder);
            Check(vm.Overview.Root is not null && vm.Overview.Error is null, $"scan {_folder}: {vm.Overview.ScanSummary} {vm.Overview.Error}");
            Write($"     scan took {sw.ElapsedMilliseconds} ms (cold)");
            sw.Restart();
            await vm.Overview.ScanAsync(_folder);
            Write($"     rescan took {sw.ElapsedMilliseconds} ms (warm) · {vm.Overview.ScanSummary}");
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
            try { Directory.Delete(tmpDir, true); } catch { /* ignore */ }

            var (binSize, binItems) = Native.RecycleBinInfo();
            Write($"     recycle bin: {binItems} items, {Format.Bytes(binSize)} · {vm.Review.RecycleBinText}");
            Check(binItems > 0, "Recycle Bin size query");

            vm.CurrentPage = Page.Settings;
            await vm.Update.CheckAsync(manual: true);
            Write($"     updater: {vm.Update.StatusText}");
            Check(vm.Update.State is UpdateState.UpToDate or UpdateState.Available, "update check");
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
