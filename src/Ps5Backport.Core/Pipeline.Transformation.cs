namespace Ps5Backport.Core;

public static partial class Pipeline
{
    sealed record Providers(List<(ModuleResult Mod, string Src, SceElf Elf)> Loaded, Dictionary<string, int> Counts);

    static Providers LoadProviders(List<(string Rel, string Src, bool Added)> modules, GameResult result,
        BackportOptions o, Knowledge knowledge, IProgress<BackportProgress>? progress, CancellationToken cancel, DiagnosticRun trace)
    {
        // 1) unwrap everything, so every module's exports can serve as providers
        var loaded = new List<(ModuleResult Mod, string Src, SceElf Elf)>();
        for (int i = 0; i < modules.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var (rel, file, added) = modules[i];
            var mod = new ModuleResult(rel.Replace(System.IO.Path.DirectorySeparatorChar, '/'), Self.FileKind(file), added);
            result.Modules.Add(mod);
            progress?.Report(new BackportProgress(i, modules.Count, mod.Path, "loading"));
            try
            {
                byte[] data;
                if (mod.Container == Self.KindElf)
                    data = File.ReadAllBytes(file);
                else if (Self.IsSelf(mod.Container))
                {
                    int encrypted = Self.EncryptedSegments(file);
                    if (encrypted > 0)
                        throw new InvalidDataException($"{encrypted} encrypted SELF segment(s): this is a retail-encrypted file; " +
                                                       "decrypt the game on a console first (PS5-SELF-Decrypter) and use that dump");
                    data = Self.Unwrap(File.ReadAllBytes(file));
                }
                else
                    throw new InvalidDataException("unknown file format");
                loaded.Add((mod, file, new SceElf(data) { AllowTextExtension = o.ExtendText }));
            }
            catch (Exception e) when (IsModuleError(e))
            {
                trace.ModuleError("loading", mod.Path, e);
                if (mod.Path.Equals(RightsModule, StringComparison.OrdinalIgnoreCase))
                {
                    // not needed to run: a tested backport shipped it zeroed and others leave it out; the overlay keeps
                    // the dump's own, so an unreadable one is a warning, not a module error
                    result.Modules.Remove(mod);
                    result.ParamWarnings.Add($"{mod.Path} could not be read ({e.Message}); left out of the backport, the dump's " +
                                             "own copy stays in use (the game does not need it to start)");
                }
                else
                    mod.Error = e.Message;
            }
        }

        var provided = loaded.SelectMany(l => l.Elf.Exports()).GroupBy(n => n, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        CheckPsvr2(loaded.Select(l => (l.Mod, l.Elf)), result);

        // the game's own fakelibs (not replaced by the store or --fakelib): known-broken homebrew builds
        foreach (var (mod, _, elf) in loaded)
            if (!mod.Added && mod.Path.StartsWith("fakelib/", StringComparison.OrdinalIgnoreCase)
                && FakelibStore.BuildDateOf(elf) is { } date && knowledge.Build(elf.ModuleName, date) is { State: "broken" } build)
                result.ParamWarnings.Add($"{mod.Path} ({elf.ModuleName} built {date}) is a known-broken build: {build.Note}");

        return new Providers(loaded, provided);
    }

    static bool IsModuleError(Exception e) => e is PatchException or InvalidDataException or IOException or ArgumentException;

    sealed record PatchContext(BackportOptions Options, Knowledge Knowledge, IReadOnlyDictionary<string, Fix> Fixes,
        List<FixPattern> Patterns, Reference Reference, IReadOnlyDictionary<string, int> Providers,
        List<FirmwareExports> NewerDbs, uint TargetPs5, uint TargetPs4, CancellationToken Cancel);

    static void PatchModule(ModuleResult mod, SceElf elf, string file, string output, PatchContext context)
    {
        var (o, k, fixes, patterns, reference, allProvided, newerDbs, targetPs5, targetPs4, cancel) = context;
        cancel.ThrowIfCancellationRequested();
        bool changed = false;
        var (ps5, ps4) = Sdk.Read(elf);
        mod.SdkBefore = $"0x{ps5:x8}/0x{ps4:x8}";
        if (ps5 > targetPs5)
        {
            changed |= Sdk.Write(elf, targetPs5, targetPs4);
            mod.SdkPatched = true;
        }
        (ps5, ps4) = Sdk.Read(elf);
        mod.SdkAfter = $"0x{ps5:x8}/0x{ps4:x8}";
        mod.SonyBuild = k.SonyBuild(elf.BuildPath);

        var ownExports = elf.Exports();
        var provided = allProvided.Where(p => p.Value > (ownExports.Contains(p.Key) ? 1 : 0))
            .Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var statuses = Imports.Classify(elf, reference.Nids, provided, reference.Exact);
        var imported = statuses.Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);
        var unknown = statuses.Where(s => Imports.IsUnresolved(s.Status)).Select(s => s.Nid).ToList();
        var unknownPlt = statuses.Where(s => Imports.IsUnresolved(s.Status) && s.Plt).Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);

