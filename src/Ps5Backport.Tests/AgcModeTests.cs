namespace Ps5Backport.Tests
{
    using Ps5Backport.Core;

    /// <summary>Option agc_mode2 (FINDINGS 6u): the store's libSceAgc variant runs Sony's AcquireMem workaround mode 2.</summary>
    [Trait("Lane", "Private")]
    public sealed class AgcModeTests
    {
        const int ModeSelect = 0xcc23;   // file offset of VA 0x8c23 in both store builds (J03828141, J03718109)

        [Fact]
        public void Option_SelectsTheAgcMode2Variant()
        {
            var option = new Knowledge(Repo.Need("data")).Options["agc_mode2"];
            Assert.Equal(("Libraries", "agc_mode2", OptionProbe.KindConsole), (option.Group, option.FakelibVariant, option.Probe.Kind));
            Assert.Empty(option.Fixes);
            Assert.Empty(option.Patterns);
        }

        /// <summary>The variant differs from its set's (mode 0) AGC only in the mode selection: 'mov eax, esi; nop' -> 'push 2; pop rax'.</summary>
        [Theory]
        [InlineData("4")]
        [InlineData("4.03")]
        public void Variant_ChangesOnlyTheModeSelection(string set)
        {
            byte[] mode0 = Self.Unwrap(File.ReadAllBytes(Repo.Need("fakelibs", set, "libSceAgc.sprx")));
            byte[] mode2 = Self.Unwrap(File.ReadAllBytes(Repo.Need("fakelibs", set, "agc_mode2", "libSceAgc.sprx")));
            Assert.Equal(mode0.Length, mode2.Length);
            Assert.Equal([ModeSelect, ModeSelect + 1, ModeSelect + 2], Enumerable.Range(0, mode0.Length).Where(i => mode0[i] != mode2[i]));
            Assert.Equal("89f090", Convert.ToHexStringLower(mode0.AsSpan(ModeSelect, 3)));
            Assert.Equal("6a0258", Convert.ToHexStringLower(mode2.AsSpan(ModeSelect, 3)));
            Assert.Equal("81fa00000004", Convert.ToHexStringLower(mode2.AsSpan(ModeSelect - 6, 6)));   // cmp edx, 0x4000000 (SDK-major-4 check)
        }
    }
}
