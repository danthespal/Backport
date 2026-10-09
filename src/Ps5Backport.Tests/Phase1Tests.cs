using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// Every sample module (GT7 USA backport, GT7 original, Control backport), loaded once: file kind, encrypted
/// segments, unwrapped ELF, re-signed SELF and the ELF model.
/// </summary>
public sealed class SampleSet
{
    public static readonly string[] SampleRoots =
        [Path.Combine("backports", "PPSA01317", "4xx+", "USA"), Path.Combine("backports", "PPSA01317", "default"), Path.Combine("backports", "PPSA34547", "4xx+")];

    public sealed record Sample(string Source, string Rel, string Kind, int Encrypted, byte[]? Elf);

    public IReadOnlyList<Sample> Samples { get; } = [];

    public SampleSet()
    {
        string original = Path.Combine(Repo.Root, ".original");
        Samples = SampleRoots
            .Select(r => Path.Combine(original, r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(f => Path.GetFileName(f).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(f =>
            {
                string kind = Self.FileKind(f);
                int encrypted = Self.IsSelf(kind) ? Self.EncryptedSegments(f) : 0;
                byte[]? elf = Self.IsSelf(kind) ? Self.Unwrap(File.ReadAllBytes(f)) : kind == Self.KindElf ? File.ReadAllBytes(f) : null;
                return new Sample(f, Path.GetRelativePath(original, f).Replace('\\', '/'), kind, encrypted, elf);
            })
            .ToList();
    }

    public void Need()
    {
        if (Samples.Count == 0)
            Assert.Skip("no sample games in .original/");
    }

    /// <summary>The ELF model as text: type, module, build path, program headers, import libraries, imports, exports, text segment.</summary>
    public static string Model(SceElf e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{e.Type} {e.ModuleName} {e.BuildPath}\n");
        foreach (var p in e.Phdrs)
            sb.Append($"phdr {p.Index} {p.Type} {p.Flags} {p.Offset} {p.Vaddr} {p.Filesz} {p.Memsz}\n");
        foreach (var (id, name) in e.ImportLibraries())
            sb.Append($"lib {id} {name}\n");
        foreach (var x in e.Imports())
            sb.Append($"import {x.Nid} {x.Library} {x.SymType} {x.GotVaddr} {x.Plt} {x.SymIndex} {x.DefinedLocally}\n");
        sb.Append("exports " + string.Join(" ", e.Exports().Order(StringComparer.Ordinal)) + "\n");
        var t = e.TextSegment();
        sb.Append($"text {t.Index} {t.Offset} {t.Filesz}\n");
        return sb.ToString();
    }
}

public class SelfAndElfTests(SampleSet set) : IClassFixture<SampleSet>
{
    /// <summary>Per module: kind, encrypted segments, SHA-256 of the unwrapped ELF, of the re-signed SELF and of the ELF model.</summary>
    [Fact]
    public void Kind_Unwrap_Sign_And_ElfModel_MatchGolden()
    {
        set.Need();
        var lines = set.Samples.Select(s => s.Elf is null
            ? $"{s.Rel} | {s.Kind} | {s.Encrypted}\n"
            : $"{s.Rel} | {s.Kind} | {s.Encrypted} | unwrap {Golden.Sha(s.Elf)} | sign {Golden.Sha(Self.Sign(s.Elf))} | model {Golden.Sha(SampleSet.Model(new SceElf(s.Elf)))}\n");
        Golden.Match("samples.txt", string.Concat(lines));
    }

    [Fact]
    public void UnwrapThenSign_ReproducesShippedPs4StyleSelfs_ExceptDigest()
    {
        set.Need();
        int checkedFiles = 0;
        foreach (var s in set.Samples.Where(s => s.Kind == Self.KindPs4Self))
        {
            byte[] original = File.ReadAllBytes(s.Source);
            byte[] again = Self.Sign(Self.Unwrap(original));
            int digest = Self.DigestOffset(original);
            Assert.Equal(original.Length, again.Length);
            original.AsSpan(digest, 32).Clear();
            again.AsSpan(digest, 32).Clear();
            Assert.True(original.AsSpan().SequenceEqual(again), $"round trip differs: {s.Source}");
            checkedFiles++;
        }
        Assert.True(checkedFiles > 0, "no PS4-style fake SELFs among the samples");
    }

    [Fact]
    public void EncryptedFlag_IsDetected_AndUnwrapRefuses()
    {
        set.Need();
        var sample = set.Samples.First(s => Self.IsSelf(s.Kind));
        byte[] self = File.ReadAllBytes(sample.Source);
        int count = BitConverter.ToUInt16(self, 0x18);
        for (int i = 0; i < count; i++)                      // forge: flag every data entry as encrypted
            if ((BitConverter.ToUInt64(self, 0x20 + i * 0x20) & (1 << 11)) != 0)
                self[0x20 + i * 0x20] |= (byte)Self.SegmentEncrypted;
        Assert.True(Self.EncryptedSegments(self) > 0);
        Assert.Throws<InvalidDataException>(() => Self.Unwrap(self));
    }

    [Fact]
    public void NotAnElf_IsRefused()
    {
        Assert.Throws<InvalidDataException>(() => new SceElf(new byte[0x40]));
        Assert.Throws<InvalidDataException>(() => Self.Sign(new byte[0x40]));
        Assert.Throws<InvalidDataException>(() => Self.Unwrap(new byte[0x40]));
    }
}

public class BpsTests
{
    static string BackPork => Repo.Need(".original", "reference", "BackPork", "patches");

    [Fact]
    public void Edits_MatchGolden_ForBackPorkPatches()
    {
        string root = BackPork;
        var files = Directory.EnumerateFiles(root, "*.bps", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        var sb = new System.Text.StringBuilder();
        foreach (string path in files)
        {
            var p = Bps.Load(path);
            sb.Append($"{Path.GetRelativePath(root, path).Replace('\\', '/')}:{p.SourceSize} {p.TargetSize} {p.SourceCrc} {p.TargetCrc} {p.Actions.Count}\n");
            foreach (var edit in Bps.Edits(p))
                sb.Append($"  {edit.Offset} {edit.Kind} {edit.Length} {Convert.ToHexStringLower(edit.Data)} {edit.FromOffset}\n");
        }
        Golden.Match("backpork-bps.txt", sb.ToString());
    }

    // --- synthetic patches (same cases as tests/test_phase3.py) -------------------
    static byte[] Varint(long v)
    {
        var bytes = new List<byte>();
        while (true)
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            if (v == 0)
            {
                bytes.Add((byte)(x | 0x80));
                return [.. bytes];
            }
            bytes.Add(x);
            v--;
        }
    }

    static byte[] MakePatch(byte[] source, byte[] target, (Bps.Command C, int Length, object? Arg)[] actions)
    {
        var body = new List<byte>("BPS1"u8.ToArray());
        body.AddRange(Varint(source.Length));
        body.AddRange(Varint(target.Length));
        body.AddRange(Varint(0));
        foreach (var (c, length, arg) in actions)
        {
            body.AddRange(Varint(((long)(length - 1) << 2) | (long)c));
            if (c == Bps.Command.TargetRead)
                body.AddRange((byte[])arg!);
            else if (c is Bps.Command.SourceCopy or Bps.Command.TargetCopy)
            {
                long d = (long)(int)arg!;
                body.AddRange(Varint((Math.Abs(d) << 1) | (d < 0 ? 1L : 0L)));
            }
        }
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(source)));
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(target)));
        body.AddRange(BitConverter.GetBytes(Crc32.Compute(body.ToArray())));
        return [.. body];
    }

    [Fact]
    public void AllFourActions_IncludingOverlappingTargetCopy()
    {
        byte[] source = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] target = (byte[])source.Clone();
        byte[] stub = [0x31, 0xC0, 0xC3, 0x90];
        stub.CopyTo(target, 8);
        Array.Copy(source, 20, target, 12, 4);
        for (int i = 16; i < 24; i += 2)
        {
            target[i] = source[22];
            target[i + 1] = source[23];
        }
        var patch = Bps.Parse(MakePatch(source, target,
        [
            (Bps.Command.SourceRead, 8, null), (Bps.Command.TargetRead, 4, stub), (Bps.Command.SourceCopy, 4, 20),
            (Bps.Command.TargetCopy, 8, 14), (Bps.Command.SourceRead, 8, null),
        ]));
        Assert.Equal(target, Bps.Apply(patch, source));
        Assert.Equal(new (long, string)[] { (8, "write"), (12, "source-copy"), (16, "target-copy") },
                     Bps.Edits(patch).Select(e => (e.Offset, e.Kind)).ToArray());
    }

    [Fact]
    public void WrongSource_IsRefused()
    {
        byte[] source = new byte[16];
        byte[] target = (byte[])source.Clone();
        target[0] = 1;
        var patch = Bps.Parse(MakePatch(source, target, [(Bps.Command.TargetRead, 1, new byte[] { 1 }), (Bps.Command.SourceRead, 15, null)]));
        var e = Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, Enumerable.Repeat((byte)2, 16).ToArray()));
        Assert.Contains("source does not match", e.Message);
    }

    [Fact]
    public void CorruptPatch_IsRefused()
    {
        byte[] data = MakePatch("ab"u8.ToArray(), "ab"u8.ToArray(), [(Bps.Command.SourceRead, 2, null)]);
        data[5] ^= 1;
        Assert.Contains("checksum", Assert.Throws<InvalidDataException>(() => Bps.Parse(data)).Message);
    }

    [Fact]
    public void Crc32_MatchesZlib()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));   // standard check value
        Assert.Equal(0u, Crc32.Compute([]));
    }
}

