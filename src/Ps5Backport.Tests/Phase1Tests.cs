using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>
/// Runs ps5bp once over every sample module (GT7 USA backport, GT7 NOT_BACKPORT, Control backport):
/// file kind, encrypted segments, unwrap, ELF model, re-sign. Phase 1 tests compare C# with it.
/// </summary>
public sealed class SampleOracle : IDisposable
{
    public static readonly string[] SampleRoots =
        [Path.Combine("GT7", "USA"), Path.Combine("GT7", "NOT_BACKPORT"), Path.Combine("CONTROLResonant", "backport")];

    public sealed record ModuleModel(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("module")] string? Module,
        [property: JsonPropertyName("build")] string? Build,
        [property: JsonPropertyName("phdrs")] long[][] Phdrs,
        [property: JsonPropertyName("libs")] Dictionary<string, string> Libraries,
        [property: JsonPropertyName("imports")] JsonElement[][] Imports,
        [property: JsonPropertyName("exports")] string[] Exports,
        [property: JsonPropertyName("text")] long[] Text);

    public sealed record Sample(
        [property: JsonPropertyName("src")] string Source,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("encrypted")] int Encrypted,
        [property: JsonPropertyName("elf")] string? Elf,
        [property: JsonPropertyName("signed")] string? Signed,
        [property: JsonPropertyName("model")] ModuleModel? Model);

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "ps5backport-oracle-" + Guid.NewGuid().ToString("N"));
    public IReadOnlyList<Sample> Samples { get; } = [];
    public string? SkipReason { get; }

    public SampleOracle()
    {
        var files = SampleRoots
            .Select(r => Path.Combine(Oracle.Root, ".original", r))
            .Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*", SearchOption.AllDirectories))
            .Where(f => Path.GetFileName(f).Equals("eboot.bin", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
        {
            SkipReason = "no sample games in .original/";
            return;
        }
        Directory.CreateDirectory(Folder);
        string list = Path.Combine(Folder, "files.json");
        File.WriteAllText(list, JsonSerializer.Serialize(files));
        try
        {
            string json = Oracle.Run($$"""
                import json, os
                from ps5bp import fself
                from ps5bp.elf import SceElf
                folder = {{JsonSerializer.Serialize(Folder)}}
                out = []
                for i, src in enumerate(json.load(open(os.path.join(folder, 'files.json')))):
                    kind = fself.file_kind(src)
                    item = {'src': src, 'kind': kind, 'encrypted': 0, 'elf': None, 'signed': None, 'model': None}
                    elf_path = None
                    if kind in fself.SELF_MAGICS.values():
                        item['encrypted'] = fself.encrypted_segments(src)
                        elf_path = os.path.join(folder, f'{i}.elf')
                        fself.unwrap(src, elf_path)
                    elif kind == 'ELF':
                        elf_path = src
                    if elf_path:
                        item['elf'] = elf_path
                        e = SceElf.load(elf_path)
                        t = e.text_segment()
                        item['model'] = {
                            'type': e.type, 'module': e.module_name, 'build': e.build_path,
                            'phdrs': [[p.index, p.type, p.flags, p.offset, p.vaddr, p.filesz, p.memsz] for p in e.phdrs],
                            'libs': {str(k): v for k, v in e.import_libraries().items()},
                            'imports': [[x.nid, x.library, x.sym_type, x.got_vaddr, x.plt, x.sym_index, x.defined_locally] for x in e.imports()],
                            'exports': sorted(e.exports()),
                            'text': [t.index, t.offset, t.filesz],
                        }
                        item['signed'] = os.path.join(folder, f'{i}.sself')
                        fself.sign(elf_path, item['signed'])
                    out.append(item)
                print(json.dumps(out))
                """);
            Samples = JsonSerializer.Deserialize<List<Sample>>(json)!;
        }
        catch (Exception e) when (e.GetType().Name.Contains("Skip"))
        {
            SkipReason = e.Message;
        }
    }

    public void Need()
    {
        if (SkipReason is not null)
            Assert.Skip(SkipReason);
    }

    public void Dispose()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);
    }
}

public class SelfAndElfTests(SampleOracle oracle) : IClassFixture<SampleOracle>
{
    [Fact]
    public void Kind_And_EncryptedSegments_MatchPython()
    {
        oracle.Need();
        Assert.NotEmpty(oracle.Samples);
        foreach (var s in oracle.Samples)
        {
            Assert.Equal(s.Kind, Self.FileKind(s.Source));
            if (Self.IsSelf(s.Kind))
                Assert.Equal(s.Encrypted, Self.EncryptedSegments(s.Source));
        }
    }

