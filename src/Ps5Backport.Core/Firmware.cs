using System.Globalization;

namespace Ps5Backport.Core;

/// <summary>Firmware versions and their SDK values.</summary>
public static class Firmware
{
    /// <summary>
    /// Target pairs from PS5-BACKPORK-KITCHEN core/VersionProfiles.vb: firmware major -> (PS5 SDK, PS4 SDK).
    /// GT7 confirms two of them: FW 10 is the original game's values, FW 4 is the backport's.
    /// FW 11 is not in Kitchen: Ghost of Yotei's original modules, PS4 half as in the FW 11.00 system libraries.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, (uint Ps5, uint Ps4)> SdkPairs = new Dictionary<int, (uint, uint)>
    {
        [1] = (0x01000050, 0x07590001),
        [2] = (0x02000009, 0x08050001),
        [3] = (0x03000027, 0x08540001),
        [4] = (0x04000031, 0x09040001),
        [5] = (0x05000033, 0x09590001),
        [6] = (0x06000038, 0x10090001),
        [7] = (0x07000038, 0x10590001),
        [8] = (0x08000041, 0x11090001),
        [9] = (0x09000040, 0x11590001),
        [10] = (0x10000040, 0x12090001),
        [11] = (0x11000040, 0x12590001),
    };

    /// <summary>'4', '4.03' -> (4, 3).</summary>
    public static (int Major, int Minor) Parse(string value)
    {
        string text = value.Trim();
        int dot = text.IndexOf('.');
        string major = dot < 0 ? text : text[..dot];
        string minor = dot < 0 ? "" : text[(dot + 1)..];
        if (!IsDigits(major) || (minor.Length > 0 && !IsDigits(minor))
            || !int.TryParse(major, CultureInfo.InvariantCulture, out int majorValue) || majorValue > 99
            || !int.TryParse(minor.Length == 0 ? "0" : minor, CultureInfo.InvariantCulture, out int minorValue) || minorValue > 99)
            throw new FormatException($"invalid firmware version '{value}' (use e.g. 4 or 4.03)");
        return (majorValue, minorValue);
    }

    /// <summary>Canonical name: '4' -> '4.00', '4.3' -> '4.03'.</summary>
    public static string Name(string value)
    {
        var (major, minor) = Parse(value);
        return $"{major}.{minor:D2}";
    }

    /// <summary>
    /// (PS5 SDK, PS4 SDK) for a target firmware. The PS5 value is 0xMMmm00pp in BCD: the major's
    /// pair comes from <see cref="SdkPairs"/>, the minor goes into the second byte
    /// (CONTROL Resonant targets 4.03 with 0x04030031; Backpork's 4 is 0x04000031).
    /// </summary>
    public static (uint Ps5, uint Ps4) SdkFor(string value)
    {
        var (major, minor) = Parse(value);
        if (!SdkPairs.TryGetValue(major, out var pair))
            throw new ArgumentException(
                $"no SDK pair known for firmware {major} (known: {string.Join(", ", SdkPairs.Keys)})");
        return ((pair.Ps5 & 0xFF00FFFF) | (Bcd(minor) << 16), pair.Ps4);
    }

    /// <summary>param.json version string for a firmware: '4.03' -> '0x0403000000000000'.</summary>
    public static string ParamVersion(string value)
    {
        var (major, minor) = Parse(value);
        return $"0x{Bcd(major):X2}{Bcd(minor):X2}000000000000";
    }

    /// <summary>Decimal digits read as hex: 3 -> 0x3, 10 -> 0x10.</summary>
    static uint Bcd(int n) => uint.Parse(n.ToString(CultureInfo.InvariantCulture), NumberStyles.HexNumber);

    static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);
}
