using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sift.Core;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string ExeUrl, string? ChecksumUrl, string PageUrl, long Size);

/// <summary>
/// Self-updater backed by GitHub Releases on larosa023/Sift.
/// A release is expected to carry Sift.exe and Sift.exe.sha256 (the CI workflow produces both).
/// Windows lets a running exe be renamed, so the swap is: rename current → .old, move new in, relaunch.
/// </summary>
public sealed class Updater
{
    public const string Repo = "larosa023/Sift";
    public static string RepoUrl => $"https://github.com/{Repo}";

    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient NoRedirect = CreateClient(followRedirects: false);

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return Normalize(v);
        }
    }

    public static string CurrentVersionText => CurrentVersion.ToString(3);

    private static HttpClient CreateClient(bool followRedirects = true)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects }) { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Sift-Updater/{CurrentVersionText}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    public static bool TryParseTag(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t, out var v) && v is not null) { version = Normalize(v); return true; }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Returns the latest release if it is newer than the running build, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return await CheckViaRedirectAsync(ct).ConfigureAwait(false);
        }
        using var _ = resp;
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        // The anonymous API allows 60 requests an hour per IP; when it says no, use the release page redirect.
        if (!resp.IsSuccessStatusCode) return await CheckViaRedirectAsync(ct).ConfigureAwait(false);
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseTag(tag, out var latest) || latest <= CurrentVersion) return null;

        string? exe = null, sum = null;
        long size = 0;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString();
            if (name.Equals("Sift.exe", StringComparison.OrdinalIgnoreCase))
            {
                exe = url;
                size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            }
            else if (name.Equals("Sift.exe.sha256", StringComparison.OrdinalIgnoreCase)) sum = url;
        }
        if (exe is null) return null;

        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? RepoUrl : RepoUrl;
        return new UpdateInfo(latest, tag, notes, exe, sum, page, size);
    }

    /// <summary>
    /// Fallback that doesn't use the GitHub API: /releases/latest redirects to /releases/tag/vX.Y.Z,
    /// and assets live at predictable /releases/download/vX.Y.Z/... URLs.
    /// </summary>
    private static async Task<UpdateInfo?> CheckViaRedirectAsync(CancellationToken ct)
    {
        using var resp = await NoRedirect.GetAsync($"{RepoUrl}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var location = resp.Headers.Location?.ToString();
        if (location is null)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            throw new HttpRequestException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        var tag = location.TrimEnd('/').Split('/').Last();
        if (!tag.StartsWith('v') || !TryParseTag(tag, out var latest) || latest <= CurrentVersion) return null;
        var baseUrl = $"{RepoUrl}/releases/download/{tag}";
        return new UpdateInfo(latest, tag, "", $"{baseUrl}/Sift.exe", $"{baseUrl}/Sift.exe.sha256", $"{RepoUrl}/releases/tag/{tag}", 0);
    }

    /// <summary>Downloads and verifies the new exe. Returns the path of the verified file.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(AppSettings.Folder, "update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"Sift-{info.Version.ToString(3)}.exe");

        using (var resp = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (info.ChecksumUrl is not null)
        {
            var expected = (await Http.GetStringAsync(info.ChecksumUrl, ct).ConfigureAwait(false))
                .Trim().Split(' ', '\t', '\r', '\n')[0].ToLowerInvariant();
            string actual;
            await using (var fs = File.OpenRead(target))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                TryDelete(target);
                throw new InvalidOperationException("The downloaded update failed its checksum and was discarded.");
            }
        }
        return target;
    }

    /// <summary>Swaps the running exe for the downloaded one and relaunches. Caller should then shut down.</summary>
    public static void ApplyAndRestart(string newExe)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate Sift.exe");
        var old = current + ".old";
        try
        {
            TryDelete(old);
            File.Move(current, old);
            try
            {
                File.Move(newExe, current);
            }
            catch
            {
                File.Move(old, current); // roll back
                throw;
            }
            Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = false });
        }
        catch (UnauthorizedAccessException)
        {
            // Installed somewhere protected (e.g. Program Files): do the swap from an elevated shell.
            var cmd = $"/c ping 127.0.0.1 -n 3 >nul & move /y \"{current}\" \"{old}\" & move /y \"{newExe}\" \"{current}\" & start \"\" \"{current}\" --updated";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
    }

    /// <summary>Called at startup: removes the previous exe left behind by an update.</summary>
    public static void CleanupAfterUpdate()
    {
        var current = Environment.ProcessPath;
        if (current is null) return;
        var old = current + ".old";
        if (!File.Exists(old)) return;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                TryDelete(old);
                await Task.Delay(500).ConfigureAwait(false);
            }
            try
            {
                var dir = Path.Combine(AppSettings.Folder, "update");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { /* ignore */ }
        });
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* ignore */ }
    }
}
