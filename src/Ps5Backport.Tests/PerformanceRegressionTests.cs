using System.Security.Cryptography;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public void Signing_AllocatesOutputWithoutCopyingEachSegment_AndLeavesInputUnchanged()
    {
        byte[] input = SyntheticBinary.Elf(new byte[16 * 1024 * 1024], imports: true);
        byte[] before = SHA256.HashData(input);
        // Warm cryptographic initialization separately from the allocation budget.
        _ = Self.Sign(SyntheticBinary.Elf());
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        byte[] signed = Self.Sign(input);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.InRange(bytes, signed.Length, signed.Length + 1024 * 1024L);
        Assert.Equal(before, SHA256.HashData(input));
        Assert.Equal(input, Self.Unwrap(signed));
    }

    [Theory]
    [InlineData("4", "4.00", true)]
    [InlineData("4.50", "4.00", false)]
    public void RunOwnedFirmwareMetadata_PreservesSelectionAndDoesNotReadAgain(string target, string selected, bool exact)
    {
        string root = Path.Combine(Path.GetTempPath(), "ps5backport-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "4.json");
            FirmwareDb.Save(new FirmwareExports("4", "synthetic", false, new() { ["lib"] = ["AAAAAAAAAAA"] }), file);
            var loaded = FirmwareDb.LoadAll(root);
            File.WriteAllText(file, "not JSON");
            var reference = Imports.LoadReference(root, target, loaded);
            Assert.Equal(exact, reference.Exact);
            Assert.Contains("firmware_db " + selected, reference.Label);
            Assert.Contains("AAAAAAAAAAA", reference.Nids);
            Assert.ThrowsAny<Exception>(() => Imports.LoadReference(root, target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
