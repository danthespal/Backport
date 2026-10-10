using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ps5Backport.Core;

/// <summary>What sce_sys/param.json says, the warnings for the target firmware, and the lowered text if asked.</summary>
public sealed record ParamInfo(string TitleId, string ContentVersion, List<string> Warnings, string? PatchedText);

/// <summary>What the GUI shows about a game (from sce_sys/param.json and sce_sys/*.png).</summary>
/// <param name="Region">From the content ID prefix: UP = US, EP = Europe, JP = Japan, HP = Asia.</param>
/// <param name="RequiredFirmware">requiredSystemSoftwareVersion as "13.60" (empty when absent).</param>
public sealed record GameInfo(string Root, string TitleName, string TitleId, string ContentId, string Region,
                              string ContentVersion, string RequiredFirmware, string SdkVersion,
                              string? IconPath, string? BackgroundPath);

/// <summary>Reading a game folder before patching.</summary>
public static class GameScan
{
    public const string MainExecutable = "eboot.bin";
    /// <summary>Libraries, plus extra executables some games launch besides eboot.bin (Battlefield 6: SP/&lt;name&gt;_Ps5_retail.elf).</summary>
    static readonly string[] ModuleExtensions = [".prx", ".sprx", ".elf"];
    static readonly string[] ParamFields = ["requiredSystemSoftwareVersion", "sdkVersion"];
    static readonly string[] AmprIndexFiles = ["ampr_emu.index", "ampr_emu.index.tmp"];

    /// <summary>Every file under a folder, in os.walk order (a folder's files sorted, then its subfolders sorted).</summary>
    public static IEnumerable<string> WalkFiles(string dir) => WalkFiles(dir, default);

