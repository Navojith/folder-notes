using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace FolderNotes.App.Orphans;

/// <summary>Thin view over <see cref="OrphansViewModel"/> (specs/05 § Orphaned page).</summary>
public sealed partial class OrphansPage : UserControl
{
    private OrphansViewModel? _vm;

    public OrphansPage()
    {
        InitializeComponent();
    }

    internal OrphansViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.Confirm = null;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.Confirm = ConfirmAsync;
            }

            OrphanList.ItemsSource = _vm?.Items;
            Sync();
        }
    }

    // x:Bind helpers
    public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static string ActionName(string action, string title) => action + " " + title;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        CountText.Text = _vm?.CountText ?? "";
        EmptyState.Visibility = _vm?.IsEmpty == true ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = _vm is { IsChecking: false };

        CheckBar.Message = _vm?.CheckMessage ?? "";
        CheckBar.IsOpen = !string.IsNullOrEmpty(_vm?.CheckMessage);

        UndoBar.Message = _vm?.BarText ?? "";
        UndoBar.Severity = _vm?.CanUndo == true ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        UndoButton.Visibility = _vm?.CanUndo == true ? Visibility.Visible : Visibility.Collapsed;
        UndoBar.IsOpen = !string.IsNullOrEmpty(_vm?.BarText);
    }

    /// <summary>"Relink to a different folder?" (05 § Orphaned page).</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Relink",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnRelink(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, item => _vm!.RelinkAsync(item), "Relink");

    private async void OnDelete(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, item => _vm!.DeleteAsync(item), "Delete");

    private void OnOpenNote(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { Tag: OrphanItem item })
        {
            _vm.OpenNote(item);
        }
    }

    private async void OnCheckAgain(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm is not null)
            {
                await _vm.CheckAgainAsync();
            }
        }
#pragma warning disable CA1031 // A click handler must not crash the UI thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "Check again failed");
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_vm is not null)
            {
                await _vm.UndoAsync();
            }
        }
#pragma warning disable CA1031 // See OnCheckAgain.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "Undo failed");
        }
    }

    private void OnUndoBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            _vm?.CloseBar();
        }
    }

    private void OnCheckBarClosed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        if (args.Reason == InfoBarCloseReason.CloseButton)
        {
            _vm?.ClearCheckMessage();
        }
    }

    private async Task RunAsync(object sender, Func<OrphanItem, Task> action, string what)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: OrphanItem item })
        {
            return;
        }

        try
        {
            await action(item);
        }
#pragma warning disable CA1031 // See OnCheckAgain.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "{What} on the Orphaned page failed", what);
        }
    }
}
