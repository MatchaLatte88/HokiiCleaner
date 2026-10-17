namespace Cleanii.Core.Categorization;

/// <summary>
/// Regeln für die Zuordnung zu <see cref="FileCategory"/>. Reihenfolge der Entscheidung:
/// 1. Datei im Laufwerks-Wurzelordner mit bekanntem Namen (pagefile.sys …),
/// 2. Pfad- oder Ordnernamen-Regel des Ordners (der tiefste/spezifischste Treffer gilt und wird an Unterordner vererbt),
/// 3. Dateiendung,
/// 4. <see cref="FileCategory.Other"/>.
/// </summary>
public sealed class CategoryRules
{
    private readonly Dictionary<string, FileCategory> _extensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileCategory> _directoryNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileCategory> _rootFileNames = new(StringComparer.OrdinalIgnoreCase);

    public static CategoryRules Default { get; } = CreateDefault();

    internal RuleNode PathRoot { get; } = new();

    /// <summary>Kategorie nach Dateiendung (ohne Pfadkontext); <see cref="FileCategory.Other"/> bei unbekannter Endung.</summary>
    public FileCategory ByExtension(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        return extension.Length > 1 && _extensions.TryGetValue(extension.Substring(1), out FileCategory category)
            ? category
            : FileCategory.Other;
    }

    internal bool TryGetRootFile(string fileName, out FileCategory category)
        => _rootFileNames.TryGetValue(fileName, out category);

    /// <summary>Wechselt in einen Unterordner: neuer Pfad-Regelknoten und die gültige Kategorie-Überschreibung.</summary>
    internal (RuleNode? Node, FileCategory? Override) Descend(RuleNode? node, FileCategory? inherited, string directoryName)
    {
        RuleNode? next = node?.Step(directoryName);
        FileCategory? effective = next?.Category
            ?? (_directoryNames.TryGetValue(directoryName, out FileCategory named) ? named : inherited);
        return (next, effective);
    }

    public CategoryRules AddExtensions(FileCategory category, params string[] extensions)
    {
        foreach (string extension in extensions)
        {
            _extensions[extension] = category;
        }

        return this;
    }

    public CategoryRules AddDirectoryNames(FileCategory category, params string[] names)
    {
        foreach (string name in names)
        {
            _directoryNames[name] = category;
        }

        return this;
    }

    public CategoryRules AddRootFiles(FileCategory category, params string[] fileNames)
    {
        foreach (string name in fileNames)
        {
            _rootFileNames[name] = category;
        }

        return this;
    }

    /// <summary>Pfadregel relativ zur Laufwerkswurzel. Ein Segment <c>*</c> passt auf genau einen beliebigen Ordnernamen.</summary>
    public CategoryRules AddPath(FileCategory category, params string[] segments)
    {
        RuleNode node = PathRoot;
        foreach (string segment in segments)
        {
            node = node.GetOrAddChild(segment);
        }

        node.Category = category;
        return this;
    }

    /// <summary>
    /// Regel für genau eine Datei an einem Pfad relativ zur Laufwerkswurzel (letztes Segment = Dateiname).
    /// Sie hat Vorrang vor der Regel des Ordners, in dem die Datei liegt.
    /// </summary>
    public CategoryRules AddPathFile(FileCategory category, params string[] segments)
    {
        if (segments.Length < 2)
        {
            throw new ArgumentException("Ein Pfad mit Ordner und Dateiname ist nötig.", nameof(segments));
        }

        RuleNode node = PathRoot;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            node = node.GetOrAddChild(segments[i]);
        }

