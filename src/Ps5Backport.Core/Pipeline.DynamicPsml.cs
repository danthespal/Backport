using System.Text;

namespace Ps5Backport.Core;

public static partial class Pipeline
{
    static readonly string LoadStartModuleNid = Nid.Compute("sceKernelLoadStartModule");
    static readonly string DlsymNid = Nid.Compute("sceKernelDlsym");

    /// <summary>Conservative static evidence, not a proof that the loading/lookup call executes.</summary>
    static string? DynamicPsmlEvidence(SceElf elf)
    {
        var imports = elf.Imports().Select(i => i.Nid).ToHashSet(StringComparer.Ordinal);
        if (imports.Contains(LoadStartModuleNid))
            foreach (string name in new[] { "libScePsml.sprx", "libScePsml.prx" })
            {
                int offset = 0;
                byte[] literal = Encoding.ASCII.GetBytes(name + "\0");
                while (offset < elf.Data.Length)
                {
                    int hit = elf.Data.AsSpan(offset).IndexOf(literal);
                    if (hit < 0) break;
                    int at = offset + hit;
                    if (at == 0 || elf.Data[at - 1] is 0 or (byte)'/')
                        return $"sceKernelLoadStartModule import and {name} path literal";
                    offset = at + literal.Length;
                }
            }
        if (imports.Contains(DlsymNid))
        {
            int offset = 0;
            while (offset < elf.Data.Length)
            {
                int hit = elf.Data.AsSpan(offset).IndexOf("scePsml"u8);
                if (hit < 0) break;
                int at = offset + hit, end = at + 7;
                while (end < elf.Data.Length && end - at < 128 &&
                    (elf.Data[end] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'_'))
                    end++;
                if ((at == 0 || elf.Data[at - 1] == 0) && end > at + 7 && end < elf.Data.Length && elf.Data[end] == 0)
                    return $"sceKernelDlsym import and {Encoding.ASCII.GetString(elf.Data, at, end - at)} symbol literal";
                offset = end;
            }
        }
        return null;
    }
}