        // 1) known fixes (Fixes + selected options): for missing imports, plus argument fixes ('always') for
        //    functions that exist on old firmware but reject new values
        var untouched = statuses.Where(s => s.Plt && s.Status is Imports.Reference or Imports.Provided or Imports.Unknown or Imports.Missing)
                                .Select(s => s.Nid).ToHashSet(StringComparer.Ordinal);
        var known = statuses.Select(s => s.Nid).Where(n =>
            o.KnownFixes && fixes.TryGetValue(n, out var f) && FixApplier.Applicable(elf, n, k, fixes) && f.Due(o.Firmware)
            && ((f.When == Fix.WhenMissing && unknownPlt.Contains(n)) || (f.When == Fix.WhenAlways && untouched.Contains(n)))).ToList();
        foreach (var (nid, how) in FixApplier.Apply(elf, known.Where(n => fixes[n].Kind != Fix.KindStub).ToList(), k, fixes))
        {
            cancel.ThrowIfCancellationRequested();
            var f = fixes[nid];
            mod.Fixed.Add(new FixedEntry { Nid = nid, Name = f.Name, How = how, Source = string.Join(", ", f.Sources), Proven = f.Proven });
            changed = true;
        }

        // 2) stubs: known stub fixes + requested NIDs + (optionally) every other unknown import
        var toStub = known.Where(n => fixes[n].Kind == Fix.KindStub).ToList();
        if (o.CandidateFixes)   // reference-derived (AnyPS5 silent stubs), opt-in
            toStub.AddRange(unknown.Where(n => unknownPlt.Contains(n) && k.Candidates.ContainsKey(n) && !fixes.ContainsKey(n)));
        toStub.AddRange(o.StubNids.Where(imported.Contains));
        if (o.StubUnknown)
            toStub.AddRange(unknown.Where(n => (unknownPlt.Contains(n) || o.DefineSymbol)
                                               && !(fixes.TryGetValue(n, out var f) && f.Kind != Fix.KindStub && known.Contains(n))));
        toStub = toStub.Distinct(StringComparer.Ordinal)
                       .Where(n => !statuses.Any(s => s.Nid == n && s.Status == Imports.Stubbed)).ToList();
        if (toStub.Count > 0)
        {
            var rules = FixApplier.StubRules(fixes);
            foreach (string n in toStub)
                if (k.Candidates.TryGetValue(n, out var c) && !fixes.ContainsKey(n))
                    rules[n] = $"ret:0x{c.Value:x}";
            foreach (var p in Patching.StubImports(elf, toStub, k, rules, o.DefineSymbol))
            {
                cancel.ThrowIfCancellationRequested();
                string origin = fixes.TryGetValue(p.Nid, out var f) ? (f.Proven ? "known fix, proven" : "known fix, derived")
                              : k.Candidates.ContainsKey(p.Nid) ? "candidate (AnyPS5), derived" : "requested";
                mod.StubbedNow.Add(new StubbedEntry
                {
                    Nid = p.Nid, Name = Name(o.Names, p.Nid), Stub = p.Stub, Got = $"0x{p.GotVaddr:x}", At = $"0x{p.StubVaddr:x}",
                    KnownFix = fixes.ContainsKey(p.Nid), Origin = origin,
                });
            }
            changed = true;
        }
        if (o.KnownFixes && o.Options.Contains(UnitySkuFallback.Option, StringComparer.Ordinal) && UnitySkuFallback.Apply(elf, cancel) > 0)
        {
            mod.Patterns.Add(new PatternEntry { Name = UnitySkuFallback.Name, Count = 1 });
            changed = true;
        }
        if (o.KnownFixes && o.Options.Contains(UbisoftOfflinePopups.Option, StringComparer.Ordinal))
        {
            int count = UbisoftOfflinePopups.Apply(elf, cancel);
            if (count > 0)
            {
                mod.Patterns.Add(new PatternEntry { Name = UbisoftOfflinePopups.Name, Count = count });
                changed = true;
            }
        }
        if (o.KnownFixes && o.Options.Contains(BootNotice.Option, StringComparer.Ordinal))
        {
            int count = BootNotice.Apply(elf, o.BootNoticeMessage ?? "", o.BootNoticeDelaySeconds, cancel);
            if (count > 0)
            {
                mod.Patterns.Add(new PatternEntry { Name = BootNotice.Name, Count = count });
                changed = true;
            }
        }
        foreach (var pattern in patterns)
        {
            cancel.ThrowIfCancellationRequested();
            int count = FixApplier.ApplyPattern(elf, pattern, cancel);
            if (count > 0)
            {
                mod.Patterns.Add(new PatternEntry { Name = pattern.Name, Count = count });
                changed = true;
            }
        }
        if (changed)
            statuses = Imports.Classify(elf, reference.Nids, provided, reference.Exact);

