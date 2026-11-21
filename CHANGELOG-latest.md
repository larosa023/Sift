## Sift 1.2

### Faster
- **Fast scan.** Run Sift as administrator and it reads the drive's file table directly instead of opening every folder. A whole drive with over a million files scans in a few seconds, and folders Windows normally won't list (like System Volume Information) are counted too. Results match the normal scan exactly; if fast scan isn't possible, Sift falls back on its own.
- **Opens instantly.** Your last scan is saved and shown the moment Sift starts, with a note saying how old it is. Press F5 for fresh results.
- **Quicker start-up** (precompiled code), and **duplicate checks remember file hashes**, so running them again only reads files that changed.

### New
- **What changed since last time.** A new *Changes* view shows which folders and files grew, shrank, appeared or disappeared since the previous scan, and sizes in the list show +/− next to them.
- **File types view.** Space used by every extension (.mp4, .iso, .psd…). Double-click one to list its biggest files.
- **Search everywhere below a folder**, with wildcards: `*.mkv`, `IMG_20??*`.
- **Colour the map by age** to spot what hasn't been touched in years.
- **Windows extras** cleanup check: the hibernation file (turn hibernation off in one step), a previous Windows installation and upgrade leftovers (handed to Windows' own Storage settings), and crash dumps.
- **Large old files** cleanup check: big files untouched for a year, outside Windows, program folders and game libraries.
- **More leftovers found:** NVIDIA and AMD driver installers, Steam's unfinished downloads and temp files, the Epic launcher cache.
- **Undo** (Ctrl+Z) puts everything the last cleanup sent to the Recycle Bin back where it was.
- **"Analyze with Sift"** on the right-click menu of folders and drives in File Explorer (turn it on in Settings).

### Themes
- Sift now uses **the same 14 themes as Hangar** and the rest of the suite, plus Mocha and High contrast.
- **Match Hangar** follows Hangar's theme and accent and changes the moment you change them in Hangar.
- **Custom theme files** in Hangar's format load from Sift's and Hangar's themes folders.
- Accent colours: the theme's own, your Windows accent, Hangar's swatches, or any colour you type in.

### Refinements
- Opening Sift again brings the running window forward (and scans the folder you passed it) instead of starting a second copy. `--quit` closes it gracefully and `--uninstall` removes its settings, for Hangar.
- Long paths in the folder bar collapse to *root › … › last three*.
- The list panel remembers its width; the folder list's columns fit without cutting off.
- Review shows what each cleanup did on its own line, with the Undo button next to it.
- Clearer confirmation text for every kind of cleanup action.
- The crash log stays small.
