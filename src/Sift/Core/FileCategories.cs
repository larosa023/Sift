namespace Sift.Core;

public enum FileCategory
{
    Folder,
    Video,
    Audio,
    Image,
    Archive,
    DiskImage,
    Program,
    Document,
    Code,
    GameData,
    System,
    Other,
}

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    public static FileCategory Of(string ext) =>
        ext.Length > 0 && Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.DiskImage => "Disk images",
        FileCategory.GameData => "Game data",
        FileCategory.Program => "Programs",
        FileCategory.Document => "Documents",
        FileCategory.Image => "Images",
        FileCategory.Archive => "Archives",
        FileCategory.Folder => "Folders",
        _ => c.ToString(),
    };

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d["." + e] = c; }

        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "m4v", "flv", "mpg", "mpeg", "ts", "m2ts", "3gp");
        Add(FileCategory.Audio, "mp3", "flac", "wav", "aac", "ogg", "m4a", "wma", "opus", "aiff", "alac", "mid");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico", "avif");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "tgz", "cab", "lz", "lzma");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd", "dmg");
        Add(FileCategory.Program, "exe", "msi", "msix", "appx", "msixbundle", "appxbundle", "bat", "cmd", "ps1", "com", "jar", "apk");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "xlsm", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "csv", "md", "epub", "one", "pst", "ost", "dwg", "dxf", "rvt");
        Add(FileCategory.Code, "cs", "js", "ts", "tsx", "jsx", "py", "java", "c", "cpp", "h", "hpp", "rs", "go", "rb", "php", "html", "css", "scss", "json", "xml", "yml", "yaml", "sql", "sln", "csproj", "pdb", "obj", "lib", "o", "pyc", "class", "map");
        Add(FileCategory.GameData, "pak", "vpk", "bundle", "assets", "resource", "upk", "uasset", "ubulk", "utoc", "ucas", "bsa", "ba2", "forge", "rpf", "wad", "big", "arc", "dat", "bin", "pck", "sav");
        Add(FileCategory.System, "dll", "sys", "drv", "mui", "cat", "etl", "evtx", "log", "tmp", "dmp", "mdmp", "db", "sqlite", "ldb", "edb", "pf", "regtrans-ms", "blf", "msp", "manifest", "nupkg", "pri");
        return d;
    }
}
