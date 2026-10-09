using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>One file in the SELF tools list.</summary>
public sealed record SelfFileRow(string Path, string Kind, string Module, string Sdk, string Counts, string Tone)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsElf => Kind == "ELF";
    public bool IsSelf => Kind is "PS5 SELF" or "PS4 fake SELF";
}

/// <summary>One line of the NID lookup.</summary>
public sealed record NidRow(string Name, string Nid, string Known);

/// <summary>
/// The Tools page (APP_PLAN 7.4, 7.5): SELF/ELF info, unwrap and fake-sign for a batch of files,
/// and NID lookup in both directions with what the knowledge base says about each function.
/// </summary>
public sealed partial class ToolsViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<SelfFileRow> Files { get; } = [];
    public ObservableCollection<NidRow> NidResults { get; } = [];

    /// <summary>Where Unwrap and Sign write; empty = next to each file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputText))]
    public partial string OutputFolder { get; set; } = "";

    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial string NidQuery { get; set; } = "";

    public string OutputText => OutputFolder.Length == 0 ? "Output: next to each file" : $"Output: {OutputFolder}";
    public bool HasFiles => Files.Count > 0;

    // --- SELF tools (7.4) ------------------------------------------------------------------------
    [RelayCommand]
    async Task AddFiles()
    {
        if (main.Store.PickFiles is { } pick &&
            await pick("Select SELF or ELF files", "PS5 modules", ["*.bin", "*.sprx", "*.prx", "*.elf", "*.self"], true) is { Length: > 0 } files)
            await Add(files);
    }

    /// <summary>Adds files (from the picker or a drop) and reads their kind, module, SDK and import/export counts.</summary>
    public async Task Add(IEnumerable<string> paths)
    {
        var fresh = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => Files.All(f => !string.Equals(f.Path, p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (fresh.Count == 0)
            return;
        Busy = true;
        try
        {
            foreach (string path in fresh)
                Files.Add(await Task.Run(() => Describe(path)));
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(HasFiles));
        }
    }

    static SelfFileRow Describe(string path)
    {
        try
        {
            string kind = Self.FileKind(path);
            if (kind == Self.KindUnknown)
                return new SelfFileRow(path, "not a SELF or ELF", "", "", "", "warn");
            if (Self.IsSelf(kind) && Self.EncryptedSegments(path) > 0)
                return new SelfFileRow(path, "encrypted", "", "", "decrypt first (PS5-SELF-Decrypter)", "error");
            byte[] data = File.ReadAllBytes(path);
            var elf = new SceElf(Self.IsSelf(kind) ? Self.Unwrap(data) : data);
            var (ps5, _) = Sdk.Read(elf);
            string shortKind = kind == Self.KindElf ? "ELF" : kind == Self.KindPs5Self ? "PS5 SELF" : "PS4 fake SELF";
            return new SelfFileRow(path, shortKind, elf.ModuleName ?? "", $"{ps5 >> 24:X}.{(ps5 >> 16) & 0xFF:X2}",
                                   $"{elf.Imports().Count:N0} / {elf.Exports().Count:N0}", "muted");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or UnauthorizedAccessException)
        {
            return new SelfFileRow(path, "unreadable", "", "", e.Message, "error");
        }
    }

    [RelayCommand]
    void ClearFiles()
    {
        Files.Clear();
        OnPropertyChanged(nameof(HasFiles));
    }

    [RelayCommand]
    async Task ChooseOutput()
    {
        if (main.PickFolder is not null && await main.PickFolder("Output folder for unwrapped and signed files") is { } folder)
            OutputFolder = folder;
    }

    [RelayCommand]
    void OutputNextToFiles() => OutputFolder = "";

    /// <summary>Every SELF in the list -> &lt;name&gt;.elf.</summary>
    [RelayCommand]
    Task Unwrap() => ConvertFiles(f => f.IsSelf, "unwrap", f => f.Name + ".elf", Self.UnwrapFile);

    /// <summary>Every ELF in the list -> a fake-signed SELF (&lt;name&gt; without .elf, else &lt;name&gt;.self).</summary>
    [RelayCommand]
    Task Sign() => ConvertFiles(f => f.IsElf, "sign", f => f.Name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase) ? f.Name[..^4] : f.Name + ".self",
                           Self.SignFile);

    /// <summary>Converts the matching files; never overwrites an existing file.</summary>
    async Task ConvertFiles(Func<SelfFileRow, bool> applies, string verb, Func<SelfFileRow, string> outputName, Action<string, string> run)
    {
        var todo = Files.Where(applies).ToList();
        if (todo.Count == 0)
        {
            main.Log.Warning($"Nothing to {verb}: the list has no {(verb == "sign" ? "ELF" : "decrypted SELF")} files");
            return;
        }
        Busy = true;
        var written = new List<string>();
        try
        {
            foreach (var file in todo)
            {
                string folder = OutputFolder.Length > 0 ? OutputFolder : System.IO.Path.GetDirectoryName(file.Path)!;
                string output = System.IO.Path.Combine(folder, outputName(file));
                if (File.Exists(output))
                {
                    main.Log.Error($"{output} already exists; not overwritten");
                    continue;
                }
                try
                {
                    await Task.Run(() => run(file.Path, output));
                    main.Log.Info($"{(verb == "sign" ? "Signed" : "Unwrapped")} {file.Path} -> {output}");
                    written.Add(output);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
                {
                    main.Log.Error($"Couldn't {verb} {file.Path}: {e.Message}");
                }
            }
        }
        finally { Busy = false; }
        await Add(written);
    }

    // --- NID lookup (7.5) ---------------------------------------------------------------------------
    partial void OnNidQueryChanged(string value) => Lookup();

    /// <summary>
    /// Each word of the query: an 11-character NID (or 'NID#lib#mod' symbol) gives its name; anything else is a
    /// name and gives its NID. Each result says what the knowledge base knows about the function.
    /// </summary>
    void Lookup()
    {
        NidResults.Clear();
        foreach (string word in NidQuery.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string nid, name;
            string? symbolIds = null;
            if (TrySplit(word) is { } split)
            {
                (nid, name) = (split.Nid, main.FunctionName(split.Nid));
                symbolIds = $"library {split.Library}, module {split.Module}";
            }
            else if (IsKnownNid(word))
                (nid, name) = (word, main.FunctionName(word));
            else
                (nid, name) = (Nid.Compute(word), word);
            NidResults.Add(new NidRow(name == nid ? "(name not in aerolib.csv)" : name, nid, Known(nid, symbolIds)));
        }
    }

    /// <summary>An 11-character word is a NID when aerolib.csv, the knowledge base or a firmware list knows it.</summary>
    bool IsKnownNid(string word)
    {
        if (word.Length != 11 || !word.All(c => Nid.Alphabet.Contains(c)))
            return false;
        var k = main.KnowledgeData;
        return main.FunctionName(word) != word || k.Fixes.ContainsKey(word) || k.Introduced.ContainsKey(word)
               || main.Database.Firmwares.Any(f => f.Db.Nids.Contains(word));
    }

    static (string Nid, int Library, int Module)? TrySplit(string word)
    {
        try { return Nid.SplitSymbol(word); }
        catch (FormatException) { return null; }
    }

    string Known(string nid, string? symbolIds)
    {
        var facts = new List<string>();
        if (symbolIds is not null)
            facts.Add(symbolIds);
        var k = main.KnowledgeData;
        if (FirmwareDb.Introduced(nid, main.Database.Firmwares.Select(f => f.Db), k) is { } introduced)
            facts.Add($"from FW {introduced.Firmware}");
        if (k.Fixes.TryGetValue(nid, out var fix))
            facts.Add($"known fix: {fix.Kind} ({(fix.Proven ? "proven" : "not console-tested")})");
        if (k.Candidates.TryGetValue(nid, out var candidate))
            facts.Add($"AnyPS5 candidate: return {candidate.Value}");
        if (k.LeftUnpatched.TryGetValue(nid, out var left))
            facts.Add(left.Count == 1 ? "left unpatched by a tested backport" : $"left unpatched by {left.Count} tested backports");
        return string.Join(" · ", facts);
    }

    /// <summary>The page opened: load the firmware lists for "from FW" (shared with the Firmware database page).</summary>
    public async Task Open()
    {
        if (main.Database.Firmwares.Count == 0)
            await main.Database.Refresh();
        Lookup();
    }
}
