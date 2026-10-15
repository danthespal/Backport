using CommunityToolkit.Mvvm.ComponentModel;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>A concise warning explanation with the original engine message available as details.</summary>
public sealed class WarningRow(string detail)
{
    public string Detail => detail;
    public string Summary => detail switch
    {
        _ when detail.StartsWith("param.json ", StringComparison.Ordinal) && detail.Contains("is above the target FW", StringComparison.Ordinal)
            => "The game's declared firmware or SDK is newer than the target. Its version fields are being left unchanged; review the details.",
        _ when detail.StartsWith("param.json ", StringComparison.Ordinal) && detail.Contains("lowered to", StringComparison.Ordinal)
            => "Version lowering is included in this result. Changing these fields does not establish compatibility.",
        "sce_sys/param.json not found" => "Game metadata is missing. Check that you opened the game root folder.",
        _ when detail.StartsWith("uses PS VR2", StringComparison.Ordinal)
            => "This game uses PS VR2. Check whether VR is required and whether the target firmware supports it.",
        _ when detail.StartsWith("AMPR debug logging enabled", StringComparison.Ordinal)
            => "AMPR diagnostic logging is enabled. Collect the log after testing and use a build without logging for release.",
        _ when detail.StartsWith("no fakelib/libSceAmpr", StringComparison.Ordinal)
            => "The dump has no AMPR or PlayGo replacement library. Under ShadowMountPlus, it would depend on the console's library setup; review the details.",
        _ when detail.StartsWith("plain ELF module(s)", StringComparison.Ordinal)
            => "Some plain ELF modules were left unchanged and are excluded from the output. Review the affected files.",
        _ when detail.StartsWith("possible dynamic Psml dependency", StringComparison.Ordinal)
            => "A possible dynamic Psml dependency was found. Review the included data; static evidence does not prove runtime use.",
        _ when detail.StartsWith("option ", StringComparison.Ordinal)
            => "An optional patch has a warning or library selection to review. Read its details before testing.",
        _ when detail.Contains("known-broken build", StringComparison.Ordinal)
            => "A module matches a known-broken library build. Review the affected module and the documented issue.",
        _ when detail.Contains("could not be read", StringComparison.Ordinal)
            => "A module could not be read and was left out of the backport. Check the source file and error details.",
        _ => detail,
    };
}

/// <summary>One optional patch on the Overview tab (data/fixes.json "options"); chosen per game, never saved.
/// <paramref name="select"/>: shows this patch in the details strip.</summary>
public sealed partial class OptionRow(FixOption o, Action<OptionRow>? select = null) : ObservableObject
{
    public string Key => o.Key;
    public string Name => o.Name;
    public string Description => o.Description;
    public string Warning => o.Warning;
    public string Group => o.Group;
    [ObservableProperty] public partial bool IsEnabled { get; set; }
    /// <summary>Shown in the details strip.</summary>
    [ObservableProperty] public partial bool IsSelected { get; set; }
    /// <summary>The requirements probe's verdict for the loaded game (<see cref="Pipeline.Verdict"/> spelling), empty before Analyze.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict), nameof(VerdictLabel), nameof(IsNeeded), nameof(IsApplicable), nameof(IsNotApplicable), nameof(IsConsoleChoice), nameof(RowOpacity))]
    public partial string Verdict { get; set; } = "";
    /// <summary>Why the probe decided so (static evidence, not console compatibility).</summary>
    [ObservableProperty] public partial string Evidence { get; set; } = "";

    public bool HasVerdict => Verdict.Length > 0;
    public string VerdictLabel => Verdict switch
    {
        "needed" => "needed", "applicable" => "applicable", "not-applicable" => "not applicable", "console-choice" => "console choice", _ => Verdict,
    };
    public bool IsNeeded => Verdict == "needed";
    public bool IsApplicable => Verdict == "applicable";
    public bool IsNotApplicable => Verdict == "not-applicable";
    public bool IsConsoleChoice => Verdict == "console-choice";
    /// <summary>Not-applicable rows are dimmed but stay selectable (an expert override is still possible).</summary>
    public double RowOpacity => IsNotApplicable ? 0.55 : 1.0;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void Select() => select?.Invoke(this);
}

/// <summary>One column of the optional patches panel: an area (data/fixes.json option "group") and its patches.</summary>
public sealed record PatchGroup(string Name, IReadOnlyList<OptionRow> Options);

