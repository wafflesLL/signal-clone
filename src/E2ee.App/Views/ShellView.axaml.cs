using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using E2ee.Messaging;
using E2ee.App.ViewModels;

namespace E2ee.App.Views;

public partial class ShellView : UserControl
{
    private ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Messages.CollectionChanged -= OnMessagesChanged;
            _vm = DataContext as ShellViewModel;
            if (_vm is not null) _vm.Messages.CollectionChanged += OnMessagesChanged;
        };
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void OnAttachClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Send a photo, video or document",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Photos, videos and documents") { Patterns = MediaTypes.Extensions.Select(x => "*" + x).ToList() }],
        });
        foreach (var file in files)
            if (file.TryGetLocalPath() is { } path) await _vm.SendFileAsync(path);
    }

    // Drop files anywhere on the chat to send them.
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _vm is { CanCompose: true } && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not { CanCompose: true } || e.DataTransfer.TryGetFiles() is not { } items) return;
        foreach (var item in items)
            if (item.TryGetLocalPath() is { } path) await _vm.SendFileAsync(path);
    }

    // Keep the newest message in view, and put the cursor back in the message box.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.ScrollToEnd();
            if (e.Action == NotifyCollectionChangedAction.Reset) DraftBox.Focus();
        }, DispatcherPriority.Background);
}
