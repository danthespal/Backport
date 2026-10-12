namespace Ps5Backport.Tests
{
    using System.Text;
    using Ps5Backport.Core;

    public sealed class DynamicPsmlTests
    {
        [Theory]
        [InlineData("sceKernelLoadStartModule", "/system/common/lib/libScePsml.sprx", true)]
        [InlineData("sceKernelLoadStartModule", "libScePsml.prx", true)]
        [InlineData("sceKernelDlsym", "scePsmlMfsrInit", true)]
        [InlineData("sceKernelDlsym", "scePsmlMfsrInitKPN9", true)]
        [InlineData("sceKernelLoadStartModule", "libScePsml.sprx.bak", false)]
        [InlineData("sceKernelLoadStartModule", "unrelated_libScePsml.sprx", false)]
        [InlineData("sceKernelDlsym", "diagnostic scePsmlMfsrInit", false)]
        [InlineData("sceKernelDlsym", "libScePsml.sprx", false)]
        [InlineData("sceKernelLoadStartModule", "scePsmlMfsrInit", false)]
        [InlineData("sceSysmoduleLoadModule", "libScePsml.sprx", false)]
        [InlineData("unrelated", "libScePsml.sprx", false)]
        public void AnalyzeAndCook_AgreeOnExplicitDynamicEvidence(string importName, string literal, bool expected)
        {
            string root = Path.Combine(Path.GetTempPath(), "ps5backport-dynamic-psml-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
                string set = Directory.CreateDirectory(Path.Combine(root, "set")).FullName;
                string app0 = Directory.CreateDirectory(Path.Combine(set, "app0")).FullName;
                string reference = Directory.CreateDirectory(Path.Combine(root, "reference")).FullName;
                string output = Path.Combine(root, "output");
                byte[] module = SyntheticBinary.Elf(Encoding.ASCII.GetBytes(literal + "\0"), imports: true);
                Encoding.ASCII.GetBytes(Nid.Compute(importName) + "#A#A\0").CopyTo(module, 0x201);
                File.WriteAllBytes(Path.Combine(game, "eboot.bin"), module);
                File.WriteAllBytes(Path.Combine(set, "libScePsml.sprx"), SyntheticBinary.Elf("/app0/k9.psp\0"u8.ToArray(), exports: true));
                File.WriteAllBytes(Path.Combine(app0, "k9.psp"), [1, 2, 3]);
                var options = new BackportOptions { Firmware = "4", ReferenceDir = reference, ExtraFakelib = set, KnownFixes = false };
                var knowledge = new Knowledge(Paths.DataDir);

                var analysis = Pipeline.Backport(game, output, options with { DryRun = true }, knowledge,
                    cancel: TestContext.Current.CancellationToken);
                Assert.Equal(expected, analysis.App0Files.Contains("k9.psp"));
                Assert.Equal(expected, analysis.ParamWarnings.Any(w => w.StartsWith("possible dynamic Psml dependency")));
                Assert.False(Directory.Exists(output));

                var cooked = Pipeline.Backport(game, output, options, knowledge,
                    cancel: TestContext.Current.CancellationToken, analysis: analysis.Analysis);
                Assert.True(cooked.Published);
                Assert.Equal(analysis.App0Files, cooked.App0Files);
                Assert.Equal(expected, File.Exists(Path.Combine(output, "k9.psp")));
                if (expected)
                    Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(output, "k9.psp")));

                // Source files still take precedence even when a dynamic dependency is found.
                File.WriteAllBytes(Path.Combine(game, "k9.psp"), [9]);
                var existing = Pipeline.Backport(game, Path.Combine(root, "existing"), options, knowledge,
                    cancel: TestContext.Current.CancellationToken);
                Assert.Empty(existing.App0Files);
                Assert.False(File.Exists(Path.Combine(root, "existing", "k9.psp")));
                Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(game, "k9.psp")));
            }
            finally { Directory.Delete(root, recursive: true); }
        }
    }
}
