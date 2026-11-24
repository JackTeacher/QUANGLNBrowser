using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using QUANGLNBrowser.App.Services;
using QUANGLNBrowser.App.ViewModels;
using QUANGLNBrowser.Engine;
using Wpf.Ui.Controls;

namespace QUANGLNBrowser.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private ContextMenu? _activeBrowserContextMenu;
    private bool _isCyclicSuggestionPreview;
    private bool _isFullscreen = true;
    private WindowState _previousWindowState = WindowState.Normal;

    public MainWindow() : this(sharedSession: null, isIncognitoWindow: false, startWithWarpVpn: false, initialOverrideUrl: null)
    {
    }

    public MainWindow(
        BrowserSessionServices? sharedSession,
        bool isIncognitoWindow = false,
        bool startWithWarpVpn = false,
        string? initialOverrideUrl = null)
    {
        InitializeComponent();
        _viewModel = sharedSession is null
            ? new MainViewModel()
            : new MainViewModel(sharedSession, isIncognitoWindow, startWithWarpVpn, initialOverrideUrl);
        _viewModel.ContextMenuRequested += OnBrowserContextMenuRequested;
        DataContext = _viewModel;
        Loaded += OnMainWindowLoaded;
        StateChanged += OnMainWindowStateChanged;
    }

    private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnMainWindowLoaded;
        // WPF-UI FluentWindow cần hoàn tất khởi tạo WindowChrome/Mica trước khi đặt Maximized
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
        });
    }

    private void OnMainWindowStateChanged(object? sender, EventArgs e)
    {
        _isFullscreen = WindowState == WindowState.Maximized;
    }

    private void OnBrowserContextMenuRequested(object? sender, BrowserContextMenuInfo info)
    {
        try
        {
            if (_activeBrowserContextMenu is not null)
            {
                _activeBrowserContextMenu.IsOpen = false;
            }

            var actions = _viewModel.BuildContextMenuActions(this, BrowserHostGrid);
            var menu = BrowserContextMenuBuilder.Build(info, actions);
            menu.PlacementTarget = BrowserHostGrid;
            menu.Placement = PlacementMode.MousePoint;
            menu.StaysOpen = false;
            _activeBrowserContextMenu = menu;
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            QUANGLNBrowser.Core.Services.AppLogger.Error("Failed to display custom browser context menu", ex);
        }
    }

    internal Func<bool> GetVpnUsageProbe() => _viewModel.HasActiveVpnTraffic;

    private void OnOmniboxGotFocus(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsLocked)
        {
            return;
        }
        _viewModel.UpdateOmniboxSuggestions(OmniboxInput.Text);
    }

    private void OnOmniboxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isCyclicSuggestionPreview || !OmniboxInput.IsKeyboardFocusWithin || _viewModel.IsLocked)
        {
            return;
        }

        _viewModel.UpdateOmniboxSuggestions(OmniboxInput.Text);
    }

    private void OnOmniboxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _viewModel.IsOmniboxSuggestionOpen)
        {
            _isCyclicSuggestionPreview = true;
            try
            {
                _viewModel.MoveOmniboxSuggestionSelection(+1);
                OmniboxInput.CaretIndex = OmniboxInput.Text.Length;
            }
            finally
            {
                _isCyclicSuggestionPreview = false;
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up && _viewModel.IsOmniboxSuggestionOpen)
        {
            _isCyclicSuggestionPreview = true;
            try
            {
                _viewModel.MoveOmniboxSuggestionSelection(-1);
                OmniboxInput.CaretIndex = OmniboxInput.Text.Length;
            }
            finally
            {
                _isCyclicSuggestionPreview = false;
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _viewModel.IsOmniboxSuggestionOpen)
        {
            _viewModel.IsOmniboxSuggestionOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_viewModel.IsOmniboxSuggestionOpen && _viewModel.GetSelectedOmniboxSuggestion() is { } selected)
            {
                _viewModel.AcceptOmniboxSuggestionCommand.Execute(selected);
            }
            else if (_viewModel.NavigateCommand.CanExecute(null))
            {
                _viewModel.NavigateCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                _viewModel.FindPreviousCommand.Execute(null);
            }
            else
            {
                _viewModel.FindNextCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.CloseFindBarCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnToggleWindowFullscreenClicked(object sender, RoutedEventArgs e)
    {
        _viewModel.IsAppMenuOpen = false;
        ToggleWindowFullscreen();
    }

    private void ToggleWindowFullscreen()
    {
        if (!_isFullscreen)
        {
            _previousWindowState = WindowState;
            _isFullscreen = true;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _isFullscreen = false;
            WindowState = _previousWindowState;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.Control && !_viewModel.IsLocked)
        {
            OmniboxInput.Focus();
            OmniboxInput.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11 && !_viewModel.IsLocked)
        {
            ToggleWindowFullscreen();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        StateChanged -= OnMainWindowStateChanged;
        _viewModel.ContextMenuRequested -= OnBrowserContextMenuRequested;
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
