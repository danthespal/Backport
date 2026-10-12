using System.Text.Json.Nodes;

namespace Ps5Backport.Core;

public static partial class Pipeline
{
    // Filename policy from ShadowMountPlus 1.7beta5-fix1, sm_fakelib.c.
    // These are assessed rules, not the console's observed loader/settings.
    static readonly string[] DeploymentEmulators = ["libSceAppContent.sprx", "libSceGameUpdate.sprx",
        "libSceNpEntitlementAccess.sprx", "libSceAmpr.sprx", "libScePlayGo.sprx"];

    static JsonObject DeploymentReport(GameResult r)
    {
        var libraries = r.Modules.Select(m => m.Path.Replace('\\', '/'))
            .Where(p => (p.StartsWith("fakelib/", StringComparison.Ordinal) || p.StartsWith("fakelib2/", StringComparison.Ordinal))
                && p.Count(c => c == '/') == 1)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
        var kernel = libraries.Where(p => Name(p) == "libkernel.sprx").ToArray();
        var full = libraries.Where(p => Name(p) != "libkernel.sprx" && !DeploymentEmulators.Contains(Name(p), StringComparer.Ordinal)).ToArray();
        var packageExcluded = libraries.Where(p => !DeploymentEmulators.Take(3).Contains(Name(p), StringComparer.Ordinal)).ToArray();
        var unassessed = r.Modules.Where(m => m.Error.Length > 0 && libraries.Contains(m.Path.Replace('\\', '/'), StringComparer.Ordinal))
            .Select(m => m.Path.Replace('\\', '/')).ToArray();
        var notes = new List<string>
        {
            "Assessed profile: ShadowMountPlus 1.7beta5-fix1 (98c524890176c7b474fcb03387bec88c4a559861). Console loader version, settings and launch format are unknown.",
            "Planned merged-game inventory includes unchanged source libraries; this output is a changed-file overlay, not a complete game. Merge with the matching original for folder/image launches.",
            "After successful publication, backport_publication.json Files records output paths, Size and Sha256; backport_run.json records source provenance. Analysis has no final output hashes; these reports do not confirm a console mount.",
        };
        if (libraries.Length > 0)
        {
            notes.Add("Fakelib modules in the planned merge: " + string.Join(", ", libraries));
            if (full.Length > 0)
                notes.Add("Select explicit per-title Full mode: folder/image Emulators only excludes " + string.Join(", ", full) + ". A per-title default inherits the loader's default mode.");
            if (kernel.Length > 0)
                notes.Add("WARNING: this loader excludes libkernel.sprx even in Full mode: " + string.Join(", ", kernel));
            string title = r.TitleId.Length > 0 ? r.TitleId : "<TITLE_ID>";
            notes.Add($"For a controlled test, confirm backport_fakelib=1, auto_update_ampr=0, update_emulators=0 and global_fakelib_exclude={title}; fakelib mode must not be Disabled. These are tester settings, not settings applied or observed by Studio.");
            notes.Add("Confirm the selected directory: external backport/fakelib2, backport/fakelib, game/fakelib2, then game/fakelib. fakelib2 prevents global composition but remains filtered.");
            if (packageExcluded.Length > 0)
                notes.Add("Installed-package Emulators only uses a narrower allowlist and excludes " + string.Join(", ", packageExcluded) + "; confirm the package launch path separately.");
            if (libraries.Any(p => Name(p) is "libSceAmpr.sprx" or "libScePlayGo.sprx"))
                notes.Add("Installed PPR cleanup can remove Ampr/PlayGo from default /data/homebrew/backports/<TITLE_ID>/fakelib when app0 is the NSFS overlay over ppr_pfs. This is conditional; Full mode does not bypass that cleanup.");
            if (unassessed.Length > 0)
                notes.Add("WARNING: module errors leave these deployment dependencies unassessed: " + string.Join(", ", unassessed));
        }
        if (r.App0Files.Count > 0)
            notes.Add("Keep the selected libraries with their added app0 data: " + string.Join(", ", r.App0Files) + ". Hashes are in the successful-publication manifest.");
        if (libraries.Any(p => Name(p) == "libScePlayGo.sprx"))
            notes.Add("Preserve an existing playgo_stub.dat. It is optional for the supplied PlayGo stubs; Studio does not generate it.");
        var logs = new List<string>();
        if (r.Options.Contains("playgo_0_6_log") && r.ParamWarnings.Any(w => w.Contains(": fakelib/libScePlayGo.sprx from ", StringComparison.Ordinal)))
            logs.Add("/app0/playlgo.log");
        if (r.ParamWarnings.Any(w => w.StartsWith("AMPR debug logging enabled:", StringComparison.Ordinal)))
            logs.Add("/app0/ampr_emu.log");
        if (logs.Count > 0)
            notes.Add("Selected diagnostic log destinations (not input dependencies): " + string.Join(", ", logs));
        JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
        return new JsonObject
        {
            ["profile"] = "ShadowMountPlus 1.7beta5-fix1",
            ["profile_commit"] = "98c524890176c7b474fcb03387bec88c4a559861",
            ["console_loader"] = "unknown", ["console_settings"] = "unknown", ["launch_format"] = "unknown",
            ["inventory_scope"] = "planned merged-game module inventory; includes unchanged source files",
            ["fakelib_modules"] = Strings(libraries), ["full_mode_required_for"] = Strings(full),
            ["excluded_even_in_full"] = Strings(kernel), ["package_emulators_exclude"] = Strings(packageExcluded),
            ["unassessed_modules"] = Strings(unassessed), ["added_app0_data"] = Strings(r.App0Files),
            ["diagnostic_log_destinations"] = Strings(logs),
            ["hash_manifest"] = Publication.ManifestName, ["source_provenance"] = Publication.EvidenceName,
            ["notes"] = Strings(notes),
        };
    }
}
