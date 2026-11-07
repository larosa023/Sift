# Sift

A fast, modern disk space analyzer for Windows.

Sift scans a drive in seconds and shows where the space went as an interactive treemap — then goes further than a plain visualizer and finds the stuff you can actually get rid of:

- **Unplayed games** — Steam, Epic and GOG installs you haven't launched in months (uninstalled through the launcher, so your library stays tidy)
- **Duplicate files** — byte-identical copies, verified with a full XxHash128 of every candidate
- **Leftover app folders** — folders in Program Files, ProgramData and AppData that don't belong to any installed app
- **Oversized caches** — temp, browser, GPU shader, Steam, Discord, Spotify and package-manager caches
- **Stale downloads** — old installers, ISOs and archives sitting in Downloads

Nothing is deleted until you review it. Items go to the **Recycle Bin** by default, and anything too large for the Recycle Bin triggers a Windows warning instead of being silently destroyed.

## Features

- Multi-threaded scanner that skips junctions/symlinks and ignores cloud-only OneDrive placeholders
- Squarified, nested treemap with category colours; double-click to zoom, Backspace or mouse-back to go up
- Folder contents list with share bars, kept in sync with the treemap
- Review queue: everything from the treemap and every cleanup check lands here before anything is removed
- Ten themes — Light, Dark, Midnight, Graphite, Mocha, Forest, Fjord, Paper, Frost, High contrast — or match Windows, plus accent colours including your Windows accent
- Filter and sort the folder list, switch to **Largest files** for the biggest files anywhere below a folder, and export either view to CSV
- Click a file type in the legend to highlight it across the treemap
- Recent folders, drag-and-drop a folder to scan it, and folders you can skip in every scan
- Taskbar progress while scanning; the taskbar flashes when a scan finishes in the background
- Recycle Bin size, Open and Empty right in Review (recycled files keep using space until it's emptied)
- Keyboard shortcuts: Ctrl+1–4 pages, F5 rescan, Ctrl+F filter, Enter zoom, Backspace up, Delete add to review, Ctrl+E show in Explorer, Esc clear
- Built-in updater backed by GitHub Releases, with SHA-256 verification
- Single self-contained `Sift.exe` — no installer, no .NET install needed

## Download

Grab `Sift.exe` from the [latest release](https://github.com/larosa023/Sift/releases/latest) and run it. Sift checks for updates on launch (you can turn that off in Settings).

Run as administrator (button in the title bar) to scan protected system folders and clean Windows Update / Delivery Optimization caches.

## Building

Requires the .NET 8 SDK on Windows.

```powershell
dotnet run --project src/Sift
# single-file release build
dotnet publish src/Sift -c Release -r win-x64 -o out
```

## Releasing

Bump `<Version>` in `src/Sift/Sift.csproj` and push to `main`. GitHub Actions builds the exe, smoke-tests it, computes its checksum and — if that version has no release yet — publishes `v<Version>`, which the in-app updater picks up. (Pushing a `v*` tag also works.)

The release must include `Sift.exe` and `Sift.exe.sha256` (the workflow handles both).

## How the updater works

On launch Sift asks the GitHub API for the latest release of `larosa023/Sift`. If its tag is newer than the running version, a banner offers to update. Sift downloads the new exe, checks it against `Sift.exe.sha256`, renames the running exe to `Sift.exe.old`, moves the new one into place and restarts. The `.old` file is cleaned up on the next launch. If Sift lives somewhere that needs admin rights to write, the swap is done from an elevated prompt.

Settings live in `%LOCALAPPDATA%\Sift\settings.json`.

## License

MIT — see [LICENSE](LICENSE).
