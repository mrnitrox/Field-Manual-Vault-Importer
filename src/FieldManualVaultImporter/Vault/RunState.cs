using System.Text.Json;

namespace FieldManualVaultImporter.Vault;

/// <summary>The four collections of the Vault, by the key its links use (?coll=current).</summary>
public static class Collections
{
    public static readonly (string Key, string Label, int Count)[] All =
    {
        ("current", "Current Doctrine", 726),
        ("recent-archive", "Recent Archive", 1787),
        ("lessons-learned", "Lessons Learned", 71),
        ("historic", "Historic (before 1980)", 1631),
    };

    public static string Label(string key) => All.FirstOrDefault(c => c.Key == key).Label ?? key;

    /// <summary>The label without the date note.</summary>
    public static string Short(string key) => key == "historic" ? "Historic" : Label(key);
}

/// <summary>The file types the Vault offers for each publication.</summary>
public static class Formats
{
    public static readonly (string Key, string Label)[] All =
    {
        ("PDF", "PDF · the original document, page for page"),
        ("EPUB", "EPUB · reflowing text for phones and e-readers"),
    };
}

public enum RunPhase { Catalog, Download, Done }

/// <summary>
/// Everything a run needs to carry on after a pause, a crash or a power cut: the choices, the catalog read from the
/// site and the phase. Saved as vault-download.json in the destination folder, so it travels with the files.
/// Which files are downloaded isn't stored: a file is done when it's on disk under its final name (it's written as
/// .part and renamed only when complete).
/// </summary>
public sealed class RunState
{
    public const string FileName = "vault-download.json";

    public int Version { get; set; } = 1;
    /// <summary>The destination folder; files go to &lt;Root&gt;\&lt;Branch&gt;\&lt;Type&gt;\.</summary>
    public string Root { get; set; } = "";
    public List<string> Collections { get; set; } = new();
    /// <summary>The file types to download: PDF, EPUB or both.</summary>
    public List<string> Formats { get; set; } = new();
    public RunPhase Phase { get; set; } = RunPhase.Catalog;
    /// <summary>Listing pages read per collection; a collection in <see cref="ListedCollections"/> is complete.</summary>
    public Dictionary<string, int> PagesRead { get; set; } = new();
    public List<string> ListedCollections { get; set; } = new();
    public List<VaultManual> Manuals { get; set; } = new();
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime Saved { get; set; }

    public static string PathIn(string root) => Path.Combine(root, FileName);

    public static RunState? Load(string root)
    {
        try
        {
            var path = PathIn(root);
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<RunState>(File.ReadAllText(path));
            if (s != null) s.Root = root;   // the folder may have been moved or the drive letter changed
            return s;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Written to a temporary file and swapped in, so a crash mid-write never leaves a broken state.</summary>
    public void Save()
    {
        Saved = DateTime.Now;
        Directory.CreateDirectory(Root);
        var path = PathIn(Root);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The publications of the chosen collections whose page was read.</summary>
    public IEnumerable<VaultManual> Chosen => Manuals.Where(m => m.Detailed && Collections.Contains(m.Collection));

    /// <summary>The files to download of a publication: the chosen types.</summary>
    public IEnumerable<VaultFile> FilesOf(VaultManual m) => m.Files.Where(f => Formats.Contains(f.Format));

    public IEnumerable<VaultFile> ChosenFiles => Chosen.SelectMany(FilesOf);
}

/// <summary>One publication as the Vault describes it.</summary>
public sealed class VaultManual
{
    public string Slug { get; set; } = "";
    public string Collection { get; set; } = "";
    /// <summary>Its details page was read (the fields below are filled).</summary>
    public bool Detailed { get; set; }
    public string? Error { get; set; }

    public string Id { get; set; } = "";
    public string Designation { get; set; } = "";
    public string Title { get; set; } = "";
    public string PubType { get; set; } = "";
    public string Branch { get; set; } = "";
    public string BranchLabel { get; set; } = "";
    public List<string> Services { get; set; } = new();
    public string? EditionDate { get; set; }
    public string? ChangeLevel { get; set; }
    public string? Status { get; set; }
    public int? Pages { get; set; }
    public string? Function { get; set; }
    public string? Echelon { get; set; }
    public string? Purpose { get; set; }
    public string? Proponent { get; set; }
    public string? Toc { get; set; }
    public string? CoverUrl { get; set; }
    public string? CoverFile { get; set; }
    public List<VaultFile> Files { get; set; } = new();

    public string PageUrl => VaultSite.Base + "/field-manual-vault/" + Slug;
}

public sealed class VaultFile
{
    public string Format { get; set; } = "";   // PDF, EPUB
    public string Url { get; set; } = "";
    /// <summary>Relative to the root: &lt;Branch&gt;\&lt;Type&gt;\&lt;name&gt;.</summary>
    public string RelativePath { get; set; } = "";
    /// <summary>Size the site gives (0 when it gives none); the real size once downloaded.</summary>
    public long Size { get; set; }
    /// <summary>The site answered 404: not retried in this run.</summary>
    public bool Missing { get; set; }
}
