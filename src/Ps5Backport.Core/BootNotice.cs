using System.Buffers.Binary;
using System.Text;

namespace Ps5Backport.Core;

/// <summary>
/// Opt-in boot notification: redirect the eboot's <c>e_entry</c> into a cave that writes a user
/// message to <c>/dev/notification0</c>, optionally after a delay, then jumps back into the real
/// entry. The console renders the write as a "Debug:" toast. See FINDINGS.md §6f.
///
/// A <c>native_game</c> process may not issue the <c>syscall</c> instruction directly — the kernel
/// kills it with <c>SYSTEM_ILLEGAL_FUNCTION_CALL</c> (0xa002030a). The cave therefore calls the
/// eboot's own libkernel imports (<c>sceKernelOpen</c>/<c>sceKernelWrite</c>/<c>sceKernelClose</c>/
/// <c>sceKernelUsleep</c>) through their PLT stubs, so the syscalls run from libkernel's sanctioned
/// text. The request is the ShadowMount/ppr-patch <c>notify_request_t</c>: 3120 bytes, message at +45.
/// </summary>
internal static class BootNotice
{
    internal const string Option = "boot_notice";
    internal const string Name = "Boot notification (Debug toast)";
    internal const string DefaultMessage = "Backport test files by Arsenic";

    const int MaxMessageBytes = 256;
    const int ReqSize = 3120;                       // notify_request_t { char unused[45]; char message[3075]; }

    // Stack layout below rsp after "sub rsp, StackAlloc". _start is entered like a normal function (rsp = 8 mod 16)
    // and the cave pushes 11 registers (88 bytes), so StackAlloc % 16 == 0 keeps call sites 16-aligned.
    const int Path = 0x000;        // "/dev/notification0\0" (19 bytes)
    const int Req = 0x020;         // notify_request_t (3120 bytes)
    const int Msg = Req + 45;      // message offset inside the request
    const int FdSlot = 0xC50;      // saved fd (8 bytes)
    const int StackAlloc = 0xC60;

    /// <summary>Largest delay: sceKernelUsleep takes a 32-bit microsecond count.</summary>
    internal const int MaxDelaySeconds = (int)(uint.MaxValue / 1000000);

    // libkernel NIDs (imported by every game eboot; stable across firmware).
    const string SceKernelOpenNid = "1G3lF1Gg1k8";
    const string SceKernelWriteNid = "4wSze92BhLI";
    const string SceKernelCloseNid = "UK2Tl2DWUns";
    const string SceKernelUsleepNid = "1jfXLRVzisc";