/// <summary>
/// Unusual trailing PT_SCE_VERSION blocks: two different versions in one record (Ghost of Yotei), appended data
/// (Borderlands 4), opaque bytes (God of War Sons of Sparta), and Kena's re-signed dump (zeroed, cut off,
/// build-path string). Unwraps are checked against golden hashes recorded from ps5bp.
/// </summary>
public class SceVersionTests
{
    static string Backports(params string[] rel) => Repo.Need([".original", "backports", .. rel]);

    static byte[] Version(SceElf e)
    {
        var p = e.Phdrs.First(p => p.Type == SceElf.PtSceVersion);
        return e.Data.AsSpan((int)p.Offset, (int)p.Filesz).ToArray();
    }

    [Theory]
    [InlineData("PPSA26344/default/eboot.bin")]                 // Yotei: records with differing versions
    [InlineData("PPSA01494/4xx+/prx/librenoircore.ps5.prx")]    // BL4: 0x1E8 bytes after the block
    [InlineData("PPSA28997/Media/Modules/PS5Util.prx")]         // GoW SoS: opaque block
    [InlineData("PPSA01746/default/prx/akdelay.prx")]           // Kena: zeroed block
    [InlineData("PPSA01746/default/sce_module/libc.prx")]       // Kena: cut off
    [InlineData("PPSA01746/default/sce_module/libSceFace.prx")]
    [InlineData("PPSA01746/4xx+/sce_module/libc.prx")]          // Kena: build-path string
    public void Unwrap_MatchesGolden_AndSignRoundTrips(string rel)
    {
        byte[] elf = Self.Unwrap(File.ReadAllBytes(Backports(rel.Split('/'))));
        Golden.Match(Path.Combine("unwrap", rel.Replace('/', '_') + ".sha256"), Golden.Sha(elf) + "\n");
        Assert.Equal(elf, Self.Unwrap(Self.Sign(elf)));
    }

