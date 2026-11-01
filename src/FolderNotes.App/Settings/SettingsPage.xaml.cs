using System.ComponentModel;
using FolderNotes.App.Themes;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;
using Windows.System;
using Windows.UI.Core;

namespace FolderNotes.App.Settings;

/// <summary>Thin view over <see cref="SettingsViewModel"/> (specs/05 § Settings page).</summary>
public sealed partial class SettingsPage : UserControl
{
    private SettingsViewModel? _vm;
    private bool _syncing;

    public SettingsPage()
    {
        InitializeComponent();
    }

    internal SettingsViewModel? ViewModel
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
                VersionText.Text = $"Folder Notes {_vm.Version} · Notes are stored on this PC only.";
                DataFolderText.Text = _vm.DataFolder;
                AutomationProperties.SetName(ResetButton, "Reset search shortcut to " + SettingsViewModel.DefaultHotkeyText);
            }

            Sync();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

    private void Sync()
    {
        if (_vm is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            ThemeChoice.SelectedIndex = (int)_vm.Theme;
            Show(ThemeError, _vm.ThemeError);

            HotkeyText.Text = _vm.IsCapturing ? "…" : _vm.HotkeyText;
            AutomationProperties.SetName(HotkeyText, _vm.HotkeyAccessibleName);
            ChangeButton.Content = _vm.IsCapturing ? "Press keys…" : "Change";
            HotkeyBox.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                _vm.IsCapturing ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"];
            Show(HotkeyMessage, _vm.HotkeyMessage);
            HotkeyMessage.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                _vm.HotkeyMessageIsError ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush"];
            HotkeyWarning.Message = _vm.HotkeyWarning ?? "";
            HotkeyWarning.IsOpen = _vm.HotkeyWarning is not null;

            StartupToggle.IsOn = _vm.StartWithWindows;
            Show(StartupError, _vm.StartupError);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Show(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _vm is null || ThemeChoice.SelectedIndex < 0)
        {
            return;
        }

        await Run(() => _vm.SetThemeAsync((AppTheme)ThemeChoice.SelectedIndex), "Changing the theme");
    }

    private void OnChangeHotkey(object sender, RoutedEventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.IsCapturing)
        {
            _vm.CancelCapture();
        }
        else
        {
            _vm.BeginCapture(); // the button keeps focus and receives the next key press
        }
    }

    private async void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is not { IsCapturing: true })
        {
            return;
        }

        e.Handled = true; // while capturing, keys are the shortcut, not button input
        uint mods = 0;
        if (Down(VirtualKey.Control))
        {
            mods |= Services.HotkeyGesture.ModControl;
        }

        if (Down(VirtualKey.Menu))
        {
            mods |= Services.HotkeyGesture.ModAlt;
        }

        if (Down(VirtualKey.Shift))
        {
            mods |= Services.HotkeyGesture.ModShift;
        }

        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows))
        {
            mods |= Services.HotkeyGesture.ModWin;
        }

        await Run(() => _vm.OnKeyPressAsync(mods, (uint)e.Key), "Capturing the shortcut");
    }

    private static bool Down(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnCaptureLostFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is { IsCapturing: true })
        {
            _vm.CancelCapture();
        }
    }

    private async void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            await Run(_vm.ResetHotkeyAsync, "Resetting the shortcut");
        }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_syncing && _vm is not null && StartupToggle.IsOn != _vm.StartWithWindows)
        {
            _vm.SetStartWithWindows(StartupToggle.IsOn);
        }
    }

    private void OnManageLabels(object sender, RoutedEventArgs e) => _vm?.ManageLabels();

    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => _vm?.OpenDataFolder();

    private static async Task Run(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
#pragma warning disable CA1031 // A handler must not crash the UI thread; the view model already shows its errors.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Error(ex, "{What} failed", what);
        }
    }
}
