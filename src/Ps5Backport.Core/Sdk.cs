using System.Buffers.Binary;

namespace Ps5Backport.Core;

/// <summary>
/// SDK version fields in PT_SCE_PROCPARAM (eboot) / PT_SCE_MODULE_PARAM (prx).
/// Param layout: +0x00 size, +0x08 magic, +0x10 PS4 SDK, +0x14 PS5 SDK.
/// </summary>
public static class Sdk
{
    public const uint ProcessParamMagic = 0x4942524F;   // 'ORBI'
    public const uint ModuleParamMagic = 0x3C13F4BF;
    const int Ps4SdkOffset = 0x10;

    /// <summary>File offset of the param segment, or -1 when it is all zeros (no SDK fields: some Wwise plugins).</summary>
    static long ParamOffset(SceElf elf)
    {
        foreach (var p in elf.Phdrs)
        {
            if (p.Type is not (SceElf.PtSceProcParam or SceElf.PtSceModuleParam))
                continue;
            if (elf.Data.AsSpan(checked((int)p.Offset), checked((int)p.Filesz)).IndexOfAnyExcept((byte)0) < 0)
                return -1;
            uint magic = elf.U32(p.Offset + 8);
            uint expected = p.Type == SceElf.PtSceProcParam ? ProcessParamMagic : ModuleParamMagic;
            if (magic != expected)
                throw new InvalidDataException($"param segment magic 0x{magic:x} != 0x{expected:x}");
            return p.Offset;
        }
        throw new InvalidDataException("no PT_SCE_PROCPARAM / PT_SCE_MODULE_PARAM segment");
    }

    /// <summary>(PS5 SDK, PS4 SDK); (0, 0) for a module without SDK fields.</summary>
    public static (uint Ps5, uint Ps4) Read(SceElf elf)
    {
        long param = ParamOffset(elf);
        if (param < 0)
            return (0, 0);
        long at = param + Ps4SdkOffset;
        return (elf.U32(at + 4), elf.U32(at));
    }

    /// <summary>Sets the SDK pair. Returns true if anything changed (never for a module without SDK fields).</summary>
    public static bool Write(SceElf elf, uint ps5, uint ps4)
    {
        long param = ParamOffset(elf);
        if (param < 0)
            return false;
        long at = param + Ps4SdkOffset;
        var old = (elf.U32(at + 4), elf.U32(at));
        var span = elf.Data.AsSpan(checked((int)at));
        BinaryPrimitives.WriteUInt32LittleEndian(span, ps4);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ps5);
        return old != (ps5, ps4);
    }
}