    [Fact]
    public void Unwrap_IsByteIdenticalToPython()
    {
        oracle.Need();
        foreach (var s in oracle.Samples.Where(s => Self.IsSelf(s.Kind)))
            Assert.True(File.ReadAllBytes(s.Elf!).AsSpan().SequenceEqual(Self.Unwrap(File.ReadAllBytes(s.Source))),
                        $"unwrap differs: {s.Source}");
    }

    [Fact]
    public void Sign_IsByteIdenticalToPython()
    {
        oracle.Need();
        foreach (var s in oracle.Samples.Where(s => s.Signed is not null))
            Assert.True(File.ReadAllBytes(s.Signed!).AsSpan().SequenceEqual(Self.Sign(File.ReadAllBytes(s.Elf!))),
                        $"sign differs: {s.Source}");
    }

    [Fact]
    public void UnwrapThenSign_ReproducesShippedPs4StyleSelfs_ExceptDigest()
    {
        oracle.Need();
        int checkedFiles = 0;
        foreach (var s in oracle.Samples.Where(s => s.Kind == Self.KindPs4Self))
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
    public void ElfModel_MatchesPython()
    {
        oracle.Need();
        foreach (var s in oracle.Samples.Where(s => s.Model is not null))
        {
            var m = s.Model!;
            var e = SceElf.Load(s.Elf!);
            string where = s.Source;
            Assert.Equal(m.Type, e.Type);
            Assert.Equal(m.Module, e.ModuleName);
            Assert.Equal(m.Build, e.BuildPath);
            Assert.Equal(m.Phdrs, e.Phdrs.Select(p => new[] { p.Index, p.Type, p.Flags, p.Offset, (long)p.Vaddr, p.Filesz, p.Memsz }).ToArray());
            Assert.Equal(m.Libraries, e.ImportLibraries().ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
            var imports = e.Imports();
            Assert.True(m.Imports.Length == imports.Count, $"import count differs: {where}");
            for (int i = 0; i < imports.Count; i++)
            {
                var x = imports[i];
                var p = m.Imports[i];
                Assert.Equal((p[0].GetString(), p[1].GetString(), p[2].GetInt32(), p[3].GetUInt64(), p[4].GetBoolean(), p[5].GetInt32(), p[6].GetBoolean()),
                             (x.Nid, x.Library, x.SymType, x.GotVaddr, x.Plt, x.SymIndex, x.DefinedLocally));
            }
            Assert.Equal(m.Exports, e.Exports().Order(StringComparer.Ordinal).ToArray());
            var t = e.TextSegment();
            Assert.Equal(m.Text, new long[] { t.Index, t.Offset, t.Filesz });
        }
    }

    [Fact]
    public void EncryptedFlag_IsDetected_AndUnwrapRefuses()
    {
        oracle.Need();
        var sample = oracle.Samples.First(s => Self.IsSelf(s.Kind));
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
    static string BackPork => Oracle.Need(".original", "reference", "BackPork", "patches");

    [Fact]
    public void Edits_MatchPython_ForBackPorkPatches()
    {
        string root = BackPork;
        var files = Directory.EnumerateFiles(root, "*.bps", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        var expected = Oracle.Json<Dictionary<string, JsonElement>>($$"""
            import json
            from ps5bp import bps
            out = {}
            for path in json.loads({{JsonSerializer.Serialize(JsonSerializer.Serialize(files))}}):
                p = bps.load(path)
                out[path] = {'head': [p.source_size, p.target_size, p.source_crc, p.target_crc, len(p.actions)],
                             'edits': [[e.offset, e.kind, e.length, e.data.hex(), e.from_offset] for e in bps.edits(p)]}
            print(json.dumps(out))
            """);
        foreach (string path in files)
        {
            var p = Bps.Load(path);
            var want = expected[path];
            Assert.Equal(want.GetProperty("head").EnumerateArray().Select(v => v.GetInt64()).ToArray(),
                         new[] { p.SourceSize, p.TargetSize, p.SourceCrc, p.TargetCrc, (long)p.Actions.Count });
            var edits = Bps.Edits(p);
            var wantEdits = want.GetProperty("edits").EnumerateArray().ToArray();
            Assert.Equal(wantEdits.Length, edits.Count);
            for (int i = 0; i < edits.Count; i++)
            {
                var w = wantEdits[i].EnumerateArray().ToArray();
                Assert.Equal((w[0].GetInt64(), w[1].GetString(), w[2].GetInt64(), w[3].GetString(), w[4].GetInt64()),
                             (edits[i].Offset, edits[i].Kind, edits[i].Length, Convert.ToHexStringLower(edits[i].Data), edits[i].FromOffset));
            }
        }
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
