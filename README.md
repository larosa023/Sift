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

**Scanning**
- **Fast scan:** run as administrator and Sift reads the NTFS file table directly — a whole drive with a million files in seconds, including folders Windows won't normally list. Falls back to a normal multi-threaded scan otherwise
- Skips junctions/symlinks and ignores cloud-only OneDrive placeholders
- Scans are saved: Sift opens with your last results instantly, and shows **what grew or shrank** since the scan before
- Folders you can skip in every scan, recent folders, drag-and-drop, and "Analyze with Sift" in File Explorer's right-click menu

**Exploring**
- Squarified, nested treemap coloured by **file type or by age**; click a legend entry to highlight it
- Folder list with **Contents**, **Largest files**, **Types** (space per extension) and **Changes** views
- Filter or search every subfolder, with wildcards (`*.mkv`); export any list to CSV

**Cleaning up** — nothing is deleted until you review it
- Caches (browsers, GPU shaders, Steam, Discord, Spotify, package managers, NVIDIA/AMD driver installers)
- Windows extras: hibernation file, previous Windows installation, upgrade leftovers, crash dumps
- Large old files, unplayed Steam/Epic/GOG games, duplicate files, leftover app folders, stale downloads
- Items go to the **Recycle Bin** by default, and **Undo** (Ctrl+Z) puts the last cleanup back

**The rest**
- The same themes as Hangar and the rest of the suite, **Match Hangar** (live), custom theme files, any accent colour
- Built-in updater backed by GitHub Releases, with SHA-256 verification
- One window at a time; `--quit` and `--uninstall` for Hangar
- Single self-contained `Sift.exe` — no installer, no .NET install needed

## Download

Grab `Sift.exe` from the [latest release](https://github.com/larosa023/Sift/releases/latest) and run it. Sift checks for updates on launch (you can turn that off in Settings).

Run as administrator (button in the title bar) for fast scans, to see protected system folders, and to clean Windows Update / Delivery Optimization caches.

Custom themes are JSON files in Hangar's theme format (see Hangar's `docs/THEMES.md`); put them in `%LOCALAPPDATA%\Sift\themes` or Hangar's own themes folder.

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

Settings live in `%LOCALAPPDATA%\Sift\settings.json`, saved scans in `%LOCALAPPDATA%\Sift\scans`.

## Releasing notes

Release notes come from `CHANGELOG-latest.md`; update it along with the version.

## License

MIT — see [LICENSE](LICENSE).
