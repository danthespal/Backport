using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Ps5Backport.Core;

namespace Ps5Backport.App;

/// <summary>Main window state. Phase 0 shell: layout and wiring only; analysis and cooking arrive in Phase 6.</summary>
public partial class MainViewModel : ObservableObject
{
    public MainViewModel()
    {
        foreach (int major in Firmware.SdkPairs.Keys.Order())
            Firmwares.Add(major.ToString());
        SelectedFirmware = "4";
        Log.Add("PS5 Backport Studio shell (Phase 0). Analysis and cooking are not implemented yet.");
    }

    /// <summary>Target firmwares the SDK table knows (minor versions like 4.03 can be typed).</summary>
    public ObservableCollection<string> Firmwares { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty]
    public partial string GameFolder { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SdkText))]
    public partial string SelectedFirmware { get; set; }

    [ObservableProperty]
    public partial bool CandidateFixes { get; set; }

    [ObservableProperty]
    public partial bool StubUnknown { get; set; }

    [ObservableProperty]
    public partial bool ExtendText { get; set; }

    [ObservableProperty]
    public partial bool PatchParam { get; set; }

    /// <summary>The SDK pair the selected firmware maps to, shown under the picker.</summary>
    public string SdkText
    {
        get
        {
            try
            {
                var (ps5, ps4) = Firmware.SdkFor(SelectedFirmware ?? "");
                return $"FW {Firmware.Name(SelectedFirmware!)}  →  PS5 SDK 0x{ps5:X8}, PS4 SDK 0x{ps4:X8}";
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                return e.Message;
            }
        }
    }

    [RelayCommand]
    void Browse()
    {
        var dialog = new OpenFolderDialog { Title = "Select a decrypted game folder (contains eboot.bin)" };
        if (dialog.ShowDialog() == true)
        {
            GameFolder = dialog.FolderName;
            Log.Add($"Game folder: {GameFolder}");
        }
    }

    [RelayCommand]
    static void About()
    {
        MessageBox.Show(
            "PS5 Backport Studio 0.1 (Phase 0)\n\n" +
            "Backports PS5 games to older firmware: import analysis, generic fixes, fakelib sets and\n" +
            "ShadowMountPlus overlays. Engine ported from ps5bp.\n\n" +
            "Credits: make_fself (ps5-payload-dev / drakmor), BestPig/BackPork,\n" +
            "idlesauce, AnyPS5, ShadowMountPlus (drakmor), Backpork Kitchen (inspiration).",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