        node.AddFile(segments[^1], category);
        return this;
    }

    private static CategoryRules CreateDefault()
    {
        var rules = new CategoryRules();

        rules.AddExtensions(FileCategory.Videos, "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg", "m2ts", "mts", "3gp", "vob");
        rules.AddExtensions(FileCategory.Images, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "svg", "ico", "raw", "cr2", "nef", "arw", "dng", "psd", "exr", "tga");
        rules.AddExtensions(FileCategory.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "mid", "midi");
        rules.AddExtensions(FileCategory.Documents, "doc", "docx", "xls", "xlsx", "ppt", "pptx", "pdf", "txt", "rtf", "odt", "ods", "odp", "csv", "md", "epub");
        rules.AddExtensions(FileCategory.Archives, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab");
        rules.AddExtensions(FileCategory.Installers, "msi", "msix", "msixbundle", "appx", "appxbundle", "iso", "img", "dmg");
        rules.AddExtensions(FileCategory.AiModels, "safetensors", "gguf", "ckpt", "pt", "pth", "onnx");
        rules.AddExtensions(FileCategory.VirtualDisks, "vhd", "vhdx", "vmdk", "vdi", "qcow2");
        rules.AddExtensions(FileCategory.Apps, "exe", "dll", "ocx");

        rules.AddRootFiles(FileCategory.System, "hiberfil.sys", "pagefile.sys", "swapfile.sys");

        rules.AddPath(FileCategory.OldWindows, "Windows.old");
        rules.AddPath(FileCategory.System, "Windows");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "Temp");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "SoftwareDistribution", "Download");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "Logs");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "Prefetch");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "Minidump");
        rules.AddPathFile(FileCategory.TempAndCaches, "Windows", "MEMORY.DMP");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "SoftwareDistribution", "DeliveryOptimization");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization");
        rules.AddPath(FileCategory.TempAndCaches, "Windows", "ServiceProfiles", "LocalService", "AppData", "Local", "FontCache");
        rules.AddPath(FileCategory.TempAndCaches, "ProgramData", "Microsoft", "Windows", "WER");
        rules.AddPath(FileCategory.TempAndCaches, "found.000");
        rules.AddPath(FileCategory.System, "System Volume Information");
        rules.AddPath(FileCategory.RecycleBin, "$Recycle.Bin");
        rules.AddPath(FileCategory.Apps, "Program Files");
        rules.AddPath(FileCategory.Apps, "Program Files (x86)");
        rules.AddPath(FileCategory.Apps, "ProgramData");
        rules.AddPath(FileCategory.TempAndCaches, "Users", "*", "AppData", "Local", "Temp");
        rules.AddPath(FileCategory.TempAndCaches, "Users", "*", "AppData", "Local", "CrashDumps");
        rules.AddPath(FileCategory.TempAndCaches, "Users", "*", "AppData", "Local", "Microsoft", "Windows", "INetCache");
        rules.AddPath(FileCategory.TempAndCaches, "Users", "*", "AppData", "Local", "Microsoft", "Windows", "Explorer"); // Miniaturansichten und Symbol-Cache
        rules.AddPath(FileCategory.TempAndCaches, "Users", "*", "AppData", "Local", "Microsoft", "Windows", "WER");
        rules.AddPath(FileCategory.Apps, "Users", "*", "AppData", "Local", "Programs");

        rules.AddDirectoryNames(
            FileCategory.TempAndCaches,
            "Cache", "Code Cache", "GPUCache", "ShaderCache", "GrShaderCache", "DawnCache", "CacheStorage", "CachedData",
            "DXCache", "GLCache", "D3DSCache");

        return rules;
    }

    /// <summary>Knoten des Pfad-Regelbaums. Exakter Treffer hat Vorrang vor <c>*</c>; beides gleichzeitig am selben Knoten wird nicht unterstützt.</summary>
    internal sealed class RuleNode
    {
        private Dictionary<string, RuleNode>? _children;
        private Dictionary<string, FileCategory>? _files;
        private RuleNode? _wildcard;

        public FileCategory? Category { get; set; }

        public void AddFile(string fileName, FileCategory category)
        {
            _files ??= new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
            _files[fileName] = category;
        }

        public bool TryGetFile(string fileName, out FileCategory category)
        {
            category = default;
            return _files is not null && _files.TryGetValue(fileName, out category);
        }

        public RuleNode? Step(string name)
        {
            if (_children is not null && _children.TryGetValue(name, out RuleNode? child))
            {
                return child;
            }

            return _wildcard;
        }

        public RuleNode GetOrAddChild(string segment)
        {
            if (segment == "*")
            {
                return _wildcard ??= new RuleNode();
            }

            _children ??= new Dictionary<string, RuleNode>(StringComparer.OrdinalIgnoreCase);
            if (!_children.TryGetValue(segment, out RuleNode? child))
            {
                child = new RuleNode();
                _children[segment] = child;
            }

            return child;
        }
    }
}
