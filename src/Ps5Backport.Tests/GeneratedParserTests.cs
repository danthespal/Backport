using FsCheck.Xunit;
using Ps5Backport.Core;

namespace Ps5Backport.Tests;

/// <summary>Bounded generated properties; FsCheck integrates with the existing xUnit v3 runner.</summary>
public sealed class GeneratedParserTests
{
    [Property(MaxTest = 200, EndSize = 64, Replay = "12345,67891")]
    public void FirmwareNames_RoundTrip(byte major, byte minor)
    {
        int m = major % 100, n = minor % 100;
        Assert.Equal((m, n), Firmware.Parse(Firmware.Name($"{m}.{n}")));
    }

    [Property(MaxTest = 200, EndSize = 64, Replay = "54321,67891")]
    public void FirmwareSdk_UsesDecimalDigitsInBcd(byte major, byte minor)
    {
        int m = 1 + major % 11, n = minor % 100;
        uint bcd = (uint)((n / 10) * 16 + n % 10);
        var pair = Firmware.SdkFor($"{m}.{n}");
        Assert.Equal((Firmware.SdkPairs[m].Ps5 & 0xFF00FFFF) | (bcd << 16), pair.Ps5);
        Assert.Equal(Firmware.SdkPairs[m].Ps4, pair.Ps4);
    }

    [Property(MaxTest = 200, EndSize = 64, Replay = "45678,67891")]
    public void Firmware_RejectsUnsupportedRanges(ushort value)
    {
        int invalid = 100 + value;
        Assert.Throws<FormatException>(() => Firmware.Parse($"{invalid}.00"));
        Assert.Throws<FormatException>(() => Firmware.Parse($"1.{invalid}"));
        Assert.Throws<ArgumentException>(() => Firmware.SdkFor($"{12 + value % 88}.00"));
    }

    [Theory]
    [InlineData("+1")]
    [InlineData("-0")]
    [InlineData("4.+1")]
    [InlineData("4. 1")]
    [InlineData("４.０１")]
    [InlineData("2147483648.0")]
    public void Firmware_RejectsNonCanonicalTokens(string text) => Assert.Throws<FormatException>(() => Firmware.Parse(text));

    [Fact]
    public void Firmware_WhitespaceAndMissingMinorRemainCompatible()
    {
        Assert.Equal((4, 0), Firmware.Parse(" 4 "));
        Assert.Equal((4, 0), Firmware.Parse("4."));
        Assert.Equal("0x0403000000000000", Firmware.ParamVersion("4.03"));
    }

    [Property(MaxTest = 200, EndSize = 64, Replay = "23456,67891")]
    public void BpsLiteral_RoundTripsAndRejectsCorruption(byte[] source, byte[] target)
    {
        source = source.Take(64).ToArray(); target = target.Take(64).ToArray();
        byte[] bytes = SyntheticBinary.LiteralPatch(source, target);
        Assert.Equal(target, Bps.Apply(Bps.Parse(bytes), source));
        bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => Bps.Parse(bytes));
    }

    [Property(MaxTest = 200, EndSize = 64, Replay = "34567,67891")]
    public void ElfSelf_RoundTripsAndRejectsTruncation(byte[] payload, byte truncate)
    {
        byte[] elf = SyntheticBinary.Elf(payload.Take(64).ToArray());
        byte[] self = Self.Sign(elf);
        Assert.Equal(elf, Self.Unwrap(self));
        Assert.Equal(Self.KindPs4Self, Self.Kind(self));
        Assert.Equal(0, Self.EncryptedSegments(self));
        Assert.Throws<InvalidDataException>(() => new SceElf(elf.Take(truncate % 64).ToArray()));
        Assert.Throws<InvalidDataException>(() => Self.Unwrap(self.Take(truncate % 32).ToArray()));
    }
}
