using Ps5Backport.Core;

// Ps5Backport command line. Phase 0: the primitives ported so far; Phase 5 brings parity with `python -m ps5bp`.
const string Usage = """
    usage: ps5backport <command> [args]
      nid <symbol name>...     NID of each name
      sdk <firmware>           SDK pair and param.json version for a target firmware (e.g. 4, 4.03)
      info <file>...           file kind; for ELFs module name, type, import/export counts
      unwrap <self> <elf>      SELF -> ELF (keeps the original ELF header)
      sign <elf> <self>        ELF -> fake-signed SELF (make_fself defaults)
    """;

try
{
    switch (args)
    {
        case ["nid", .. var names] when names.Length > 0:
            foreach (string name in names)
                Console.WriteLine($"{Nid.Compute(name)}  {name}");
            return 0;
        case ["sdk", var fw]:
            var (ps5, ps4) = Firmware.SdkFor(fw);
            Console.WriteLine($"FW {Firmware.Name(fw)}: PS5 SDK 0x{ps5:x8}, PS4 SDK 0x{ps4:x8}, param.json {Firmware.ParamVersion(fw)}");
            return 0;
        case ["info", .. var files] when files.Length > 0:
            foreach (string file in files)
            {
                string kind = Self.FileKind(file);
                Console.WriteLine($"{file}: {kind}");
                if (kind != Self.KindElf)
                    continue;
                var elf = SceElf.Load(file);
                Console.WriteLine($"   type=0x{elf.Type:x} module={elf.ModuleName}");
                Console.WriteLine($"   build: {elf.BuildPath}");
                Console.WriteLine($"   {elf.Imports().Count} imports, {elf.Exports().Count} exports");
            }
            return 0;
        case ["unwrap", var input, var output]:
            Self.UnwrapFile(input, output);
            Console.WriteLine($"unwrapped {input} -> {output}");
            return 0;
        case ["sign", var input, var output]:
            Self.SignFile(input, output);
            Console.WriteLine($"signed {input} -> {output}");
            return 0;
        default:
            Console.Error.Write(Usage);
            return 2;
    }
}
catch (Exception e) when (e is FormatException or ArgumentException or InvalidDataException or IOException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}
