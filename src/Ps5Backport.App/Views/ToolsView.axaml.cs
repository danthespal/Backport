using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Ps5Backport.App.ViewModels;

namespace Ps5Backport.App.Views;

public partial class ToolsView : UserControl
{
    public ToolsView()
    {
        InitializeComponent();
        DropArea.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None);
        DropArea.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (DataContext is ToolsViewModel vm && e.DataTransfer.TryGetFiles() is { } items)
                await vm.Add(items.OfType<IStorageFile>().Select(f => f.TryGetLocalPath()).OfType<string>());
        });
    }
}
