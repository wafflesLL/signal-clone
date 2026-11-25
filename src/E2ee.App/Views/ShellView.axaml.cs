using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
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
    }

    // Keep the newest message in view, and put the cursor back in the message box.
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            MessageScroller.ScrollToEnd();
            if (e.Action == NotifyCollectionChangedAction.Reset) DraftBox.Focus();
        }, DispatcherPriority.Background);
}