    /// <summary>Ordered traversal with cancellation between directories/files.</summary>
    public static IEnumerable<string> WalkFiles(string dir, CancellationToken cancel)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.TryPop(out string? current))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (string file in Directory.EnumerateFiles(current).Order(StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                yield return file;
            }
            // Never follow directory links/junctions: they can lead back to an ancestor.
            foreach (string sub in Directory.EnumerateDirectories(current).OrderDescending(StringComparer.Ordinal))
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(sub);
        }
    }

    /// <summary>Relative paths of eboot.bin, every .prx/.sprx and every .elf executable under the game root (os.walk order, sorted).</summary>
    public static List<string> FindModules(string root) => FindModules(root, default);

    /// <summary>Ordered module discovery with cancellation during traversal.</summary>
    public static List<string> FindModules(string root, CancellationToken cancel)
    {
        return WalkFiles(root, cancel).Where(file => Path.GetFileName(file).Equals(MainExecutable, StringComparison.OrdinalIgnoreCase)
            || ModuleExtensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            .Select(file => Path.GetRelativePath(root, file)).ToList();
    }

    /// <summary>
    /// A shipped ampr_emu index belongs to one ampr_emu build and app0 layout. CONTROL's README:
    /// delete it in app0 before playing, the emulator rebuilds it. An overlay can't delete files.
    /// </summary>
    public static string? AmprIndexWarning(string root)
    {
        var found = AmprIndexFiles.Where(n => File.Exists(Path.Combine(root, n))).ToList();
        return found.Count == 0 ? null :
            $"{string.Join(", ", found)} present in the game folder: delete it from app0 on the console before playing " +
            "(ampr_emu rebuilds it; a stale index from another ampr_emu build can break the game). " +
            "The output overlay cannot remove it.";
    }

    static readonly string[] DumpFakelibs = ["libSceAmpr.sprx", "libScePlayGo.sprx"];

    /// <summary>
    /// A dump needs ampr_emu (libSceAmpr) and the PlayGo stub in fakelib/ under ShadowMountPlus
    /// (REFERENCE_OVERVIEW Part 4): a warning when neither the game nor the fakelib set provides them.
    /// </summary>
    /// <param name="modules">Relative paths of every module of the game plus the added fakelibs.</param>
    public static string? DumpFakelibWarning(IEnumerable<string> modules)
    {
        var present = modules.Select(m => m.Replace('\\', '/').ToLowerInvariant()).ToHashSet();
        var missing = DumpFakelibs.Where(n => !present.Contains($"fakelib/{n.ToLowerInvariant()}")).ToList();
        return missing.Count == 0 ? null :
            $"no fakelib/{string.Join(" or fakelib/", missing)} in the game or the fakelib set: a dump needs them under " +
            "ShadowMountPlus, so the game would depend on the console's own (global fakelib or auto_update_ampr)";
    }

    /// <summary>
    /// Title, version and firmware warnings from sce_sys/param.json. With <paramref name="patchParam"/>,
    /// <see cref="ParamInfo.PatchedText"/> is the file with the versions above the target lowered
    /// (formatting kept), to be written to the overlay.
    /// </summary>
    public static ParamInfo ReadParam(string root, string targetFirmware, bool patchParam = false)
    {
        string path = Path.Combine(root, "sce_sys", "param.json");
        if (!File.Exists(path))
            return new ParamInfo("", "", ["sce_sys/param.json not found"], null);
        string text = File.ReadAllText(path, new UTF8Encoding(false));
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];                                  // utf-8-sig
        using var doc = JsonDocument.Parse(text);
        var param = doc.RootElement;
        string Get(string key) => param.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var info = new ParamInfo(Get("titleId"), Get("contentVersion"), [], null);

        string target = Firmware.ParamVersion(targetFirmware);
        var above = ParamFields.Where(k => Get(k).Length > 0 && Hex(Get(k)) > Hex(target)).ToList();
        if (above.Count == 0)
            return info;
        if (!patchParam)
        {
            info.Warnings.Add(
                $"param.json {string.Join(", ", above.Select(k => $"{k}={Get(k)}"))} is above the target FW {targetFirmware}; " +
                "left unchanged (--patch-param lowers it; tested backports have done both)");
            return info;
        }
        // rewrite only the version values, keeping the file's formatting
        foreach (string k in above)
            text = Regex.Replace(text, "(\"" + k + "\"\\s*:\\s*\")0[xX][0-9A-Fa-f]+(\")", m => m.Groups[1].Value + target + m.Groups[2].Value);
        info.Warnings.Add($"param.json {string.Join(", ", above)} lowered to {target} (written to sce_sys/param.json)");
        return info with { PatchedText = text };
    }

    /// <summary>Title, IDs, region, versions and cover images of a game folder (empty fields when param.json is missing).</summary>
    public static GameInfo ReadInfo(string root)
    {
        string? File_(string name) => File.Exists(Path.Combine(root, "sce_sys", name)) ? Path.Combine(root, "sce_sys", name) : null;
        string path = Path.Combine(root, "sce_sys", "param.json");
        if (!File.Exists(path))
            return new GameInfo(root, Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), "", "", "", "", "", "", File_("icon0.png"), File_("pic0.png"));
        using var doc = JsonDocument.Parse(File.ReadAllText(path, new UTF8Encoding(false)).TrimStart('\uFEFF'));
        var p = doc.RootElement;
        string Get(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        string title = "";
        if (p.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
        {
            string lang = Get(lp, "defaultLanguage");
            if (lp.TryGetProperty(lang.Length > 0 ? lang : "en-US", out var local))
                title = Get(local, "titleName");
        }
        string contentId = Get(p, "contentId");
        string titleId = Get(p, "titleId");
        return new GameInfo(root, title.Length > 0 ? title : titleId, titleId, contentId, Region(contentId), Get(p, "contentVersion"),
                            FirmwareOf(Get(p, "requiredSystemSoftwareVersion")), FirmwareOf(Get(p, "sdkVersion")),
                            File_("icon0.png"), File_("pic0.png"));
    }

    /// <summary>Region from a content ID ("UP9000-PPSA01317_00-..."): the service prefix's first two letters.</summary>
    public static string Region(string contentId) => contentId.Length < 2 ? "" : contentId[..2] switch
    {
        "UP" => "US", "EP" => "Europe", "JP" => "Japan", "HP" => "Asia", "KP" => "Korea", "IP" => "Internal", var other => other,
    };

    /// <summary>"0x1360000000000000" -> "13.60" (BCD major.minor in the top two bytes); "" when not a version.</summary>
    public static string FirmwareOf(string paramVersion)
    {
        string hex = paramVersion.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? paramVersion[2..] : paramVersion;
        if (hex.Length != 16 || !hex.All(char.IsAsciiHexDigit))
            return "";
        return $"{int.Parse(hex[..2], NumberStyles.HexNumber):X}.{hex[2..4]}";
    }

    /// <summary>int(x, 16) for param.json values ('0x...' prefix allowed).</summary>
    static System.Numerics.BigInteger Hex(string value)
    {
        string digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return System.Numerics.BigInteger.Parse("0" + digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
