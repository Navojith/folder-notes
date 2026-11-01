using System.ComponentModel;
using FolderNotes.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FolderNotes.App.Labels;

/// <summary>Thin view over <see cref="LabelsViewModel"/> (specs/05 § Labels page).</summary>
public sealed partial class LabelsPage : UserControl
{
    private LabelsViewModel? _vm;
    private bool _syncing;
    private bool _committing; // Enter commits; the LostFocus that follows mustn't commit again

    public LabelsPage()
    {
        InitializeComponent();
    }

    /// <summary>"Show notes" was clicked for this label name.</summary>
    public event EventHandler<string>? ShowNotesRequested;

    internal LabelsViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
            }

            LabelList.ItemsSource = _vm?.Items;
            SyncForm();
        }
    }

    // x:Bind helpers
    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static string ActionName(string action, string name) => action + " " + name;

    public static string RenameBoxName(string name) => "New name for " + name;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LabelsViewModel.CountText) or nameof(LabelsViewModel.NewName)
            or nameof(LabelsViewModel.NewColor) or nameof(LabelsViewModel.NewError))
        {
            SyncForm();
        }
    }

    private void SyncForm()
    {
        _syncing = true;
        try
        {
            CountText.Text = _vm?.CountText ?? "";
            if (_vm is not null && NewNameBox.Text != _vm.NewName)
            {
                NewNameBox.Text = _vm.NewName;
            }

            var color = _vm?.NewColor ?? "";
            NewColorSwatch.ColorKey = color;
            NewColorText.Text = color;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NewColorButton, "New label colour: " + color);
            NewErrorText.Text = _vm?.NewError ?? "";
            NewErrorText.Visibility = string.IsNullOrEmpty(_vm?.NewError) ? Visibility.Collapsed : Visibility.Visible;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ───────────── new label ─────────────

    private void OnNewNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _vm is not null)
        {
            _vm.NewName = NewNameBox.Text;
        }
    }

    private async void OnNewNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && _vm is not null)
        {
            e.Handled = true;
            await _vm.CreateAsync();
        }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && await _vm.CreateAsync())
        {
            NewNameBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnNewColour(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        ColorPickerFlyout.Create(_vm.NewColor, key => _vm.NewColor = key).ShowAt(NewColorButton);
    }

    // ───────────── rows ─────────────

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { Tag: LabelItem item })
        {
            _vm.BeginRename(item); // the box loads (x:Load) and takes focus in OnRenameBoxLoaded
        }
    }

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    private async void OnRenameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: LabelItem item })
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _committing = true;
            try
            {
                await _vm.CommitRenameAsync(item);
            }
            finally
            {
                _committing = false;
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            LabelsViewModel.CancelRename(item);
        }
    }

    private async void OnRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_committing && _vm is not null && sender is FrameworkElement { Tag: LabelItem { IsEditing: true } item })
        {
            await _vm.CommitRenameAsync(item);
        }
    }

    private void OnColour(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && sender is FrameworkElement { Tag: LabelItem item } button)
        {
            ColorPickerFlyout.Create(item.Color, async key => await _vm.RecolorAsync(item, key)).ShowAt(button);
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: LabelItem item })
        {
            return;
        }

        var (title, message) = LabelsViewModel.DeleteConfirmation(item);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _vm.DeleteAsync(item);
        }
    }

    private void OnShowNotes(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LabelItem item })
        {
            ShowNotesRequested?.Invoke(this, item.Name);
        }
    }
}