/// <summary>One line of the Modules tab.</summary>
public sealed class ModuleRow(ModuleResult m)
{
    public ModuleResult Result => m;
    public string Path => m.Path;
    public string Container => m.Container.Replace(" (make_fself.py)", "");
    /// <summary>PS5 SDK as a firmware version: "10.00 → 4.00" (raw values in <see cref="SdkRaw"/>).</summary>
    public string Sdk => m.SdkPatched ? $"{Version(m.SdkBefore)} → {Version(m.SdkAfter)}" : Version(m.SdkBefore);
    public string SdkRaw => m.SdkPatched ? $"{m.SdkBefore} → {m.SdkAfter}" : m.SdkBefore;
    public string UnresolvedTone => Unresolved > 0 ? "warn" : "muted";
    public int Fixed => m.Fixed.Count + m.StubbedNow.Count;
    public int Unresolved => m.Unknown.Count;
    public string State => m.Error.Length > 0 ? "error" : m.Written ? "written" : Fixed > 0 || m.SdkPatched || m.Patterns.Count > 0 ? "will change" : "unchanged";
    /// <summary>Status key for coloring: ok / warn / error / muted.</summary>
    public string Tone => m.Error.Length > 0 ? "error" : State is "written" or "will change" ? "ok" : "muted";
    public bool HasError => m.Error.Length > 0;
    public bool HasChanges => Tone == "ok";
    public string StateDisplay => State switch { "error" => "Error", "written" => "Written", "will change" => "Will change", _ => "Unchanged" };
    public string StateDescription => HasError ? m.Error : StateDisplay;

    /// <summary>"0x10000040/0x12090001" (PS5/PS4 SDK) -> "10.00": the PS5 SDK's BCD major.minor.</summary>
    static string Version(string pair) =>
        pair.Length >= 10 && uint.TryParse(pair.AsSpan(2, 8), System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? $"{v >> 24:X}.{(v >> 16) & 0xFF:X2}" : pair;
}

/// <summary>One line of the Imports tab, with what the inspector shows for it.</summary>
public sealed class ImportRow
{
    public required string Module { get; init; }
    public required string Nid { get; init; }
    public required string Name { get; init; }
    public required string Library { get; init; }
    public required string Status { get; init; }
    public bool Plt { get; init; }
    public int? Refs { get; init; }
    public string Candidate { get; init; } = "";
    /// <summary>Number of console-tested backports that shipped this import unpatched.</summary>
    public int LeftByCount { get; init; }
    public string Introduced { get; init; } = "";
    /// <summary>What was done to it in this run ("adapter -> ... via cave", "ret0 @ 0x..."), if anything.</summary>
    public string Applied { get; init; } = "";
    public Fix? Fix { get; init; }

    public string RefsText => Refs?.ToString() ?? "";
    public bool HasRefs => Refs is not null;
    public bool HasApplied => Applied.Length > 0;
    public bool HasCandidate => Candidate.Length > 0;
    public bool HasLeftBy => LeftByCount > 0;
    public string LeftByText => LeftByCount == 1 ? "A console-tested backport shipped it unpatched"
                                                 : $"{LeftByCount} console-tested backports shipped it unpatched";
    public bool HasIntroduced => Introduced.Length > 0;
    public bool HasFix => Fix is not null;
    public string FixSummary => Fix is null ? "" :
        $"{Fix.Kind} · {(Fix.Proven ? "console-tested" : "not console-tested")}";
    public string FixNote => Fix?.Note ?? "";
    public string Tone => Status switch
    {
        Imports.Stubbed or Imports.Reference or Imports.Provided => "ok",
        Imports.Patched => "patched",
        Imports.Missing or Imports.Unknown => "warn",
        _ => "muted",
    };
    public bool IsResolved => Tone is "ok" or "patched";
    public bool IsUnresolved => Imports.IsUnresolved(Status);
    public string StatusDisplay => Status switch
    {
        Imports.Reference => "On firmware", Imports.Provided => "Game library", Imports.Stubbed => "Stubbed",
        Imports.Patched => "Patched", Imports.Data => "Data · unchecked", Imports.Missing => "Missing", Imports.Unknown => "Unknown", _ => Status,
    };
    public string StatusDescription => Status switch
    {
        Imports.Reference => "Exported by the reference firmware.",
        Imports.Provided => "Provided by a library shipped with the game or backport.",
        Imports.Stubbed => "Neutralized with a local stub; this alone does not establish compatibility.",
        Imports.Patched => "Redirected or adapted by a patch.",
        Imports.Data => "Data import: function export lists do not verify it.",
        Imports.Missing => "Not exported by the exact target firmware reference; needs a fix.",
        Imports.Unknown => "Not found in a lower-bound reference; availability on the target is uncertain.",
        _ => Status,
    };
}
