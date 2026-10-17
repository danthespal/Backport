namespace Ps5Backport.Core;

/// <summary>
/// The backport Studio would make, without writing it: a dry run of the eboot's game that keeps the patched eboot in
/// memory, as a <see cref="CodeImage"/> the code graph compares with the original (README "Code graph page").
/// </summary>
public static class CookPreview
{
    /// <summary>Folder handed to the run as its output; a dry run never creates or writes it.</summary>
    static readonly string Unused = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ps5backport-cook-preview");

    /// <summary>
    /// Dry-runs <paramref name="options"/> on the game folder holding <paramref name="eboot"/> and returns the patched eboot
    /// (named like the original, its fakelibs being the store set the run would add) with the run's result.
    /// </summary>
    public static (CodeImage Image, GameResult Result) Run(string eboot, BackportOptions options, Knowledge knowledge, NameDb names,
                                                           CancellationToken cancel = default)
    {
        string game = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(eboot)) ?? ".";
        string module = System.IO.Path.GetFileName(eboot);
        var result = Pipeline.Backport(game, Unused, options with { DryRun = true, KeepModule = module, Names = names }, knowledge, cancel: cancel);
        var mod = result.Modules.FirstOrDefault(m => string.Equals(m.Path, module, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"the dry run did not process {module}: is it in a game folder (sce_sys/param.json)?");
        if (mod.Error.Length > 0)
            throw new InvalidDataException($"{module}: {mod.Error}");
        if (mod.PatchedData is not { } data)
            throw new InvalidDataException($"the dry run kept no patched copy of {module}");
        string? fakelibs = result.FakelibSource.Length > 0 ? result.FakelibSource : null;
        return (new CodeImage(eboot, new SceElf(data), names, fakelibs), result);
    }
}