    [Fact]
    public void AppendedData_VersionBlockIsFoundBeforeIt()
    {
        var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01494", "4xx+", "prx", "librenoircore.ps5.prx"))));
        Assert.Equal("libRenoirCore.PS5", elf.ModuleName);
        Assert.Equal(Convert.FromHexString("0000160008"), Version(elf)[..5]);   // first record: crti:
    }

    [Fact]
    public void OpaqueVersionBlock_IsTakenAsIs()
    {
        string path = Backports("PPSA28997", "Media", "Modules", "PS5Util.prx");
        var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(path)));
        byte[] file = File.ReadAllBytes(path);
        byte[] version = Version(elf);
        Assert.Equal("PS5Util", elf.ModuleName);
        Assert.Equal(file[^version.Length..], version);
    }

    [Fact]
    public void Kena_ZeroedCutOffAndBuildPathBlocks()
    {
        Assert.All(Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))))), b => Assert.Equal(0, b));
        // the re-signed libc ends at its last segment, 8 bytes short of its declared size
        Assert.All(Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "sce_module", "libc.prx"))))), b => Assert.Equal(0, b));
        Assert.Equal("Prospero_Release/libc.prx\0"u8.ToArray(),
                     Version(new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "4xx+", "sce_module", "libc.prx"))))));
    }

    /// <summary>.esbak = the decrypted original; the re-signed SELF differs only in header bytes after the phdr table.</summary>
    [Fact]
    public void Kena_SameCodeAsTheDecryptedBackup()
    {
        var ours = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))));
        var backup = SceElf.Load(Backports("PPSA01746", "default", "prx", "akdelay.prx.esbak"));
        var t = backup.TextSegment();
        Assert.True(ours.Data.AsSpan((int)t.Offset, (int)t.Filesz).SequenceEqual(backup.Data.AsSpan((int)t.Offset, (int)t.Filesz)));
    }

    [Fact]
    public void KenaDamagedRightSprx_IsRefused()
    {
        string prx = Backports("PPSA01746", "default", "sce_sys", "about", "right.sprx");
        var e = Assert.Throws<InvalidDataException>(() => Self.Unwrap(File.ReadAllBytes(prx)));
        Assert.Contains("points outside the file", e.Message);
    }

    /// <summary>A Wwise plugin whose module-param segment is all zeros has no SDK fields: read (0, 0), write nothing.</summary>
    [Fact]
    public void ModuleWithoutSdkFields_ReadsZero_WritesNothing()
    {
        var elf = new SceElf(Self.Unwrap(File.ReadAllBytes(Backports("PPSA01746", "default", "prx", "akdelay.prx"))));
        Assert.Equal((0u, 0u), Sdk.Read(elf));
        Assert.False(Sdk.Write(elf, 0x04000031, 0x09040001));
        Assert.Equal((0u, 0u), Sdk.Read(elf));
    }
}
