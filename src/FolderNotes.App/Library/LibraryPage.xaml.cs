using System.Collections.Specialized;
using System.ComponentModel;
using FolderNotes.App.Services;
using FolderNotes.App.Themes;
using FolderNotes.Core.Domain;
using FolderNotes.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace FolderNotes.App.Library;

/// <summary>
/// Thin view over <see cref="LibraryViewModel"/> (specs/05 § Library behaviour): mirrors its state,
/// forwards commands, and switches between the wide (master/detail) and narrow layouts.
/// </summary>
public sealed partial class LibraryPage : UserControl
{
    /// <summary>Master/detail from this width (effective pixels) up (05).</summary>
    public const double WideWidth = 1000;

    private const double MinMasterWidth = 400;

    private static readonly (NoteSort Sort, string Text)[] SortOptions =
    [
        (NoteSort.UpdatedDesc, "Last updated"),
        (NoteSort.Title, "Title"),
        (NoteSort.DueDate, "Due date"),
        (NoteSort.Path, "Path"),
    ];

    private LibraryViewModel? _vm;
    private bool _syncing;
    private bool _wide = true;
    private bool _detailShown; // narrow layout only: the editor replaces the list

    public LibraryPage()
    {
        InitializeComponent();
        BuildSortMenu();
        DetailView.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnDetailPreviewKeyDown), handledEventsToo: false);
    }

    internal LibraryViewModel? ViewModel
    {
        get => _vm;
        set
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.ActiveFilters.CollectionChanged -= OnFiltersChanged;
            }

            _vm = value;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.ActiveFilters.CollectionChanged += OnFiltersChanged;
            }

            SyncAll();
        }
    }

    // x:Bind helpers
    public static bool Not(bool value) => !value;

    public static Visibility Hidden(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    // ───────────── view model → controls ─────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.Rows):
                SyncRows();
                break;
            case nameof(LibraryViewModel.CountText):
                CountText.Text = _vm!.CountText;
                break;
            case nameof(LibraryViewModel.EmptyState):
                SyncEmpty();
                break;
            case nameof(LibraryViewModel.ViewMode):
                SyncViewMode();
                break;
            case nameof(LibraryViewModel.Sort):
                SyncSortMenu();
                break;
            case nameof(LibraryViewModel.SelectedId):
                SyncSelection();
                break;
            case nameof(LibraryViewModel.Detail):
                SyncDetail();
                break;
        }
    }

    private void SyncAll()
    {
        SyncRows();
        SyncEmpty();
        SyncViewMode();
        SyncSortMenu();
        SyncFilterChips();
        SyncDetail();
    }

    private void SyncRows()
    {
        _syncing = true;
        try
        {
            NoteList.ItemsSource = _vm?.Rows;
            NoteGrid.ItemsSource = _vm?.Rows;
            CountText.Text = _vm?.CountText ?? "";
        }
        finally
        {
            _syncing = false;
        }

        SyncSelection();
    }

    private void SyncSelection()
    {
        var row = _vm?.SelectedId is { } id ? _vm.Rows.FirstOrDefault(r => r.Id == id) : null;
        _syncing = true;
        try
        {
            NoteList.SelectedItem = row;
            NoteGrid.SelectedItem = row;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncEmpty()
    {
        var state = _vm?.EmptyState ?? LibraryEmptyState.None;
        EmptyPanel.Visibility = state == LibraryEmptyState.None ? Visibility.Collapsed : Visibility.Visible;
        EmptyClearButton.Visibility = state == LibraryEmptyState.NoMatches ? Visibility.Visible : Visibility.Collapsed;
        (EmptyTitle.Text, EmptyMessage.Text) = state switch
        {
            LibraryEmptyState.NoMatches => ("No notes match these filters", ""),
            _ => ("No notes yet", "Right-click a folder in Explorer and choose Add folder note, or run FolderNotes --edit <folder>."),
        };
        EmptyMessage.Visibility = EmptyMessage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncViewMode()
    {
        var cards = _vm?.ViewMode == LibraryViewMode.Cards;
        ListToggle.IsChecked = !cards;
        CardsToggle.IsChecked = cards;
        NoteList.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        NoteGrid.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        SyncSelection();
    }

    private void BuildSortMenu()
    {
        foreach (var (sort, text) in SortOptions)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "sort", Tag = sort };
            item.Click += async (_, _) =>
            {
                if (_vm is not null)
                {
                    await _vm.SetSortAsync(sort);
                }
            };
            SortMenu.Items.Add(item);
        }
    }

    private void SyncSortMenu()
    {
        foreach (var item in SortMenu.Items.OfType<RadioMenuFlyoutItem>())
        {
            item.IsChecked = _vm is not null && (NoteSort)item.Tag == _vm.Sort;
        }

        var current = SortOptions.FirstOrDefault(o => o.Sort == (_vm?.Sort ?? NoteSort.UpdatedDesc)).Text;
        AutomationProperties.SetName(SortButton, "Sort: " + current);
        ToolTipService.SetToolTip(SortButton, "Sort: " + current);
    }

    private void OnFiltersChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncFilterChips();

    private void SyncFilterChips()
    {
        FilterChips.Children.Clear();
        if (_vm is null)
        {
            return;
        }

        foreach (var filter in _vm.ActiveFilters)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock { Text = filter.Text, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new FontIcon { Glyph = "", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Content = content, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
            AutomationProperties.SetName(chip, filter.RemoveName);
            chip.Click += async (_, _) => await _vm.RemoveFilterAsync(filter);
            FilterChips.Children.Add(chip);
        }

        FilterChips.Visibility = _vm.HasFilters ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(FilterButton, _vm.HasFilters ? $"Filter ({_vm.ActiveFilters.Count} active)" : "Filter");
    }

    private void SyncDetail()
    {
        var detail = _vm?.Detail;
        if (!ReferenceEquals(DetailView.ViewModel, detail))
        {
            DetailView.ViewModel = detail;
            DetailView.ResetForNewSession();
        }

        DetailView.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelectionText.Visibility = detail is null ? Visibility.Visible : Visibility.Collapsed;
        if (detail is null && _detailShown)
        {
            _detailShown = false; // e.g. the undo window ran out: back to the list
            ApplyLayout();
        }
    }

    // ───────────── layout ─────────────

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var width = XamlRoot?.Size.Width ?? Root.ActualWidth;
        _wide = width >= WideWidth;
        if (_wide)
        {
            MasterColumn.Width = new GridLength(Math.Max(MinMasterWidth, Root.ActualWidth * 0.45));
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(DetailPane, 1);
            MasterPane.Visibility = Visibility.Visible;
            DetailPane.Visibility = Visibility.Visible;
            DetailPane.BorderThickness = new Thickness(1, 0, 0, 0);
            BackButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            MasterColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
            Grid.SetColumn(DetailPane, 0);
            MasterPane.Visibility = _detailShown ? Visibility.Collapsed : Visibility.Visible;
            DetailPane.Visibility = _detailShown ? Visibility.Visible : Visibility.Collapsed;
            DetailPane.BorderThickness = default;
            BackButton.Visibility = Visibility.Visible;
        }
    }

    // ───────────── controls → view model ─────────────

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null || sender is not ListViewBase list)
        {
            return;
        }

        if (list.SelectedItem is LibraryRow row)
        {
            await _vm.SelectAsync(row.Id);
        }

        // A deselect caused by a reload or a removed row keeps the detail (05: selection is kept by id).
    }

    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not LibraryRow row || _vm is null)
        {
            return;
        }

        // A click selects too, but Enter and UIA Invoke raise only ItemClick.
        ((ListViewBase)sender).SelectedItem = row;
        await _vm.SelectAsync(row.Id);
        if (!_wide)
        {
            _detailShown = true; // the narrow layout swaps the list for the editor
            ApplyLayout();
        }
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.FlushAsync();
        }

        _detailShown = false;
        ApplyLayout();
        (NoteList.Visibility == Visibility.Visible ? NoteList : (ListViewBase)NoteGrid).Focus(FocusState.Programmatic);
    }

    private void OnViewToggle(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ViewMode = ReferenceEquals(sender, CardsToggle) ? LibraryViewMode.Cards : LibraryViewMode.List;
        }

        SyncViewMode(); // also re-checks the toggle that was clicked off
    }

    private void OnFilterMenuOpening(object? sender, object e)
    {
        FilterMenu.Items.Clear();
        if (_vm is null)
        {
            return;
        }

        var labels = new MenuFlyoutSubItem { Text = "Labels" };
        foreach (var label in _vm.AllLabels)
        {
            var item = new ToggleMenuFlyoutItem { Text = label.Name, IsChecked = _vm.IsLabelFiltered(label.Name) };
            item.Click += async (_, _) => await _vm.ToggleLabelFilterAsync(label.Name);
            labels.Items.Add(item);
        }

        labels.IsEnabled = labels.Items.Count > 0;
        FilterMenu.Items.Add(labels);

        var statuses = new MenuFlyoutSubItem { Text = "Status" };
        foreach (var visual in StatusVisuals.All.Where(v => v.HasPill))
        {
            var status = visual.Status;
            var item = new ToggleMenuFlyoutItem { Text = visual.Text, IsChecked = _vm.IsStatusFiltered(status) };
            item.Click += async (_, _) => await _vm.ToggleStatusFilterAsync(status);
            statuses.Items.Add(item);
        }

        FilterMenu.Items.Add(statuses);
        FilterMenu.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "Clear filters", IsEnabled = _vm.HasFilters };
        clear.Click += OnClearFilters;
        FilterMenu.Items.Add(clear);
    }

    private async void OnClearFilters(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await _vm.ClearFiltersAsync();
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LibraryRow row })
        {
            FolderLauncher.Open(row.PathDisplay);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: LibraryRow row })
        {
            var package = new DataPackage();
            package.SetText(row.PathDisplay);
            Clipboard.SetContent(package);
        }
    }

    private async void OnDetailPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Ctrl+Enter saves now (05). Esc has no meaning in the main window.
        if (e.Key == VirtualKey.Enter && _vm?.Detail is { } detail
            && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            await detail.SaveNowAsync();
        }
    }
}
