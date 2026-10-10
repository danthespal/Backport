using System.Diagnostics;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Local deterministic mutation corpus, isolated in a child process with finite resource budgets.</summary>
public sealed class ParserBudgetTests
{
    const int MaximumBytes = 64 * 1024, Cases = 512;

    [Theory]
    [InlineData("elf")]
    [InlineData("self")]
    [InlineData("bps")]
    public async Task MutatedReaders_StayWithinBudgets(string format)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ParserBudgetTests).Assembly.Location);
        start.ArgumentList.Add("-method"); start.ArgumentList.Add("Ps5Backport.Tests.ParserBudgetTests.MutationWorker");
        start.Environment["PS5BP_FUZZ_FORMAT"] = format;
        start.Environment["DOTNET_GCHeapHardLimit"] = "10000000"; // hexadecimal: 256 MiB managed heap, runtime-enforced
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.True(process.ExitCode == 0, $"{format} mutation worker failed:\n{await output}\n{await error}");
    }

    [Fact]
    public void MutationWorker()
    {
        string? format = Environment.GetEnvironmentVariable("PS5BP_FUZZ_FORMAT");
        if (format is null) return;
        Assert.Contains(format, new[] { "elf", "self", "bps" });
        byte[] seed = format switch
        {
            "elf" => SyntheticBinary.Elf(imports: true, exports: true),
            "self" => Self.Sign(SyntheticBinary.Elf()),
            _ => SyntheticBinary.LiteralPatch([1, 2, 3], [4, 5, 6]),
        };
        var random = new Random(305419896);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < Cases; i++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            byte[] data = seed.ToArray();
            int at = random.Next(data.Length);
            switch (i % 4)
            {
                case 0: data[at] ^= (byte)(1 << random.Next(8)); break;
                case 1: data = data.Take(at).ToArray(); break;
                case 2:
                    at = Math.Min(at, data.Length - 8);
                    SyntheticBinary.W64(data, at, i % 3 == 0 ? ulong.MaxValue : (ulong)MaximumBytes + 1);
                    break;
                default: data = [.. data, .. new byte[random.Next(64)]]; break;
            }
            if (format == "bps" && data.Length >= 16)
                SyntheticBinary.W32(data, data.Length - 4, Crc32.Compute(data.AsSpan(0, data.Length - 4)));
            Assert.InRange(data.Length, 0, MaximumBytes);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                if (format == "bps")
                    _ = Bps.Apply(Bps.Parse(data), [1, 2, 3], MaximumBytes);
                else if (format == "self")
                {
                    _ = Self.EncryptedSegments(data); _ = Self.DigestOffset(data);
                    _ = new SceElf(Self.Unwrap(data, MaximumBytes));
                }
                else
                {
                    var elf = new SceElf(data);
                    _ = elf.Symbols(); _ = elf.Imports(); _ = elf.Exports(); _ = elf.ModuleName; _ = elf.BuildPath;
                    _ = Sdk.Read(elf);
                    _ = Imports.Classify(elf, new HashSet<string>(), exact: true);
                }
            }
            catch (InvalidDataException) { /* Expected rejection of a malformed or over-budget file. */ }
            catch (Exception e) { Assert.Fail($"{format} seed=305419896 case={i}: {e}"); }
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated <= 8 * 1024 * 1024, $"{format} case={i}: allocation budget");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"{format} case={i}: time budget");
        }
    }

    [Fact]
    public void OutputLimits_RejectBeforeLargeAllocation()
    {
        byte[] self = Self.Sign(SyntheticBinary.Elf());
        int elfAt = 0x20 + BitConverter.ToUInt16(self, 0x18) * 0x20;
        SyntheticBinary.W64(self, elfAt + 0x40 + 8, 1024 * 1024);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => Self.Unwrap(self, MaximumBytes));
        var patch = new Bps.Patch(0, 1024 * 1024, [], [], 0, 0);
        Assert.Throws<InvalidDataException>(() => Bps.Apply(patch, [], MaximumBytes));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocated < MaximumBytes * 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(31)]
    public void TruncatedSelfMetadata_IsADataError(int size)
    {
        byte[] self = Self.Sign(SyntheticBinary.Elf()).Take(size).ToArray();
        Assert.Throws<InvalidDataException>(() => Self.EncryptedSegments(self));
        Assert.Throws<InvalidDataException>(() => Self.DigestOffset(self));
    }
}