    // SDK _start: "push rbp; mov rbp,rsp; push r15" (6 bytes) then later "mov r14d,[rdi]; mov rbx,rsi".
    static readonly byte[] Prologue = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57];
    static readonly byte[] StartSignature = [0x44, 0x8B, 0x37, 0x48, 0x89, 0xF3];

    /// <summary>Returns 1 when the eboot entry was redirected, else 0 (module left unchanged).</summary>
    internal static int Apply(SceElf elf, string message, int delaySeconds, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delaySeconds, MaxDelaySeconds);
        ulong entryVa = elf.U64(0x18);          // ELF64 e_entry
        long entryOff;
        try { entryOff = elf.Off(entryVa); }
        catch (InvalidDataException) { return 0; }
        if (!Matches(elf.Data, entryOff, Prologue) ||
            !ContainsWithin(elf.Data, entryOff, entryOff + 64, StartSignature))
            return 0;

        // Route through the game's own libkernel imports (no raw syscalls).
        ulong openPlt = ResolvePlt(elf, SceKernelOpenNid);
        ulong writePlt = ResolvePlt(elf, SceKernelWriteNid);
        ulong closePlt = ResolvePlt(elf, SceKernelCloseNid);
        ulong usleepPlt = delaySeconds > 0 ? ResolvePlt(elf, SceKernelUsleepNid) : 0;
        if (openPlt == 0 || writePlt == 0 || closePlt == 0)
            return 0;                            // missing imports: leave the module unchanged

        byte[] msg = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(message) ? DefaultMessage : message);
        if (msg.Length > MaxMessageBytes)
        {
            int cut = MaxMessageBytes;
            while ((msg[cut] & 0xC0) == 0x80)          // never split a UTF-8 sequence
                cut--;
            msg = msg[..cut];
        }

        var (body, calls) = BuildCave(msg, delaySeconds, usleepPlt != 0);
        long cave = Patching.FindContiguousCave(elf, body.Length)
            ?? throw new PatchException("Matched boot notification, but no contiguous code padding is available");
        ulong caveVa = elf.Vaddr(cave);

        // Patch each `call rel32` to its PLT entry (calls: [usleep?], open, write, close).
        ulong[] targets = usleepPlt != 0
            ? [usleepPlt, openPlt, writePlt, closePlt]
            : [openPlt, writePlt, closePlt];
        for (int i = 0; i < calls.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(calls[i] + 1),
                checked((int)((long)targets[i] - (long)caveVa - calls[i] - 5)));

        // Tail "jmp rel32" resumes the real entry at +6 (past the overwritten push rbp/mov rbp,rsp/push r15).
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4),
            checked((int)((long)(entryVa + 6) - (long)caveVa - body.Length)));
        body.CopyTo(elf.Data, cave);

        // Redirect e_entry (6 bytes): jmp rel32 -> cave, then nop.
        byte[] redirect = [0xE9, 0, 0, 0, 0, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(redirect.AsSpan(1),
            checked((int)(caveVa - (entryVa + 5))));
        redirect.CopyTo(elf.Data, entryOff);
        return 1;
    }

    static (byte[] Body, int[] Calls) BuildCave(byte[] msg, int delaySeconds, bool hasUsleep)
    {
        var b = new List<byte>();
        var calls = new List<int>();

        // Replicate the 6 overwritten _start bytes: push rbp; mov rbp, rsp; push r15.
        b.Add(0x55);                       // push rbp
        b.AddRange([0x48, 0x89, 0xE5]);    // mov rbp, rsp
        b.AddRange([0x41, 0x57]);          // push r15

        // Save the caller-saved registers _start still needs (rdi/rsi/rdx) and the call-clobbered set.
        b.Add(0x57);                       // push rdi
        b.Add(0x56);                       // push rsi
        b.Add(0x52);                       // push rdx
        b.Add(0x51);                       // push rcx
        b.Add(0x50);                       // push rax
        b.AddRange([0x41, 0x50]);          // push r8
        b.AddRange([0x41, 0x51]);          // push r9
        b.AddRange([0x41, 0x52]);          // push r10
        b.AddRange([0x41, 0x53]);          // push r11

        EmitSubRspImm(b, StackAlloc);

        // Build "/dev/notification0\0" on the stack (X-only text cannot hold a string constant).
        EmitMovabsRax(b, 0x746f6e2f7665642fUL); EmitStoreRax(b, Path + 0);   // "/dev/not"
        EmitMovabsRax(b, 0x6f69746163696669UL); EmitStoreRax(b, Path + 8);   // "ificatio"
        EmitStoreDwordImm(b, Path + 16, 0x306e);                              // "n0\0"

        // Zero the whole 3120-byte request (type=0 at byte 0 and byte 44 = 0, as the ABI requires).
        EmitLeaRdi(b, Req);
        b.AddRange([0x31, 0xC0]);           // xor eax, eax
        b.AddRange([0xB9]); EmitU32(b, ReqSize / 8);   // mov ecx, 390
        b.AddRange([0xF3, 0x48, 0xAB]);      // rep stosq

        // Message bytes as immediates (the request is already zeroed, so it stays NUL-terminated).
        int chunks = (msg.Length + 7) / 8;
        for (int c = 0; c < chunks; c++)
        {
            ulong word = 0;
            for (int i = 0; i < 8; i++)
            {
                int idx = c * 8 + i;
                if (idx < msg.Length)
                    word |= (ulong)msg[idx] << (i * 8);
            }
            EmitMovabsRax(b, word);
            EmitStoreRax(b, Msg + c * 8);
        }

        if (delaySeconds > 0 && hasUsleep)
        {
            b.AddRange([0xBF]); EmitU32(b, (uint)((long)delaySeconds * 1000000));   // mov edi, usec
            calls.Add(EmitCallRel32(b));       // call sceKernelUsleep@plt
        }

        // open("/dev/notification0", O_WRONLY=1, 0)
        EmitLeaRdi(b, Path);
        b.AddRange([0xBE]); EmitU32(b, 1);   // mov esi, 1 (O_WRONLY)
        b.AddRange([0x31, 0xD2]);            // xor edx, edx (mode = 0)
        calls.Add(EmitCallRel32(b));          // call sceKernelOpen@plt
        EmitStoreEax(b, FdSlot);             // save fd

        // write(fd, &req, 3120)
        EmitLoadEdi(b, FdSlot);
        EmitLeaRsi(b, Req);
        b.AddRange([0xBA]); EmitU32(b, ReqSize);   // mov edx, 3120
        calls.Add(EmitCallRel32(b));          // call sceKernelWrite@plt

        // close(fd)
        EmitLoadEdi(b, FdSlot);
        calls.Add(EmitCallRel32(b));          // call sceKernelClose@plt

        // Restore caller-saved registers; rbp/r15 stay pushed (the real entry's frame).
        EmitAddRspImm(b, StackAlloc);
        b.AddRange([0x41, 0x5B]);    // pop r11
        b.AddRange([0x41, 0x5A]);    // pop r10
        b.AddRange([0x41, 0x59]);    // pop r9
        b.AddRange([0x41, 0x58]);    // pop r8
        b.Add(0x58);                 // pop rax
        b.Add(0x59);                 // pop rcx
        b.Add(0x5A);                 // pop rdx
        b.Add(0x5E);                 // pop rsi
        b.Add(0x5F);                 // pop rdi
        b.Add(0xE9);                 // jmp rel32 (displacement patched by the caller)
        EmitU32(b, 0);

        return (b.ToArray(), calls.ToArray());
    }

    static ulong ResolvePlt(SceElf elf, string nid)
    {
        foreach (var imp in elf.Imports())
            if (imp.Nid == nid && imp.Plt)
            {
                try { return Patching.PltEntry(elf, imp); }
                catch (PatchException) { return 0; }
            }
        return 0;
    }

    static void EmitU32(List<byte> b, int v) => b.AddRange(BitConverter.GetBytes(v));
    static void EmitU32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));

    static void EmitMovabsRax(List<byte> b, ulong v) { b.Add(0x48); b.Add(0xB8); b.AddRange(BitConverter.GetBytes(v)); }

    static int EmitCallRel32(List<byte> b)
    {
        int off = b.Count;
        b.Add(0xE8);                    // call rel32
        EmitU32(b, 0);                  // displacement patched by the caller
        return off;
    }

    static void EmitStoreRax(List<byte> b, int disp) { b.AddRange([0x48, 0x89, 0x84, 0x24]); EmitU32(b, disp); }        // mov [rsp+disp], rax
    static void EmitStoreEax(List<byte> b, int disp) { b.AddRange([0x89, 0x84, 0x24]); EmitU32(b, disp); }             // mov [rsp+disp], eax
    static void EmitStoreDwordImm(List<byte> b, int disp, int imm) { b.AddRange([0xC7, 0x84, 0x24]); EmitU32(b, disp); EmitU32(b, imm); }
    static void EmitLoadEdi(List<byte> b, int disp) { b.AddRange([0x8B, 0xBC, 0x24]); EmitU32(b, disp); }              // mov edi, [rsp+disp]
    static void EmitLeaRdi(List<byte> b, int disp) { b.AddRange([0x48, 0x8D, 0xBC, 0x24]); EmitU32(b, disp); }         // lea rdi, [rsp+disp]
    static void EmitLeaRsi(List<byte> b, int disp) { b.AddRange([0x48, 0x8D, 0xB4, 0x24]); EmitU32(b, disp); }         // lea rsi, [rsp+disp]
    static void EmitSubRspImm(List<byte> b, int imm) { b.AddRange([0x48, 0x81, 0xEC]); EmitU32(b, imm); }
    static void EmitAddRspImm(List<byte> b, int imm) { b.AddRange([0x48, 0x81, 0xC4]); EmitU32(b, imm); }

    static bool Matches(byte[] data, long at, byte[] pattern)
    {
        if (at < 0 || at > data.Length - pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[at + i] != pattern[i]) return false;
        return true;
    }

    static bool ContainsWithin(byte[] data, long start, long end, byte[] needle)
    {
        long stop = Math.Min(end, data.Length) - needle.Length + 1;
        for (long i = start; i < stop; i++)
        {
            int j = 0;
            while (j < needle.Length && data[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
