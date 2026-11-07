namespace Sift.Core.Finders;

/// <summary>Known cache and temp locations that apps rebuild on their own. Contents are cleared, folders kept.</summary>
public sealed class CacheFinder : ICleanupFinder
{
    public string Id => "caches";
    public string Title => "Oversized caches";
    public string Description => "Temp files, browser caches, GPU shader caches, package-manager caches and crash dumps. Apps rebuild these as needed; Sift empties the folder and leaves it in place.";
    public string Glyph => "";
    public bool Grouped => false;
    public bool NeedsScan => false;

    private sealed record Spec(string Name, string Path, Risk Risk, string Note);

    public Task<IReadOnlyList<CleanupItem>> FindAsync(FinderContext ctx, CancellationToken ct) => Task.Run<IReadOnlyList<CleanupItem>>(() =>
    {
        var result = new List<CleanupItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Specs())
        {
            ct.ThrowIfCancellationRequested();
            var path = s.Path.TrimEnd('\\');
            if (!seen.Add(path) || !Directory.Exists(path)) continue;
            ctx.Status?.Report($"Measuring {s.Name}…");
            var m = FsUtil.Measure(path, ct);
            if (m.Size < ctx.Settings.CacheMinBytes) continue;
            result.Add(new CleanupItem
            {
                Path = path,
                Title = s.Name,
                Detail = s.Note,
                Size = m.Size,
                LastUsedUtc = m.NewestWriteUtc,
                LastUsedLabel = "Last written",
                Risk = s.Risk,
                Action = ItemAction.DeleteContents,
                Suggested = s.Risk == Risk.Safe,
                Source = "Cache",
                Glyph = "",
            });
        }
        return result.OrderByDescending(r => r.Size).ToList();
    }, ct);

    private static IEnumerable<Spec> Specs()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        yield return new("Your temp folder", Path.GetTempPath(), Risk.Safe, "Temporary files left by apps and installers");
        yield return new("Windows temp folder", Path.Combine(windows, "Temp"), Risk.Safe, "System temporary files (some need administrator)");
        yield return new("Windows Update downloads", Path.Combine(windows, "SoftwareDistribution", "Download"), Risk.Safe, "Already-installed update packages (needs administrator)");
        yield return new("Delivery Optimization cache", Path.Combine(windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), Risk.Safe, "Update files shared with other PCs (needs administrator)");
        yield return new("Crash dumps", Path.Combine(local, "CrashDumps"), Risk.Safe, "Memory dumps from crashed apps");
        yield return new("Error report archive", Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), Risk.Safe, "Windows Error Reporting files");
        yield return new("Error report queue", Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), Risk.Safe, "Unsent Windows Error Reporting files");
        yield return new("DirectX shader cache", Path.Combine(local, "D3DSCache"), Risk.Safe, "Rebuilt automatically by games");
        yield return new("NVIDIA DirectX cache", Path.Combine(local, @"NVIDIA\DXCache"), Risk.Safe, "Shader cache, rebuilt by games");
        yield return new("NVIDIA OpenGL cache", Path.Combine(local, @"NVIDIA\GLCache"), Risk.Safe, "Shader cache, rebuilt by games");
        yield return new("NVIDIA compute cache", Path.Combine(roaming, @"NVIDIA\ComputeCache"), Risk.Safe, "CUDA kernel cache");
        yield return new("NVIDIA shared cache", Path.Combine(programData, @"NVIDIA Corporation\NV_Cache"), Risk.Safe, "Shader cache");
        yield return new("AMD DirectX cache", Path.Combine(local, @"AMD\DxCache"), Risk.Safe, "Shader cache, rebuilt by games");
        yield return new("AMD DXC cache", Path.Combine(local, @"AMD\DxcCache"), Risk.Safe, "Shader cache, rebuilt by games");
        yield return new("AMD Vulkan cache", Path.Combine(local, @"AMD\VkCache"), Risk.Safe, "Shader cache, rebuilt by games");
        yield return new("Intel shader cache", Path.Combine(local, @"Intel\ShaderCache"), Risk.Safe, "Shader cache, rebuilt by games");

        foreach (var (name, userData) in new[]
        {
            ("Chrome", Path.Combine(local, @"Google\Chrome\User Data")),
            ("Edge", Path.Combine(local, @"Microsoft\Edge\User Data")),
            ("Brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
            ("Vivaldi", Path.Combine(local, @"Vivaldi\User Data")),
            ("Opera", Path.Combine(roaming, @"Opera Software\Opera Stable")),
        })
        {
            foreach (var prof in BrowserProfiles(userData))
            {
                var label = Path.GetFileName(prof) is "Default" or "Opera Stable" ? name : $"{name} ({Path.GetFileName(prof)})";
                yield return new($"{label} cache", Path.Combine(prof, "Cache"), Risk.Safe, "Web page cache — you stay signed in");
                yield return new($"{label} code cache", Path.Combine(prof, "Code Cache"), Risk.Safe, "Compiled script cache");
                yield return new($"{label} GPU cache", Path.Combine(prof, "GPUCache"), Risk.Safe, "Graphics cache");
                yield return new($"{label} service worker cache", Path.Combine(prof, @"Service Worker\CacheStorage"), Risk.Review, "Offline data for websites and web apps");
            }
        }
        foreach (var p in FsUtil.SafeDirectories(Path.Combine(local, @"Mozilla\Firefox\Profiles")))
            yield return new($"Firefox cache ({Path.GetFileName(p)})", Path.Combine(p, "cache2"), Risk.Safe, "Web page cache — you stay signed in");

        yield return new("Discord cache", Path.Combine(roaming, @"discord\Cache"), Risk.Safe, "Images and media from chats");
        yield return new("Discord code cache", Path.Combine(roaming, @"discord\Code Cache"), Risk.Safe, "Compiled script cache");
        yield return new("Spotify cache", Path.Combine(local, @"Spotify\Data"), Risk.Safe, "Streamed music cache (downloads are kept elsewhere)");
        yield return new("Microsoft Teams cache", Path.Combine(roaming, @"Microsoft\Teams\Cache"), Risk.Safe, "Classic Teams cache");
        yield return new("Teams (new) cache", Path.Combine(local, @"Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams"), Risk.Review, "New Teams cache — you may need to sign in again");

        var steam = GameFinder.SteamRoot();
        if (steam is not null)
        {
            yield return new("Steam web cache", Path.Combine(steam, @"appcache\httpcache"), Risk.Safe, "Store and library page cache");
            foreach (var lib in GameFinder.SteamLibraries())
                yield return new($"Steam shader cache ({Path.GetPathRoot(lib)?.TrimEnd('\\')})", Path.Combine(lib, @"steamapps\shadercache"), Risk.Safe, "Pre-compiled shaders, re-downloaded as needed");
        }

        yield return new("npm cache", Path.Combine(local, "npm-cache"), Risk.Safe, "Downloaded Node.js packages");
        yield return new("pip cache", Path.Combine(local, @"pip\Cache"), Risk.Safe, "Downloaded Python packages");
        yield return new("Yarn cache", Path.Combine(local, @"Yarn\Cache"), Risk.Safe, "Downloaded Node.js packages");
        yield return new("NuGet HTTP cache", Path.Combine(local, @"NuGet\v3-cache"), Risk.Safe, "Downloaded package metadata");
        yield return new("NuGet packages", Path.Combine(profile, @".nuget\packages"), Risk.Review, "Restored .NET packages — projects re-download them on build");
        yield return new("Gradle caches", Path.Combine(profile, @".gradle\caches"), Risk.Review, "Java/Android build cache");
        yield return new("Cargo registry cache", Path.Combine(profile, @".cargo\registry\cache"), Risk.Safe, "Downloaded Rust crates");
        yield return new("Go build cache", Path.Combine(local, "go-build"), Risk.Safe, "Go compiler cache");
    }

    private static IEnumerable<string> BrowserProfiles(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        if (File.Exists(Path.Combine(userData, "Preferences"))) { yield return userData; yield break; } // Opera layout
        foreach (var d in FsUtil.SafeDirectories(userData))
        {
            var n = Path.GetFileName(d);
            if (n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal) || n == "Guest Profile") yield return d;
        }
    }
}
