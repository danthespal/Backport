using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

/// <summary>
/// One crash address mapped onto a module: Kind is "RIP", "fault address" or "backtrace" (Index = position in the
/// backtrace); Vaddr is the module's ELF address when the address falls in its code, else null; Note says how it was mapped.
/// A backtrace entry is a return address: the call that made it ends there.
/// </summary>
public sealed record CrashFrame(string Kind, int Index, ulong Runtime, ulong? Vaddr, string Note)
{
    public bool IsReturnAddress => Kind == "backtrace";
}

/// <summary>A fatal event of a crash log (CrashLog.Analyze) with its addresses mapped onto one module.</summary>
public sealed record CrashEvent(string Signal, string Reason, IReadOnlyList<CrashFrame> Frames, string Evidence);

/// <summary>
/// Maps a crash log's addresses onto the eboot the code graph shows (README "Code graph page"). With the log's module map,
/// an address in the eboot's logged text range is that range's offset plus the code segment's address, and the range size
/// is checked against the code segment. Without a map (ShadowMount one-line summaries) the usual eboot base 0x400000 is
/// assumed and said so. Evidence for reading a crash, not proof that this file is the binary that crashed.
/// </summary>
public static class CrashMap
{
    /// <summary>Where a PS5 eboot's code is loaded when the log has no module map (FINDINGS §2: PPSA38510).</summary>
    public const ulong DefaultEbootBase = 0x400000;

    public static List<CrashEvent> Map(JsonObject analysis, CodeImage image)
    {
        var text = image.Elf.TextSegment();
        ulong textEnd = text.Vaddr + (ulong)text.Memsz;
        string stem = Path.GetFileNameWithoutExtension(image.Path);
        bool Ours(string runtimePath) => string.Equals(Path.GetFileNameWithoutExtension(runtimePath.Replace('\\', '/').Split('/')[^1]), stem, StringComparison.OrdinalIgnoreCase);

        var result = new List<CrashEvent>();
        foreach (var ev in (analysis["events"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var modules = (ev["modules"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var ours = modules.Where(m => Ours(m["path"]?.GetValue<string>() ?? "")).ToList();
            string evidence;
            if (ours.Count == 0)
                evidence = modules.Count == 0
                    ? $"the log has no module map: assumed the eboot's code starts at 0x{DefaultEbootBase:x}"
                    : $"the log's module map has no {stem}: its addresses are in other modules";
            else
            {
                var sizes = ours.SelectMany(m => (m["ranges"] as JsonArray ?? []).OfType<JsonObject>())
                    .Select(r => r["end"]!.GetValue<ulong>() - r["start"]!.GetValue<ulong>()).ToList();
                // the loader maps whole 16 KB pages
                bool fits = sizes.Any(s => s >= (ulong)text.Memsz && s - (ulong)text.Memsz < 0x4000);
                evidence = fits ? $"the logged text range of {stem} matches this file's code size (0x{text.Memsz:x})"
                    : $"the logged text size ({string.Join(", ", sizes.Select(s => $"0x{s:x}"))}) differs from this file's code (0x{text.Memsz:x}): it may not be the build that crashed";
            }

            var frames = new List<CrashFrame>();
            int backtrace = 0;
            foreach (var a in (ev["addresses"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string kind = a["kind"]?.GetValue<string>() ?? "";
                string normalized = kind.StartsWith("backtrace", StringComparison.Ordinal) ? "backtrace" : kind;
                int index = normalized == "backtrace" ? backtrace++ : 0;
                ulong runtime = a["value"]!.GetValue<ulong>();
                string? module = a["runtimeModule"]?.GetValue<string>();
                ulong? vaddr = null;
                string note;
                if (runtime == 0)
                    note = "zero: a call or jump through a null pointer (the caller is the next backtrace entry)";
                else if (module is not null && Ours(module) && a["textRelativeOffset"]?.GetValue<string>() is { } rel)
                {
                    ulong v = text.Vaddr + Convert.ToUInt64(rel[2..], 16);
                    (vaddr, note) = v < textEnd ? ((ulong?)v, $"{module} + {rel}") : (null, $"{module} + {rel}, outside this file's code");
                }
                else if (module is not null)
                    note = $"in {module}";
                else if (ours.Count == 0 && modules.Count == 0 && runtime >= DefaultEbootBase && runtime - DefaultEbootBase + text.Vaddr < textEnd)
                    (vaddr, note) = (runtime - DefaultEbootBase + text.Vaddr, $"assumed eboot base 0x{DefaultEbootBase:x}");
                else
                    note = a["resolution"]?.GetValue<string>() ?? "not in a logged module";
                frames.Add(new CrashFrame(normalized, index, runtime, vaddr, note));
            }
            result.Add(new CrashEvent(ev["signal"]?.GetValue<string>() ?? ev["title"]?.GetValue<string>() ?? "",
                                      ev["reason"]?.GetValue<string>() ?? "", frames, evidence));
        }
        return result;
    }
}