        foreach (var s in statuses)
        {
            int at = mod.Counts.FindIndex(kv => kv.Key == s.Status);
            if (at < 0)
                mod.Counts.Add(new(s.Status, 1));
            else
                mod.Counts[at] = new(s.Status, mod.Counts[at].Value + 1);
        }
        var unresolved = statuses.Where(s => Imports.IsUnresolved(s.Status)).ToList();
        var refs = unresolved.Count > 0 ? Imports.CodeReferences(elf, unresolved, cancel) : [];
        foreach (var s in unresolved)
        {
            var intro = FirmwareDb.Introduced(s.Nid, newerDbs, k);
            mod.Unknown.Add(new UnresolvedEntry
            {
                Nid = s.Nid, Name = Name(o.Names, s.Nid), Library = s.Library,
                Candidate = k.Candidates.TryGetValue(s.Nid, out var c) ? $"return 0x{c.Value:x8} ({c.Source})" : "",
                Status = s.Status, CodeRefs = refs[s.Nid],
                LeftBy = string.Join(", ", k.LeftUnpatched.GetValueOrDefault(s.Nid) ?? []),
                Introduced = intro is { } x ? $"{x.Firmware} / {x.Source}" : "",
            });
        }

        cancel.ThrowIfCancellationRequested();
        mod.TextExtended = elf.TextExtended;
        mod.Statuses = statuses;
        if (o.DryRun)
            return;
        string dest = System.IO.Path.Combine(output, mod.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (changed)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, Self.Sign(elf.Data));   // always a fake-signed SELF: the console loads SELFs, also for a decrypted (plain ELF) dump
            mod.Written = true;
        }
        else if (mod.Added || mod.PathCaseCorrected)   // unchanged fakelib or renamed module: keep its container
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file));
            mod.Written = true;
        }
    }

    static string Name(NameDb? names, string nid) => names?.Name(nid) ?? nid;

}
