using System.Buffers.Binary;
using System.Text;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>AMPRIDX3 output follows the upstream format and represents the mounted game plus overlay view.</summary>
public sealed class AmprIndexTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ps5backport-ampr-index-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Build_MergesOverlayAndOmitsGeneratedOrPrivateFiles()
    {
        string game = Path.Combine(root, "game");
        string overlay = Path.Combine(root, "overlay");
        Directory.CreateDirectory(Path.Combine(game, "data"));
        Directory.CreateDirectory(overlay);
        File.WriteAllBytes(Path.Combine(game, "data", "base.bin"), [1, 2]);
        File.WriteAllBytes(Path.Combine(game, "same.bin"), [1]);
        File.WriteAllBytes(Path.Combine(game, "ampr_emu.index"), [0]);
        File.WriteAllBytes(Path.Combine(overlay, "same.bin"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(overlay, "backport_report.txt"), "private");
        File.WriteAllText(Path.Combine(overlay, "ampr_emu.log"), "diagnostic");

        AmprIndexResult result = AmprIndex.Build(game, overlay);

        Assert.Equal(2, result.Entries);
        Assert.Equal(1, result.OverlayEntries);
        byte[] data = File.ReadAllBytes(result.Path);
        Assert.Equal("AMPRIDX3", Encoding.ASCII.GetString(data, 0, 8));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        Assert.Equal(2UL, BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16)));

        ulong pathBytes = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(24));
        int pathsOffset = checked(48 + 2 * 24);
        string paths = Encoding.UTF8.GetString(data, pathsOffset, checked((int)pathBytes));
        Assert.Contains("/app0/data/base.bin\0", paths);
        Assert.Contains("/app0/same.bin\0", paths);
        Assert.DoesNotContain("backport_report", paths);
        Assert.DoesNotContain("ampr_emu.index", paths);
        Assert.DoesNotContain("ampr_emu.log", paths);

        ulong firstSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48 + 8));
        ulong secondSize = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(48 + 24 + 8));
        Assert.Equal(new ulong[] { 2, 4 }, new[] { firstSize, secondSize });
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
