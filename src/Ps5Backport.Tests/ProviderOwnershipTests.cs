using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class ProviderOwnershipTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ps5backport-providers-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData(false, Imports.Missing)]
    [InlineData(true, Imports.Provided)]
    public void DuplicateExport_IsProvidedOnlyWhenAnotherModuleOwnsIt(bool anotherProvider, string expected)
    {
        string game = Directory.CreateDirectory(Path.Combine(_root, "game")).FullName;
        File.WriteAllBytes(Path.Combine(game, "a.sprx"), SyntheticBinary.Elf(imports: true, exports: true));
        if (anotherProvider)
            File.WriteAllBytes(Path.Combine(game, "b.sprx"), SyntheticBinary.Elf(exports: true));
        var reference = new Reference(new HashSet<string>(), "synthetic exact", true);
        string db = Directory.CreateDirectory(Path.Combine(_root, "reference")).FullName;
        FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new()), Path.Combine(db, "4.00.json"));
        var result = Pipeline.Backport(game, Path.Combine(_root, "out"),
            new BackportOptions { Firmware = "4", ReferenceDir = db, KnownFixes = false, DryRun = true }, Knowledge.Default,
            cancel: TestContext.Current.CancellationToken);
        Assert.All(result.Modules, m => Assert.Empty(m.Error));
        Assert.Equal(expected, Assert.Single(result.Modules.Single(m => m.Path == "a.sprx").Statuses).Status);
        var libraryStatus = Assert.Single(FakelibStore.Check(game, reference).Single(m => m.Name == "a.sprx").Statuses);
        Assert.Equal(expected, libraryStatus.Status);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
