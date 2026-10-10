using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ps5Backport.Core;

namespace GameAnalysis;

/// <summary>Paths are relative to the profile file; firmware targets and variants are explicit.</summary>
public sealed class Profile
{
    public string GameDirectory { get; set; } = "";
    public string ReferenceDirectory { get; set; } = "";
    public string? Aerolib { get; set; }
    public string? DataDirectory { get; set; }
    public string? FakelibDirectory { get; set; }
    public string Baseline { get; set; } = "default";
    public string[] Variants { get; set; } = [];
    public string[] Firmwares { get; set; } = [];
    public string[] StudioFirmwares { get; set; } = [];
    public string[] Options { get; set; } = [];
    public string[] ImportNids { get; set; } = [];
    public Window[] Windows { get; set; } = [];

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Profile Load(string path)
    {
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Json)
            ?? throw new ArgumentException("Profile must be a JSON object.");
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        profile.GameDirectory = ResolveRequired(profile.GameDirectory);
        profile.ReferenceDirectory = ResolveRequired(profile.ReferenceDirectory);
        profile.Aerolib = Resolve(profile.Aerolib);
        profile.DataDirectory = Resolve(profile.DataDirectory);
        profile.FakelibDirectory = Resolve(profile.FakelibDirectory);
        profile.Validate();
        return profile;
        string ResolveRequired(string value) => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("gameDirectory and referenceDirectory are required.") : Path.GetFullPath(value, folder);
        string? Resolve(string? value) => value is null ? null : ResolveRequired(value);
    }

    internal void Validate()
    {
        if (Variants is null || Firmwares is null || StudioFirmwares is null || Windows is null || ImportNids is null || Options is null)
            throw new ArgumentException("Profile arrays must not be null.");
        if (Variants.Concat(Firmwares).Concat(StudioFirmwares).Concat(ImportNids).Concat(Options).Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Profile arrays must contain nonempty strings.");
        if (Variants.Length == 0 || Variants.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Variants.Length
            || !Variants.Contains(Baseline, StringComparer.Ordinal))
            throw new ArgumentException("Variants must be distinct and include the baseline.");
        foreach (string variant in Variants)
        {
            string dir = Paths.ResolveRelativeFile(GameDirectory, variant);
            if (variant.Contains('/') || variant.Contains('\\')) throw new ArgumentException("Variants must be immediate child folders.");
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        }
        if (Firmwares.Length == 0) throw new ArgumentException("At least one firmware target is required.");
        Firmwares = Firmwares.Select(Firmware.Name).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var window in Windows)
        {
            if (window is null || string.IsNullOrWhiteSpace(window.Module) || string.IsNullOrWhiteSpace(window.Address))
                throw new ArgumentException("Each window requires a module and address.");
            _ = Paths.ResolveRelativeFile(GameDirectory, window.Module);
            _ = window.Vaddr;
            if (window.Length is < 1 or > 4096) throw new ArgumentException("Window length must be 1–4096 bytes.");
        }
        StudioFirmwares = StudioFirmwares.Select(Firmware.Name).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string firmware in StudioFirmwares)
        {
            if (!Firmwares.Contains(firmware)) throw new ArgumentException("Studio targets must also be listed in firmwares.");
            _ = Firmware.SdkFor(firmware);
        }
        if (StudioFirmwares.Length > 0 && DataDirectory is null) throw new ArgumentException("Studio dry runs require dataDirectory.");
        if (StudioFirmwares.Length == 0 && Options.Length > 0) throw new ArgumentException("Options require studioFirmwares targets.");
    }
}

/// <summary>A manually chosen instruction boundary, decoded for each variant containing the module.</summary>
public sealed class Window
{
    public string Module { get; set; } = "";
    public string Address { get; set; } = "";
    public int Length { get; set; }
    [JsonIgnore]
    internal ulong Vaddr => Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(Address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : ulong.Parse(Address, CultureInfo.InvariantCulture);
}
