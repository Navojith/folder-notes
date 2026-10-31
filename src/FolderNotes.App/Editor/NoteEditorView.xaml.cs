using System.ComponentModel;
using System.Diagnostics;
using FolderNotes.App.Controls;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderNotes.App.Editor;

/// <summary>
/// Thin view over <see cref="NoteEditorViewModel"/> (ADR-014): pushes control changes into the view
/// model and mirrors its state back. All rules live in the view model.
/// </summary>
public sealed partial class NoteEditorView : UserControl
{
    private NoteEditorViewModel? _vm;
    private bool _syncing; // true while controls are set from the view model

    public NoteEditorView()
    {
        InitializeComponent();
        BuildStatusItems();
        BuildColorItems();
    }

    /// <summary>Raised when the view wants its window closed (undo window ran out).</summary>
    public event EventHandler? CloseRequested;

    internal NoteEditorViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.Labels.CollectionChanged -= OnLabelsChanged;
                _vm.UndoExpired -= OnUndoExpired;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.Labels.CollectionChanged += OnLabelsChanged;
                _vm.UndoExpired += OnUndoExpired;
            }

            SyncAll();
        }
    }

    /// <summary>Back to the initial look for a new session in a reused window.</summary>
    public void ResetForNewSession()
    {
        BodyTabs.SelectedItem = WriteTab;
        LabelBox.Text = "";
        LabelBox.ItemsSource = null;
    }

    /// <summary>Puts the caret in the title; a draft's suggested title is selected so typing replaces it (05).</summary>
    public void FocusTitle()
    {
        TitleBox.Focus(FocusState.Programmatic);
        if (_vm?.IsDraft == true)
        {
            TitleBox.SelectAll();
        }
        else
        {
            TitleBox.Select(TitleBox.Text.Length, 0);
        }
    }

    // ───────────── view model → controls ─────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoteEditorViewModel.State):
            case nameof(NoteEditorViewModel.MissingMessage):
            case nameof(NoteEditorViewModel.CanDelete):
            case nameof(NoteEditorViewModel.FolderExists):
            case nameof(NoteEditorViewModel.FolderPath):
                SyncState();
                break;
            case nameof(NoteEditorViewModel.SaveText):
                SaveText.Text = _vm!.SaveText;
                break;
            case nameof(NoteEditorViewModel.TitleError):
            case nameof(NoteEditorViewModel.BodyError):
            case nameof(NoteEditorViewModel.LabelError):
                SyncErrors();
                break;
            case nameof(NoteEditorViewModel.Title):
            case nameof(NoteEditorViewModel.Body):
            case nameof(NoteEditorViewModel.Status):
            case nameof(NoteEditorViewModel.AccentColor):
            case nameof(NoteEditorViewModel.KeepUntil):
            case nameof(NoteEditorViewModel.ReviewOn):
            case nameof(NoteEditorViewModel.SafeAfter):
                SyncFields();
                break;
        }
    }

    private void SyncAll()
    {
        SyncState();
        SyncFields();
        SyncLabels();
        SyncErrors();
        SaveText.Text = _vm?.SaveText ?? "";
    }

    private void SyncState()
    {
        var state = _vm?.State ?? EditorState.Loading;
        EditorPanel.Visibility = state == EditorState.Editing ? Visibility.Visible : Visibility.Collapsed;
        DeletedBar.IsOpen = state == EditorState.Deleted;
        MissingBar.IsOpen = state == EditorState.Missing;
        MissingBar.Message = _vm?.MissingMessage ?? "";
        DeleteButton.IsEnabled = _vm?.CanDelete == true;
        FolderText.Text = _vm?.FolderPath ?? "";
        ToolTipService.SetToolTip(FolderText, _vm?.FolderPath);
        OpenFolderButton.IsEnabled = _vm?.FolderExists == true;
    }

    private void SyncFields()
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (TitleBox.Text != _vm.Title)
            {
                TitleBox.Text = _vm.Title;
            }

            if (EditorText.NormalizeNewlines(BodyBox.Text) != _vm.Body)
            {
                BodyBox.Text = _vm.Body;
            }

            StatusBox.SelectedIndex = (int)_vm.Status;
            var colorIndex = _vm.AccentColor is { } key ? Palette.Keys.ToList().IndexOf(key) + 1 : 0;
            ColorGrid.SelectedIndex = Math.Max(0, colorIndex);
            SetDate(KeepPicker, KeepClear, _vm.KeepUntil);
            SetDate(ReviewPicker, ReviewClear, _vm.ReviewOn);
            SetDate(SafePicker, SafeClear, _vm.SafeAfter);
            if (PreviewBorder.Visibility == Visibility.Visible)
            {
                Preview.Markdown = _vm.Body;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SetDate(CalendarDatePicker picker, HyperlinkButton clear, DateOnly? date)
    {
        DateTimeOffset? value = date is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)) : null;
        if (picker.Date?.Date != value?.Date)
        {
            picker.Date = value;
        }

        clear.Visibility = date is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncErrors()
    {
        Show(TitleErrorText, _vm?.TitleError);
        Show(BodyErrorText, _vm?.BodyError);
        Show(LabelErrorText, _vm?.LabelError);

        static void Show(TextBlock block, string? text)
        {
            block.Text = text ?? "";
            block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnLabelsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => SyncLabels();

    private void SyncLabels()
    {
        LabelPanel.Children.Clear();
        if (_vm is null)
        {
            return;
        }

        foreach (var label in _vm.Labels)
        {
            var chip = new LabelChip { LabelName = label.Name, ColorKey = label.Color, VerticalAlignment = VerticalAlignment.Center };
            var remove = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 10 },
                Padding = new Thickness(4),
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Background = null,
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(remove, "Remove label " + label.Name);
            remove.Click += (_, _) => _vm.RemoveLabel(label);
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            item.Children.Add(chip);
            item.Children.Add(remove);
            LabelPanel.Children.Add(item);
        }
    }

    private void OnUndoExpired(object? sender, EventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ───────────── controls → view model ─────────────

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _vm is not null)
        {
            _vm.Title = TitleBox.Text;
        }
    }

    private void OnBodyChanged(object sender, TextChangedEventArgs e)
    {
        if (!_syncing && _vm is not null)
        {
            _vm.Body = EditorText.NormalizeNewlines(BodyBox.Text);
        }
    }

    private void OnStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && _vm is not null && StatusBox.SelectedIndex >= 0)
        {
            _vm.Status = (NoteStatus)StatusBox.SelectedIndex;
        }
    }

    private void OnColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && _vm is not null && ColorGrid.SelectedIndex >= 0)
        {
            _vm.AccentColor = ColorGrid.SelectedIndex == 0 ? null : Palette.Keys[ColorGrid.SelectedIndex - 1];
        }
    }

    private void OnDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        DateOnly? value = sender.Date is { } d ? DateOnly.FromDateTime(d.Date) : null;
        if (sender == KeepPicker)
        {
            _vm.KeepUntil = value;
        }
        else if (sender == ReviewPicker)
        {
            _vm.ReviewOn = value;
        }
        else
        {
            _vm.SafeAfter = value;
        }
    }

    private void OnClearDate(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (ReferenceEquals(sender, KeepClear))
        {
            _vm.KeepUntil = null;
            KeepPicker.Focus(FocusState.Programmatic);
        }
        else if (ReferenceEquals(sender, ReviewClear))
        {
            _vm.ReviewOn = null;
            ReviewPicker.Focus(FocusState.Programmatic);
        }
        else
        {
            _vm.SafeAfter = null;
            SafePicker.Focus(FocusState.Programmatic);
        }
    }

    private void OnLabelFocus(object sender, RoutedEventArgs e) => RefreshSuggestions();

    private void OnLabelTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RefreshSuggestions();
        }
    }

    private void RefreshSuggestions()
    {
        if (_vm is not null)
        {
            LabelBox.ItemsSource = _vm.SuggestLabels(LabelBox.Text);
        }
    }

    private void OnLabelSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_vm is null)
        {
            return;
        }

        var added = args.ChosenSuggestion is LabelSuggestion s ? _vm.AddLabel(s) : _vm.AddLabel(args.QueryText);
        if (added)
        {
            LabelBox.Text = "";
            RefreshSuggestions();
        }
    }

    private void OnBodyTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var preview = sender.SelectedItem == PreviewTab;
        BodyBox.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewBorder.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            Preview.Markdown = _vm?.Body ?? "";
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_vm?.FolderPath is not { Length: > 0 } path || !Directory.Exists(path))
        {
            return;
        }

        // explorer.exe does its own argument parsing; a quoted drive root ("D:\") is fine there.
        using var _ = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm is null || !_vm.CanDelete)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete this note?",
            Content = "You can undo this for 10 seconds.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _vm.DeleteAsync();
            UndoButton.Focus(FocusState.Programmatic);
        }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.UndoDeleteAsync();
            if (_vm.State == EditorState.Editing)
            {
                FocusTitle();
            }
        }
    }

    // ───────────── static content ─────────────

    private void BuildStatusItems()
    {
        foreach (var visual in StatusVisuals.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (visual.HasPill)
            {
                var icon = new PathIcon { Data = ThemeTokens.GetIconGeometry(visual.IconKey!), Width = 20, Height = 20 };
                row.Children.Add(new Viewbox { Width = 16, Height = 16, Child = icon });
            }

            row.Children.Add(new TextBlock { Text = visual.Text, VerticalAlignment = VerticalAlignment.Center });
            var item = new ComboBoxItem { Content = row };
            AutomationProperties.SetName(item, visual.Text);
            StatusBox.Items.Add(item);
        }
    }

    private void BuildColorItems()
    {
        foreach (var key in new string?[] { null }.Concat(Palette.Keys))
        {
            var item = new GridViewItem { Content = new ColorSwatch { ColorKey = key } };
            AutomationProperties.SetName(item, key ?? "No color");
            ToolTipService.SetToolTip(item, key ?? "No color");
            ColorGrid.Items.Add(item);
        }
    }
}
