using CommunityToolkit.Mvvm.ComponentModel;
using Ps5Backport.Core;

namespace Ps5Backport.App.ViewModels;

/// <summary>One optional patch on the Overview tab (data/fixes.json "options"); chosen per game, never saved.</summary>
public sealed partial class OptionRow(FixOption o) : ObservableObject
{
    public string Key => o.Key;
    public string Name => o.Name;
    public string Description => o.Description;
    public string Warning => o.Warning;
    [ObservableProperty] public partial bool IsEnabled { get; set; }
}

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
}
