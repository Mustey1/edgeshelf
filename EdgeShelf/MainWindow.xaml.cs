using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using DragDropEffects = System.Windows.DragDropEffects;
using ComboBox = System.Windows.Controls.ComboBox;

namespace EdgeShelf;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x4553;
    private const int WmHotkey = 0x0312;
    private const int WmMouseActivate = 0x0021;
    private const int WmClipboardUpdate = 0x031D;
    private const int MaNoActivate = 3;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModAlt = 0x0001;
    private const uint VkSpace = 0x20;
    private const uint VkS = 0x53;
    private const double ExpandedWidth = 360;
    private const double ExpandedHeight = 640;
    private const double CollapsedWidth = 32;
    private const double CollapsedHeight = 160;

    private readonly DispatcherTimer _hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(210) };
    private readonly TranslateTransform _panelSlide = new();
    private readonly AppState _state = StateStore.Load();
    private readonly ShelfStore _shelf = new();
    private readonly ClipboardStore _clipboard = new();
    private readonly SnippetStore _snippets = new();
    private readonly GoogleCalendarClient _calendar = new();
    private readonly IcalCalendarClient _icalCalendar = new();
    private readonly ObservableCollection<CalendarEvent> _calendarEvents = new();
    private readonly DispatcherTimer _calendarTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer _clipboardRetry = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly System.Drawing.Icon _appIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _startupMenuItem;
    private HwndSource? _source;
    private bool _expanded;
    private bool _quitting;
    private bool _hotkeyRegistered;
    private bool _clipboardListenerRegistered;
    private uint _ignoredClipboardSequence;
    private uint _lastClipboardSequence;
    private uint _pendingClipboardSequence;
    private int _clipboardRetries;
    private bool _dragActive;
    private bool _outgoingDrag;
    private bool _menuOpen;
    private System.Windows.Point _dragStart;
    private ShelfItem? _dragItem;
    private ClipboardEntry? _clipboardDragItem;
    private bool _clipboardDragStarted;
    private Snippet? _editingSnippet;
    private bool _calendarLoading;
    private DateTimeOffset _calendarLastFetch;

    public MainWindow()
    {
        InitializeComponent();
        _state.StartWithWindows = IsStartupRegistered();
        PanelSurface.RenderTransform = _panelSlide;
        ShelfList.ItemsSource = _shelf.Items;
        UpdateShelfEmpty();
        ClipboardList.ItemsSource = _clipboard.Items;
        _clipboard.Items.CollectionChanged += (_, _) => UpdateClipboardEmpty();
        UpdateClipboardEmpty();
        var snippetsView = CollectionViewSource.GetDefaultView(_snippets.Items);
        snippetsView.Filter = item => item is Snippet snippet &&
            snippet.SearchText.Contains(SnippetsSearch.Text, StringComparison.OrdinalIgnoreCase);
        SnippetsList.ItemsSource = snippetsView;
        _snippets.Items.CollectionChanged += (_, _) => UpdateSnippetsEmpty();
        UpdateSnippetsEmpty();
        CalendarClientId.Text = _calendar.ClientId;
        CalendarList.ItemsSource = _calendarEvents;
        UpdateCalendarConnectionView();
        LoadSettingsControls();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_state.HoverDelayMs);
        _closeTimer.Interval = TimeSpan.FromMilliseconds(_state.CloseDelayMs);
        _clipboard.HistoryLimit = _state.ClipboardHistoryLimit is >= 10 and <= 500
            ? _state.ClipboardHistoryLimit : 100;
        Position(CollapsedHeight);
        SelectSection(_state.LastSection, false);

        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); Open(); };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            if (!IsMouseOver && !_dragActive && !_outgoingDrag && !_menuOpen) ClosePanel();
        };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (_expanded) return;
            _panelSlide.BeginAnimation(TranslateTransform.XProperty, null);
            _panelSlide.X = -ExpandedWidth;
            PanelSurface.Visibility = Visibility.Collapsed;
            Trigger.Visibility = Visibility.Visible;
            SetPanelGeometry(false);
        };
        _clipboardRetry.Tick += (_, _) =>
        {
            _clipboardRetry.Stop();
            CaptureClipboard();
        };
        _calendarTimer.Tick += async (_, _) =>
        {
            if (CalendarView.Visibility != Visibility.Visible || !IsCalendarConnected) return;
            if (DateTimeOffset.UtcNow - _calendarLastFetch > TimeSpan.FromMinutes(5))
                await RefreshCalendarAsync();
            else CalendarList.Items.Refresh();
        };
        _calendarTimer.Start();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(Open));
        _startupMenuItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = _state.StartWithWindows,
            CheckOnClick = true
        };
        _startupMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            var enabled = _startupMenuItem.Checked;
            if (SetStartupRegistration(enabled))
            {
                _state.StartWithWindows = enabled;
                StartWithWindowsCheck.IsChecked = enabled;
                StateStore.Save(_state);
            }
            else
            {
                _startupMenuItem.Checked = !enabled;
                _trayIcon?.ShowBalloonTip(4000, "Edge Shelf", "Could not update Windows startup.", Forms.ToolTipIcon.Warning);
            }
        });
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.Invoke(Quit));
        _appIcon = LoadAppIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Edge Shelf",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(Open);

        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _closeTimer.Stop();
            _collapseTimer.Stop();
            _clipboardRetry.Stop();
            _calendarTimer.Stop();
            if (_hotkeyRegistered && _source is not null)
                UnregisterHotKey(_source.Handle, HotkeyId);
            if (_clipboardListenerRegistered && _source is not null)
                RemoveClipboardFormatListener(_source.Handle);
            _source?.RemoveHook(WindowProc);
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _appIcon.Dispose();
            menu.Dispose();
        };
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                using var extracted = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (extracted is not null) return (System.Drawing.Icon)extracted.Clone();
            }
        }
        catch (Exception error) when (error is ArgumentException or ExternalException) { }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WindowProc);
        var (modifiers, key) = ShortcutKeys(_state.Shortcut);
        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, key);
        _clipboardListenerRegistered = AddClipboardFormatListener(_source.Handle);
        if (!_hotkeyRegistered)
            _trayIcon.ShowBalloonTip(4000, "Edge Shelf", "Ctrl + Shift + Space is already in use.", Forms.ToolTipIcon.Warning);
        if (!_clipboardListenerRegistered)
            _trayIcon.ShowBalloonTip(4000, "Edge Shelf", "Clipboard history could not be started.", Forms.ToolTipIcon.Warning);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmMouseActivate && !_expanded)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        if (message == WmClipboardUpdate)
        {
            handled = true;
            var sequence = GetClipboardSequenceNumber();
            if (sequence != _ignoredClipboardSequence && sequence != _lastClipboardSequence)
            {
                _pendingClipboardSequence = sequence;
                _clipboardRetries = 0;
                CaptureClipboard();
            }
            return IntPtr.Zero;
        }
        if (message == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            if (_expanded) ClosePanel(true); else Open();
        }
        return IntPtr.Zero;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _closeTimer.Stop();
        if (!_expanded) _hoverTimer.Start();
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hoverTimer.Stop();
        if (_expanded && !_dragActive && !_outgoingDrag && !_menuOpen
            && !IsTextInputActive) _closeTimer.Start();
    }

    private void Open()
    {
        _hoverTimer.Stop();
        _closeTimer.Stop();
        _collapseTimer.Stop();
        if (_expanded)
        {
            SetPanelGeometry(true);
            return;
        }
        var start = PanelSurface.Visibility == Visibility.Visible ? _panelSlide.X : -ExpandedWidth;
        _panelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _panelSlide.X = start;
        _expanded = true;
        SelectSection(_state.LastSection, false);
        Trigger.Visibility = Visibility.Collapsed;
        SetPanelGeometry(true);
        PanelSurface.Visibility = Visibility.Visible;
        _panelSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(start, 0,
            TimeSpan.FromMilliseconds(230))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void ClosePanel(bool force = false)
    {
        _hoverTimer.Stop();
        _closeTimer.Stop();
        if (!_expanded || (!force && (_dragActive || _outgoingDrag || _menuOpen
            || IsTextInputActive))) return;
        var start = _panelSlide.X;
        _panelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        _panelSlide.X = start;
        _expanded = false;
        _panelSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(start, -ExpandedWidth,
            TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        _collapseTimer.Start();
    }

    private void SetPanelGeometry(bool expanded)
    {
        var height = expanded ? ExpandedHeight : CollapsedHeight;
        Width = expanded ? ExpandedWidth : CollapsedWidth;
        Height = height;
        Position(height);
    }

    private void Position(double height)
    {
        Left = 0;
        Top = (SystemParameters.PrimaryScreenHeight - height) / 2;
    }

    private void Section_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string section }) SelectSection(section, true);
    }

    private void SelectSection(string section, bool persist)
    {
        if (section is not ("Shelf" or "Clipboard" or "Snippets" or "Calendar")) section = "Shelf";
        SectionTitle.Text = section;
        ShelfView.Visibility = section == "Shelf" ? Visibility.Visible : Visibility.Collapsed;
        ClipboardView.Visibility = section == "Clipboard" ? Visibility.Visible : Visibility.Collapsed;
        ClipboardClearButton.Visibility = section == "Clipboard" ? Visibility.Visible : Visibility.Collapsed;
        SnippetsView.Visibility = section == "Snippets" ? Visibility.Visible : Visibility.Collapsed;
        CalendarView.Visibility = section == "Calendar" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        if (section == "Calendar" && IsCalendarConnected
            && DateTimeOffset.UtcNow - _calendarLastFetch > TimeSpan.FromMinutes(1))
            Dispatcher.BeginInvoke(async () => await RefreshCalendarAsync(), DispatcherPriority.Background);
        foreach (var button in new[] { ShelfButton, ClipboardButton, SnippetsButton, CalendarButton })
        {
            var active = (string)button.Tag == section;
            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(48, 59, 75))
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = active
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 191, 204));
        }
        if (persist)
        {
            _state.LastSection = section;
            StateStore.Save(_state);
        }
    }

    private bool IsTextInputActive => SnippetsSearch.IsKeyboardFocusWithin
        || SnippetTitle.IsKeyboardFocusWithin
        || SnippetValue.IsKeyboardFocusWithin
        || SnippetCategory.IsKeyboardFocusWithin
        || CalendarClientId.IsKeyboardFocusWithin
        || CalendarIcalUrl.IsKeyboardFocusWithin
        || HistoryLimitText.IsKeyboardFocusWithin
        || ShortcutChoice.IsDropDownOpen
        || HoverDelayChoice.IsDropDownOpen
        || CloseDelayChoice.IsDropDownOpen;

    private void OpenSettings()
    {
        Open();
        ShelfView.Visibility = Visibility.Collapsed;
        ClipboardView.Visibility = Visibility.Collapsed;
        ClipboardClearButton.Visibility = Visibility.Collapsed;
        SnippetsView.Visibility = Visibility.Collapsed;
        CalendarView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SectionTitle.Text = "Settings";
        LoadSettingsControls();
    }

    private void LoadSettingsControls()
    {
        StartWithWindowsCheck.IsChecked = _state.StartWithWindows;
        AutoAddImagesCheck.IsChecked = _state.AutoAddCopiedImages;
        ClipboardEnabledCheck.IsChecked = _state.ClipboardEnabled;
        HistoryLimitText.Text = _state.ClipboardHistoryLimit.ToString();
        SelectComboValue(ShortcutChoice, _state.Shortcut);
        SelectComboValue(HoverDelayChoice, _state.HoverDelayMs.ToString());
        SelectComboValue(CloseDelayChoice, _state.CloseDelayMs.ToString());
        ShortcutHint.Text = _state.Shortcut + " to toggle";
        SettingsStatus.Text = "";
    }

    private static void SelectComboValue(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString() ?? item.Content?.ToString(), value,
                    StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HistoryLimitText.Text, out var limit) || limit is < 10 or > 500)
        {
            SettingsStatus.Text = "History limit must be between 10 and 500.";
            return;
        }
        var shortcut = (ShortcutChoice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Ctrl+Shift+Space";
        var hover = int.Parse((HoverDelayChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "200");
        var close = int.Parse((CloseDelayChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "550");
        var oldShortcut = _state.Shortcut;
        if (!ChangeShortcut(shortcut))
        {
            SettingsStatus.Text = "That shortcut is already in use. Choose another one.";
            return;
        }
        if (!SetStartupRegistration(StartWithWindowsCheck.IsChecked == true))
        {
            ChangeShortcut(oldShortcut);
            SettingsStatus.Text = "Could not update Windows startup.";
            return;
        }
        if (!_clipboard.SetHistoryLimit(limit))
        {
            ChangeShortcut(oldShortcut);
            SetStartupRegistration(_state.StartWithWindows);
            SettingsStatus.Text = "Could not save the history limit.";
            return;
        }
        _state.Shortcut = shortcut;
        _state.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        _startupMenuItem.Checked = _state.StartWithWindows;
        _state.HoverDelayMs = hover;
        _state.CloseDelayMs = close;
        _state.AutoAddCopiedImages = AutoAddImagesCheck.IsChecked == true;
        _state.ClipboardEnabled = ClipboardEnabledCheck.IsChecked == true;
        _state.ClipboardHistoryLimit = limit;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(hover);
        _closeTimer.Interval = TimeSpan.FromMilliseconds(close);
        ShortcutHint.Text = shortcut + " to toggle";
        StateStore.Save(_state);
        SettingsStatus.Text = "Saved.";
    }

    private bool ChangeShortcut(string shortcut)
    {
        if (_source is null) return false;
        var old = ShortcutKeys(_state.Shortcut);
        if (_hotkeyRegistered) UnregisterHotKey(_source.Handle, HotkeyId);
        var next = ShortcutKeys(shortcut);
        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, next.Modifiers, next.Key);
        if (_hotkeyRegistered) return true;
        _hotkeyRegistered = RegisterHotKey(_source.Handle, HotkeyId, old.Modifiers, old.Key);
        return false;
    }

    private static (uint Modifiers, uint Key) ShortcutKeys(string shortcut) => shortcut switch
    {
        "Ctrl+Alt+Space" => (ModControl | ModAlt, VkSpace),
        "Ctrl+Shift+S" => (ModControl | ModShift, VkS),
        "Ctrl+Alt+S" => (ModControl | ModAlt, VkS),
        _ => (ModControl | ModShift, VkSpace)
    };

    private static bool SetStartupRegistration(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key is null) return false;
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable) || !Path.GetFileName(executable).Equals("EdgeShelf.exe", StringComparison.OrdinalIgnoreCase))
                    return false;
                key.SetValue("EdgeShelf", $"\"{executable}\"");
            }
            else key.DeleteValue("EdgeShelf", false);
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool IsStartupRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var command = key?.GetValue("EdgeShelf") as string;
            return StartupCommandMatches(command, Environment.ProcessPath);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException) { return false; }
    }

    private static bool StartupCommandMatches(string? command, string? executable)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            var registeredPath = command.Trim();
            if (registeredPath.Length >= 2 && registeredPath[0] == '"')
            {
                var closingQuote = registeredPath.IndexOf('"', 1);
                if (closingQuote > 1) registeredPath = registeredPath[1..closingQuote];
            }
            else
            {
                var argumentStart = registeredPath.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
                if (argumentStart >= 0) registeredPath = registeredPath[..(argumentStart + 4)];
            }
            return string.Equals(Path.GetFullPath(registeredPath), Path.GetFullPath(executable),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private void UpdateCalendarConnectionView()
    {
        CalendarSetup.Visibility = IsCalendarConnected ? Visibility.Collapsed : Visibility.Visible;
        CalendarConnected.Visibility = IsCalendarConnected ? Visibility.Visible : Visibility.Collapsed;
        CalendarEmpty.Visibility = _calendarEvents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsCalendarConnected => _icalCalendar.IsConnected || _calendar.IsConnected;

    private void CalendarIcalHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://support.google.com/calendar/answer/37648")
                { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    private async void CalendarIcalConnect_Click(object sender, RoutedEventArgs e)
    {
        CalendarIcalConnectButton.IsEnabled = false;
        CalendarConnectStatus.Text = "Checking calendar link…";
        try
        {
            await _icalCalendar.ConnectAsync(CalendarIcalUrl.Text);
            _calendar.Disconnect();
            CalendarIcalUrl.Clear();
            CalendarConnectStatus.Text = "";
            UpdateCalendarConnectionView();
            await RefreshCalendarAsync();
        }
        catch (Exception error)
        {
            CalendarConnectStatus.Text = error.Message;
        }
        finally { CalendarIcalConnectButton.IsEnabled = true; }
    }

    private void CalendarHelp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://console.cloud.google.com/auth/clients")
                { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    private async void CalendarConnect_Click(object sender, RoutedEventArgs e)
    {
        CalendarConnectButton.IsEnabled = false;
        CalendarConnectStatus.Text = "Opening Google authorization in your browser…";
        try
        {
            await _calendar.ConnectAsync(CalendarClientId.Text);
            _icalCalendar.Disconnect();
            CalendarConnectStatus.Text = "";
            UpdateCalendarConnectionView();
            await RefreshCalendarAsync();
        }
        catch (OperationCanceledException)
        {
            CalendarConnectStatus.Text = "Connection timed out. Try again.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or HttpRequestException or IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or JsonException)
        {
            CalendarConnectStatus.Text = error.Message;
        }
        finally { CalendarConnectButton.IsEnabled = true; }
    }

    private async void CalendarRefresh_Click(object sender, RoutedEventArgs e) => await RefreshCalendarAsync();

    private async Task RefreshCalendarAsync()
    {
        if (_calendarLoading || !IsCalendarConnected) return;
        _calendarLoading = true;
        CalendarStatus.Text = "Loading today’s events…";
        try
        {
            var events = _icalCalendar.IsConnected
                ? await _icalCalendar.GetTodayEventsAsync()
                : await _calendar.GetTodayEventsAsync();
            _calendarEvents.Clear();
            foreach (var item in events) _calendarEvents.Add(item);
            _calendarLastFetch = DateTimeOffset.UtcNow;
            CalendarStatus.Text = events.Count == 0 ? "Today" : $"Today · {events.Count} upcoming";
            UpdateCalendarConnectionView();
        }
        catch (Exception error)
        {
            CalendarStatus.Text = "Could not load events: " + error.Message;
        }
        finally { _calendarLoading = false; }
    }

    private void CalendarDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _calendar.Disconnect();
        _icalCalendar.Disconnect();
        _calendarEvents.Clear();
        CalendarConnectStatus.Text = "";
        UpdateCalendarConnectionView();
    }

    private void CalendarJoin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: CalendarEvent item }
            || !item.CanJoin) return;
        try { Process.Start(new ProcessStartInfo(item.MeetingUrl) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    private void TextInput_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_expanded && !IsMouseOver && !IsTextInputActive) _closeTimer.Start();
        }, DispatcherPriority.Background);
    }

    private void SnippetsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SnippetsList is null) return;
        CollectionViewSource.GetDefaultView(SnippetsList.ItemsSource)?.Refresh();
        UpdateSnippetsEmpty();
    }

    private void UpdateSnippetsEmpty()
    {
        if (SnippetsEmpty is null || SnippetsList is null) return;
        var view = CollectionViewSource.GetDefaultView(SnippetsList.ItemsSource);
        SnippetsEmpty.Text = _snippets.Items.Count == 0 ? "No snippets yet" : "No matches";
        SnippetsEmpty.Visibility = view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SnippetNew_Click(object sender, RoutedEventArgs e) => EditSnippet(null);

    private void EditSnippet(Snippet? snippet)
    {
        _editingSnippet = snippet;
        SnippetFormTitle.Text = snippet is null ? "New snippet" : "Edit snippet";
        SnippetTitle.Text = snippet?.Title ?? "";
        SnippetValue.Text = snippet?.Value ?? "";
        SnippetCategory.Text = snippet?.Category ?? "";
        SnippetValidation.Text = "";
        SnippetForm.Visibility = Visibility.Visible;
        SnippetTitle.Focus();
    }

    private void SnippetCancel_Click(object sender, RoutedEventArgs e)
    {
        SnippetForm.Visibility = Visibility.Collapsed;
        _editingSnippet = null;
    }

    private void SnippetSave_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SnippetTitle.Text) || string.IsNullOrWhiteSpace(SnippetValue.Text))
        {
            SnippetValidation.Text = "Title and value are required.";
            return;
        }
        var saved = _editingSnippet is null
            ? _snippets.Add(SnippetTitle.Text, SnippetValue.Text, SnippetCategory.Text) is not null
            : _snippets.Update(_editingSnippet, SnippetTitle.Text, SnippetValue.Text, SnippetCategory.Text);
        if (!saved)
        {
            SnippetValidation.Text = "Could not save the snippet.";
            return;
        }
        SnippetForm.Visibility = Visibility.Collapsed;
        _editingSnippet = null;
        CollectionViewSource.GetDefaultView(SnippetsList.ItemsSource)?.Refresh();
        UpdateSnippetsEmpty();
    }

    private void SnippetsList_Click(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(SnippetsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is Snippet snippet) CopySnippet(snippet);
    }

    private void SnippetsList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(SnippetsList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is not Snippet snippet) return;
        var menu = new ContextMenu { PlacementTarget = row };
        AddAction("Copy", () => CopySnippet(snippet));
        AddAction("Edit", () => EditSnippet(snippet));
        AddAction("Duplicate", () => { _snippets.Duplicate(snippet); UpdateSnippetsEmpty(); });
        menu.Items.Add(new Separator());
        AddAction("Delete", () => { _snippets.Remove(snippet); UpdateSnippetsEmpty(); });
        void AddAction(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        _menuOpen = true;
        _closeTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsMouseOver && !IsTextInputActive) _closeTimer.Start();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CopySnippet(Snippet snippet)
    {
        try
        {
            System.Windows.Clipboard.SetText(snippet.Value);
            _ignoredClipboardSequence = GetClipboardSequenceNumber();
            _lastClipboardSequence = _ignoredClipboardSequence;
        }
        catch (ExternalException) { }
    }

    private void CaptureClipboard()
    {
        if (_pendingClipboardSequence == 0 || _pendingClipboardSequence == _ignoredClipboardSequence
            || _pendingClipboardSequence == _lastClipboardSequence) return;
        try
        {
            if (GetClipboardSequenceNumber() != _pendingClipboardSequence) return;
            var data = System.Windows.Clipboard.GetDataObject();
            if (data is null) return;
            if (data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                && data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            {
                if (_state.ClipboardEnabled) _clipboard.AddFiles(paths);
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                var image = System.Windows.Clipboard.GetImage();
                if (image is not null)
                {
                    var newEntry = !_state.ClipboardEnabled || _clipboard.AddImage(image);
                    if (_state.AutoAddCopiedImages && newEntry)
                    {
                        _shelf.AddImage(image);
                        UpdateShelfEmpty();
                    }
                }
            }
            else if (System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            {
                if (_state.ClipboardEnabled)
                    _clipboard.AddText(System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText));
            }
            _lastClipboardSequence = _pendingClipboardSequence;
            UpdateClipboardEmpty();
        }
        catch (ExternalException)
        {
            if (++_clipboardRetries <= 3) _clipboardRetry.Start();
        }
    }

    private void UpdateClipboardEmpty()
    {
        if (ClipboardEmpty is null || ClipboardList is null) return;
        ClipboardEmpty.Visibility = _clipboard.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClipboardClear_Click(object sender, RoutedEventArgs e)
    {
        _clipboard.Clear();
        UpdateClipboardEmpty();
    }

    private void ClipboardList_Click(object sender, MouseButtonEventArgs e)
    {
        if (_clipboardDragStarted || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        var row = ItemsControl.ContainerFromElement(ClipboardList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is ClipboardEntry entry) CopyClipboardEntry(entry);
    }

    private void ClipboardList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _clipboardDragStarted = false;
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            _clipboardDragItem = null;
            return;
        }
        _dragStart = e.GetPosition(ClipboardList);
        _clipboardDragItem = ItemsControl.ContainerFromElement(ClipboardList, e.OriginalSource as DependencyObject)
            is ListBoxItem row ? row.DataContext as ClipboardEntry : null;
    }

    private void ClipboardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ClipboardEntry entry }) return;
        _clipboardDragItem = null;
        _clipboard.Remove(entry);
        e.Handled = true;
    }

    private void ClipboardList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_clipboardDragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(ClipboardList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var entry = _clipboardDragItem;
        _clipboardDragItem = null;
        var data = CreateClipboardDragData(entry);
        if (data is null) return;
        _clipboardDragStarted = true;
        _outgoingDrag = true;
        _closeTimer.Stop();
        try { System.Windows.DragDrop.DoDragDrop(ClipboardList, data, DragDropEffects.Copy); }
        finally
        {
            _outgoingDrag = false;
            if (!IsMouseOver) _closeTimer.Start();
        }
    }

    private static System.Windows.DataObject? CreateClipboardDragData(ClipboardEntry entry)
    {
        var data = new System.Windows.DataObject();
        switch (entry.Kind)
        {
            case ClipboardKind.Text:
            case ClipboardKind.Url:
                data.SetText(entry.Text);
                return data;
            case ClipboardKind.Image:
                if (!File.Exists(entry.ImagePath)) return null;
                data.SetData(System.Windows.DataFormats.FileDrop, new[] { entry.ImagePath });
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(entry.ImagePath);
                    image.EndInit();
                    image.Freeze();
                    data.SetImage(image);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException
                    or ArgumentException or NotSupportedException) { }
                return data;
            case ClipboardKind.Files:
                var paths = entry.Paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
                if (paths.Length == 0) return null;
                data.SetData(System.Windows.DataFormats.FileDrop, paths);
                return data;
            default:
                return null;
        }
    }

    private void ClipboardList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ClipboardList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is not ClipboardEntry entry) return;
        var menu = new ContextMenu { PlacementTarget = row };
        AddAction("Copy", () => CopyClipboardEntry(entry));
        AddAction("Pin to Shelf", () => PinClipboardEntryToShelf(entry));
        AddAction(entry.PinLabel, () => _clipboard.TogglePin(entry));
        menu.Items.Add(new Separator());
        AddAction("Delete", () => _clipboard.Remove(entry));
        void AddAction(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        _menuOpen = true;
        _closeTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsMouseOver) _closeTimer.Start();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void CopyClipboardEntry(ClipboardEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case ClipboardKind.Text:
                case ClipboardKind.Url:
                    System.Windows.Clipboard.SetText(entry.Text);
                    break;
                case ClipboardKind.Image:
                    if (File.Exists(entry.ImagePath))
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.UriSource = new Uri(entry.ImagePath);
                        image.EndInit();
                        System.Windows.Clipboard.SetImage(image);
                    }
                    break;
                case ClipboardKind.Files:
                    var paths = new StringCollection();
                    paths.AddRange(entry.Paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray());
                    if (paths.Count > 0) System.Windows.Clipboard.SetFileDropList(paths);
                    break;
            }
            _ignoredClipboardSequence = GetClipboardSequenceNumber();
            _lastClipboardSequence = _ignoredClipboardSequence;
        }
        catch (Exception error) when (error is ExternalException or IOException or UnauthorizedAccessException) { }
    }

    private void PinClipboardEntryToShelf(ClipboardEntry entry)
    {
        switch (entry.Kind)
        {
            case ClipboardKind.Text:
            case ClipboardKind.Url:
                _shelf.AddText(entry.Text);
                break;
            case ClipboardKind.Image:
                _shelf.AddImageFile(entry.ImagePath);
                break;
            case ClipboardKind.Files:
                _shelf.AddPaths(entry.Paths);
                break;
        }
        UpdateShelfEmpty();
    }

    private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (_outgoingDrag) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        if (!CanAcceptDrop(e.Data)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _dragActive = true;
        _hoverTimer.Stop();
        _closeTimer.Stop();
        Open();
        SelectSection("Shelf", false);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (_outgoingDrag) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        _dragActive = CanAcceptDrop(e.Data);
        if (_dragActive) _closeTimer.Stop();
        e.Effects = _dragActive ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        if (_outgoingDrag) return;
        _dragActive = false;
        if (_expanded && !IsMouseOver) _closeTimer.Start();
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (_outgoingDrag) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        try
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
            {
                _shelf.AddPaths(paths);
            }
            else
            {
                var image = ReadDroppedImage(e.Data);
                if (image is not null) _shelf.AddImage(image);
            }
            UpdateShelfEmpty();
            e.Effects = DragDropEffects.Copy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or ExternalException)
        {
            e.Effects = DragDropEffects.None;
        }
        finally
        {
            _dragActive = false;
            e.Handled = true;
            if (!IsMouseOver) _closeTimer.Start();
        }
    }

    private static bool CanAcceptDrop(System.Windows.IDataObject data)
    {
        try
        {
            return data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                || data.GetDataPresent(System.Windows.DataFormats.Bitmap)
                || data.GetDataPresent(typeof(BitmapSource));
        }
        catch (ExternalException) { return false; }
    }

    private static BitmapSource? ReadDroppedImage(System.Windows.IDataObject data)
    {
        var raw = data.GetData(typeof(BitmapSource)) ?? data.GetData(System.Windows.DataFormats.Bitmap);
        if (raw is BitmapSource source) return source;
        if (raw is System.Drawing.Bitmap bitmap)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        return null;
    }

    private void UpdateShelfEmpty() =>
        ShelfEmpty.Visibility = _shelf.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShelfList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        _dragStart = e.GetPosition(ShelfList);
        _dragItem = ItemsControl.ContainerFromElement(ShelfList, e.OriginalSource as DependencyObject)
            is ListBoxItem row ? row.DataContext as ShelfItem : null;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button) return true;
            source = source is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ShelfList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(ShelfList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var path = _dragItem.Path;
        _dragItem = null;
        if (!File.Exists(path) && !Directory.Exists(path)) return;
        var data = new System.Windows.DataObject();
        data.SetData(System.Windows.DataFormats.FileDrop, new[] { path });
        _outgoingDrag = true;
        _closeTimer.Stop();
        try { System.Windows.DragDrop.DoDragDrop(ShelfList, data, DragDropEffects.Copy); }
        finally
        {
            _outgoingDrag = false;
            if (!IsMouseOver) _closeTimer.Start();
        }
    }

    private void ShelfList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (ShelfList.SelectedItem is ShelfItem item) OpenItem(item);
    }

    private void ShelfDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ShelfItem item }) return;
        _dragItem = null;
        DoShelfAction(item, "Remove");
        e.Handled = true;
    }

    private void ShelfList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ShelfList, e.OriginalSource as DependencyObject) as ListBoxItem;
        if (row?.DataContext is not ShelfItem item) return;
        var menu = new ContextMenu { PlacementTarget = row };
        AddAction("Open", "Open");
        AddAction("Copy", "Copy");
        AddAction("Show in Explorer", "Show");
        menu.Items.Add(new Separator());
        AddAction("Remove from Shelf", "Remove");
        void AddAction(string title, string action)
        {
            var entry = new MenuItem { Header = title };
            entry.Click += (_, _) => DoShelfAction(item, action);
            menu.Items.Add(entry);
        }
        _menuOpen = true;
        _closeTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (!IsMouseOver) _closeTimer.Start();
        };
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void DoShelfAction(ShelfItem item, string action)
    {
        switch (action)
        {
            case "Open": OpenItem(item); break;
            case "Copy": CopyItem(item); break;
            case "Show": ShowItem(item); break;
            case "Remove": _shelf.Remove(item); UpdateShelfEmpty(); break;
        }
    }

    private void OpenItem(ShelfItem item)
    {
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
        try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or IOException) { }
    }

    private void CopyItem(ShelfItem item)
    {
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
        try
        {
            if (item.IsInternal && item.IsImage && item.Preview is BitmapSource image)
                System.Windows.Clipboard.SetImage(image);
            else
            {
                var paths = new StringCollection { item.Path };
                System.Windows.Clipboard.SetFileDropList(paths);
            }
            _ignoredClipboardSequence = GetClipboardSequenceNumber();
            _lastClipboardSequence = _ignoredClipboardSequence;
        }
        catch (Exception e) when (e is ExternalException or IOException) { }
    }

    private void ShowItem(ShelfItem item)
    {
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return;
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.Arguments = item.IsFolder ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"";
            Process.Start(info);
        }
        catch (Exception e) when (e is Win32Exception or IOException) { }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        ClosePanel();
    }

    private void Quit()
    {
        _quitting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
