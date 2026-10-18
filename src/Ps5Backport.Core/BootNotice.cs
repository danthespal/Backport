using System.Buffers.Binary;
using System.Text;

namespace Ps5Backport.Core;

/// <summary>
/// Opt-in boot notification: redirect a startup function into a cave that writes a user message to
/// <c>/dev/notification0</c>, optionally after a delay, then jumps back into the real function. The console renders
/// the write as a "Debug:" toast. See FINDINGS.md §6f and §6p.
///
/// A <c>native_game</c> process may not issue the <c>syscall</c> instruction directly — the kernel
/// kills it with <c>SYSTEM_ILLEGAL_FUNCTION_CALL</c> (0xa002030a). The cave therefore calls the module's
/// own libkernel imports (open/write/close and a sleep for the delay) through their PLT stubs, so the syscalls
/// run from libkernel's sanctioned text. The request is the ShadowMount/ppr-patch <c>notify_request_t</c>:
/// 3120 bytes, message at +45.
///
/// The hook is the eboot's SDK <c>_start</c> when the eboot imports the functions (console-confirmed). Otherwise it
/// is the initializer (<c>DT_INIT</c>) of the game's bundled <c>sce_module/libc.prx</c>: every sample libc starts it
/// with the same prologue and imports <c>_open</c>/<c>_write</c>/<c>_close</c>/<c>_nanosleep</c>. Exactly one module
/// per game carries the notice (<see cref="Assess"/>). <c>sceKernelDlsym</c> on libkernel fails in a game process
/// (console, §6p), so missing functions are never looked up at run time. When no single cave fits, the same program is
/// split across int3 padding (§6w).
/// </summary>
internal static class BootNotice
{
    internal const string Option = "boot_notice";
    internal const string Name = "Boot notification (Debug toast)";
    internal const string DefaultMessage = "Backport test files by Arsenic";

    const int MaxMessageBytes = 256;
    const int ReqSize = 3120;                       // notify_request_t { char unused[45]; char message[3075]; }

    // Stack layout below rsp after "sub rsp, StackAlloc". The hooked function is entered like a normal function
    // (rsp = 8 mod 16) and the cave pushes 11 registers (88 bytes), so StackAlloc % 16 == 0 keeps call sites 16-aligned.
    const int Path = 0x000;        // "/dev/notification0\0" (19 bytes)
    const int Req = 0x020;         // notify_request_t (3120 bytes)
    const int Msg = Req + 45;      // message offset inside the request
    const int FdSlot = 0xC50;      // saved fd (8 bytes)
    const int Timespec = 0xC60;    // { tv_sec, tv_nsec } for a nanosleep-style delay (16 bytes)
    const int StackAlloc = 0xC70;

    /// <summary>Largest delay: sceKernelUsleep takes a 32-bit microsecond count.</summary>
    internal const int MaxDelaySeconds = (int)(uint.MaxValue / 1000000);

    const ulong DtInit = 12;
    const string LibcModule = "libc";

    // Each call uses the first alternative the module imports; all take the same arguments (NIDs stable across firmware).
    static readonly (string Name, string Nid)[] Opens = [("sceKernelOpen", "1G3lF1Gg1k8"), ("_open", "6c3rCVE-fTU"), ("open", "wuCroIGjt2g")];
    static readonly (string Name, string Nid)[] Writes = [("sceKernelWrite", "4wSze92BhLI"), ("_write", "FxVZqBAA7ks"), ("write", "FN4gaPmuFV8")];
    static readonly (string Name, string Nid)[] Closes = [("sceKernelClose", "UK2Tl2DWUns"), ("_close", "NNtFaKJbPt0"), ("close", "bY-PO6JhzhQ")];
    static readonly (string Name, string Nid)[] Usleeps = [("sceKernelUsleep", "1jfXLRVzisc"), ("usleep", "QcteRwbsnV0")];
    static readonly (string Name, string Nid)[] Nanosleeps =
        [("sceKernelNanosleep", "QvsZxomvUHs"), ("_nanosleep", "NhpspxdjEKU"), ("nanosleep", "yS8U2TGCe1A")];

    // SDK _start: "push rbp; mov rbp,rsp; push r15" (6 bytes) then later "mov r14d,[rdi]; mov rbx,rsi".
    static readonly byte[] Prologue = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57];
    static readonly byte[] StartSignature = [0x44, 0x8B, 0x37, 0x48, 0x89, 0xF3];
    // libc DT_INIT (every sample, SDK 9.00-10.00): push rbp; mov rbp,rsp; push r15; push r14; push r13; push r12; push rbx; push rax.
    static readonly byte[] LibcInitPrologue = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56, 0x41, 0x55, 0x41, 0x54, 0x53, 0x50];

    /// <summary>The cave for one module: hook point and PLT entries; Sleep = 0 for no delay.</summary>
    sealed record Plan(ulong EntryVa, long EntryOff, ulong Open, ulong Write, ulong Close, ulong Sleep, bool Nanosleep);

    /// <summary>The cave for this module, or null when it has no hook point or does not import open/write/close.</summary>
    static Plan? PlanFor(SceElf elf, bool delay)
    {
        if (Hook(elf) is not { } entry)
            return null;
        ulong open = First(elf, Opens), write = First(elf, Writes), close = First(elf, Closes);
        if (open == 0 || write == 0 || close == 0)
            return null;
        ulong sleep = delay ? First(elf, Usleeps) : 0;
        bool nanosleep = delay && sleep == 0 && (sleep = First(elf, Nanosleeps)) != 0;
        return new(entry.Va, entry.Off, open, write, close, sleep, nanosleep);   // no sleep import: no delay
    }

    /// <summary>
    /// The one module that carries the notice — the eboot's <c>_start</c> when it can, else the bundled libc's
    /// initializer — or null with the reason. The evidence is the requirements probe's text and the cook's warning.
    /// </summary>
    internal static (string? Host, string Evidence) Assess(IEnumerable<(string Path, SceElf Elf)> modules)
    {
        var hooks = modules.Where(m => Hook(m.Elf) is not null).OrderBy(m => IsLibc(m.Elf)).ToList();
        if (hooks.Count == 0)
            return (null, "no module has the SDK _start entry or the bundled libc initializer the notice hooks");
        foreach (var (path, elf) in hooks)
            if (PlanFor(elf, delay: true) is not null)
                return (path, path == hooks[0].Path
                    ? "depends on the console or the tester's intent, not on the game"
                    : $"depends on the tester's intent; {hooks[0].Path} imports {Missing(hooks[0].Elf)}, so the notice runs in {path}'s initializer");
        return (null, string.Join("; ", hooks.Select(h => $"{h.Path} imports {Missing(h.Elf)}")));
    }

    /// <summary>"no X (A, B or C)" for every call the module cannot make.</summary>
    static string Missing(SceElf elf) => string.Join(", ", new[] { ("open", Opens), ("write", Writes), ("close", Closes) }
        .Where(r => First(elf, r.Item2) == 0)
        .Select(r => $"no {r.Item1} ({string.Join(", ", r.Item2[..^1].Select(f => f.Name))} or {r.Item2[^1].Name})"));

    /// <summary>Returns 1 when the module's hook point was redirected, else 0 (module left unchanged).</summary>
    internal static int Apply(SceElf elf, string message, int delaySeconds, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delaySeconds, MaxDelaySeconds);
        // Route through the module's own libkernel imports (no raw syscalls).
        if (PlanFor(elf, delaySeconds > 0) is not { } plan)
            return 0;                            // no hook point, or no way to call open/write/close: unchanged

        byte[] msg = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(message) ? DefaultMessage : message);
        if (msg.Length > MaxMessageBytes)
        {
            int cut = MaxMessageBytes;
            while ((msg[cut] & 0xC0) == 0x80)          // never split a UTF-8 sequence
                cut--;
            msg = msg[..cut];
        }

        var (body, calls) = BuildCave(msg, delaySeconds, plan);
        ulong caveVa;
        if (Patching.FindContiguousCave(elf, body.Length) is long cave)
        {
            caveVa = elf.Vaddr(cave);

            // Patch each `call rel32` to its PLT entry.
            foreach (var (at, target) in calls)
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 1),
                    checked((int)((long)target - (long)caveVa - at - 5)));

            // Tail "jmp rel32" resumes the real function at +6 (past the overwritten push rbp/mov rbp,rsp/push r15).
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(body.Length - 4),
                checked((int)((long)(plan.EntryVa + 6) - (long)caveVa - body.Length)));
            body.CopyTo(elf.Data, cave);
        }
        else
        {
            // No single cave fits (the text extension can go to contiguous adapters first, PPSA25991): the same program
            // split across int3 padding, calling through the imports' GOT slots (what their PLT entries jump through).
            var gots = new Dictionary<string, ulong> { ["open"] = Got(elf, plan.Open), ["write"] = Got(elf, plan.Write), ["close"] = Got(elf, plan.Close) };
            if (plan.Sleep != 0)
                gots["sleep"] = Got(elf, plan.Sleep);
            try
            {
                caveVa = Patching.PlaceChain(elf, BuildChain(msg, delaySeconds, plan), 0, tailJmp: false, callGots: gots,
                                             tailTo: plan.EntryVa + 6);
            }
            catch (PatchException e)
            {
                throw new PatchException($"Matched boot notification, but {e.Message}", e);
            }
        }

        // Redirect the hook point (6 bytes): jmp rel32 -> cave, then nop.
        byte[] redirect = [0xE9, 0, 0, 0, 0, 0x90];
        BinaryPrimitives.WriteInt32LittleEndian(redirect.AsSpan(1),
            checked((int)((long)caveVa - (long)(plan.EntryVa + 5))));
        redirect.CopyTo(elf.Data, plan.EntryOff);
        return 1;
    }

    static (byte[] Body, List<(int At, ulong Target)> Calls) BuildCave(byte[] msg, int delaySeconds, Plan plan)
    {
        var b = new List<byte>();
        var calls = new List<(int At, ulong Target)>();    // call rel32 sites and their PLT targets

        // Replicate the 6 overwritten bytes: push rbp; mov rbp, rsp; push r15.
        b.Add(0x55);                       // push rbp
        b.AddRange([0x48, 0x89, 0xE5]);    // mov rbp, rsp
        b.AddRange([0x41, 0x57]);          // push r15

        // Save the argument registers the hooked function still needs (rdi/rsi/rdx) and the call-clobbered set.
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
        for (int c = 0; c < (msg.Length + 7) / 8; c++)
        {
            ulong word = 0;
            for (int i = 0; i < 8 && c * 8 + i < msg.Length; i++)
                word |= (ulong)msg[c * 8 + i] << (i * 8);
            EmitMovabsRax(b, word);
            EmitStoreRax(b, Msg + c * 8);
        }

        if (plan.Sleep != 0 && plan.Nanosleep)
        {
            EmitStoreQwordImm(b, Timespec, delaySeconds);   // tv_sec
            EmitStoreQwordImm(b, Timespec + 8, 0);          // tv_nsec
            EmitLeaRdi(b, Timespec);
            b.AddRange([0x31, 0xF6]);                       // xor esi, esi (no remainder)
            calls.Add((EmitCallRel32(b), plan.Sleep));      // nanosleep(&ts, NULL)
        }
        else if (plan.Sleep != 0)
        {
            b.AddRange([0xBF]); EmitU32(b, (uint)((long)delaySeconds * 1000000));   // mov edi, usec
            calls.Add((EmitCallRel32(b), plan.Sleep));      // usleep(usec)
        }

        // open("/dev/notification0", O_WRONLY=1, 0); al = 0 because POSIX open is variadic
        EmitLeaRdi(b, Path);
        b.AddRange([0xBE]); EmitU32(b, 1);   // mov esi, 1 (O_WRONLY)
        b.AddRange([0x31, 0xD2]);            // xor edx, edx (mode = 0)
        b.AddRange([0x31, 0xC0]);            // xor eax, eax
        calls.Add((EmitCallRel32(b), plan.Open));
        EmitStoreEax(b, FdSlot);             // save fd

        // write(fd, &req, 3120)
        EmitLoadEdi(b, FdSlot);
        EmitLeaRsi(b, Req);
        b.AddRange([0xBA]); EmitU32(b, ReqSize);   // mov edx, 3120
        calls.Add((EmitCallRel32(b), plan.Write));

        // close(fd)
        EmitLoadEdi(b, FdSlot);
        calls.Add((EmitCallRel32(b), plan.Close));

        // Restore the saved registers; rbp/r15 stay pushed (the real function's frame).
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

        return (b.ToArray(), calls);
    }

    /// <summary>
    /// <see cref="BuildCave"/>'s program for <see cref="Patching.PlaceChain"/>: no instruction over 8 bytes, so each fits the
    /// 13 usable bytes of a 16-byte int3 run with its joining jmp (constants go 4 bytes at a time through eax). Calls are
    /// call_got:open/write/close/sleep; the chain's tail jmp resumes the hooked function.
    /// </summary>
    static List<Insn> BuildChain(byte[] msg, int delaySeconds, Plan plan)
    {
        var c = new List<Insn>();
        void I(params byte[] bytes) => c.Add(new Insn(bytes, false, ""));
        void E(Action<List<byte>> emit) { var b = new List<byte>(); emit(b); c.Add(new Insn([.. b], false, "")); }
        void Call(string import) => c.Add(new Insn([], true, "", CallNid: import));
        void StoreBytes(int disp, byte[] data)          // mov eax, imm32; mov [rsp+disp], eax
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                byte[] word = new byte[4];
                data.AsSpan(i, Math.Min(4, data.Length - i)).CopyTo(word);
                I([0xB8, .. word]);
                E(b => EmitStoreEax(b, disp + i));
            }
        }

        I(0x55); I(0x48, 0x89, 0xE5); I(0x41, 0x57);                         // push rbp; mov rbp, rsp; push r15
        I(0x57); I(0x56); I(0x52); I(0x51); I(0x50);                         // push rdi, rsi, rdx, rcx, rax
        I(0x41, 0x50); I(0x41, 0x51); I(0x41, 0x52); I(0x41, 0x53);          // push r8..r11
        E(b => EmitSubRspImm(b, StackAlloc));

        StoreBytes(Path, "/dev/notification0\0"u8.ToArray());
        E(b => EmitLeaRdi(b, Req));
        I(0x31, 0xC0);                                                       // xor eax, eax
        E(b => { b.Add(0xB9); EmitU32(b, ReqSize / 8); });                   // mov ecx, 390
        I(0xF3, 0x48, 0xAB);                                                 // rep stosq
        StoreBytes(Msg, msg);

        if (plan.Sleep != 0 && plan.Nanosleep)
        {
            E(b => { b.Add(0xB8); EmitU32(b, delaySeconds); });               // mov eax, tv_sec
            E(b => { b.AddRange([0x48, 0x89, 0x84, 0x24]); EmitU32(b, Timespec); });       // mov [rsp+Timespec], rax
            I(0x31, 0xC0);                                                   // xor eax, eax
            E(b => { b.AddRange([0x48, 0x89, 0x84, 0x24]); EmitU32(b, Timespec + 8); });   // tv_nsec = 0
            E(b => EmitLeaRdi(b, Timespec));
            I(0x31, 0xF6);                                                   // xor esi, esi
            Call("sleep");
        }
        else if (plan.Sleep != 0)
        {
            E(b => { b.Add(0xBF); EmitU32(b, (uint)((long)delaySeconds * 1000000)); });   // mov edi, usec
            Call("sleep");
        }

        E(b => EmitLeaRdi(b, Path));
        E(b => { b.Add(0xBE); EmitU32(b, 1); });                              // mov esi, 1 (O_WRONLY)
        I(0x31, 0xD2); I(0x31, 0xC0);                                        // xor edx, edx; xor eax, eax
        Call("open");
        E(b => EmitStoreEax(b, FdSlot));

        E(b => EmitLoadEdi(b, FdSlot));
        E(b => EmitLeaRsi(b, Req));
        E(b => { b.Add(0xBA); EmitU32(b, ReqSize); });                        // mov edx, 3120
        Call("write");

        E(b => EmitLoadEdi(b, FdSlot));
        Call("close");

        E(b => EmitAddRspImm(b, StackAlloc));
        I(0x41, 0x5B); I(0x41, 0x5A); I(0x41, 0x59); I(0x41, 0x58);          // pop r11..r8
        I(0x58); I(0x59); I(0x5A); I(0x5E); I(0x5F);                         // pop rax, rcx, rdx, rsi, rdi
        return c;
    }

    /// <summary>GOT slot a PLT entry jumps through (<c>jmp [rip+rel32]</c>, checked by <see cref="Patching.PltEntry"/>).</summary>
    static ulong Got(SceElf elf, ulong plt) =>
        plt + 6 + (ulong)(long)BinaryPrimitives.ReadInt32LittleEndian(elf.Data.AsSpan((int)elf.Off(plt) + 2));

    /// <summary>
    /// The hook point and its file offset: e_entry when it is the SDK <c>_start</c>, or DT_INIT of the bundled libc
    /// when it has the libc initializer prologue; else null.
    /// </summary>
    static (ulong Va, long Off)? Hook(SceElf elf)
    {
        if (At(elf, elf.U64(0x18)) is { } start && Matches(elf.Data, start.Off, Prologue)       // ELF64 e_entry
            && ContainsWithin(elf.Data, start.Off, start.Off + 64, StartSignature))
            return start;
        if (IsLibc(elf) && elf.Dyn.TryGetValue(DtInit, out ulong init) && At(elf, init) is { } entry
            && Matches(elf.Data, entry.Off, LibcInitPrologue))
            return entry;
        return null;
    }

    static bool IsLibc(SceElf elf) => elf.ModuleName == LibcModule;

    static (ulong Va, long Off)? At(SceElf elf, ulong va)
    {
        try { return (va, elf.Off(va)); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>PLT entry of the first imported alternative, or 0.</summary>
    static ulong First(SceElf elf, (string Name, string Nid)[] alternatives)
    {
        foreach (var (_, nid) in alternatives)
            if (ResolvePlt(elf, nid) is var plt and not 0)
                return plt;
        return 0;
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
    static void EmitStoreQwordImm(List<byte> b, int disp, int imm) { b.AddRange([0x48, 0xC7, 0x84, 0x24]); EmitU32(b, disp); EmitU32(b, imm); }   // mov qword [rsp+disp], imm32
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
