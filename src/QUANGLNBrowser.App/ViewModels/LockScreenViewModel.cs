using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QUANGLNBrowser.Core.Interfaces;
using QUANGLNBrowser.Core.Models;
using QUANGLNBrowser.Core.Services;

namespace QUANGLNBrowser.App.ViewModels;

/// <summary>
/// ViewModel cho màn hình khóa / khởi tạo Vault (Argon2id + Windows Hello + Decoy PIN).
/// </summary>
public sealed partial class LockScreenViewModel : ObservableObject
{
    private readonly IVaultService _vaultService;
    private readonly Action<ProfileInfo> _onUnlocked;

    [ObservableProperty]
    private bool _isFirstRun;

    [ObservableProperty]
    private string _passwordInput = string.Empty;

    [ObservableProperty]
    private string _confirmPasswordInput = string.Empty;

    [ObservableProperty]
    private string _decoyPinInput = string.Empty;

    [ObservableProperty]
    private bool _enableWindowsHello;

    [ObservableProperty]
    private bool _isWindowsHelloAvailable;

    [ObservableProperty]
    private bool _canUseWindowsHelloUnlock;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _clockText = DateTime.Now.ToString("HH:mm");

    [ObservableProperty]
    private string _solarDateText = VietnameseCalendarService.FormatSolarDate(DateTime.Now);

    [ObservableProperty]
    private string _lunarDateText = VietnameseCalendarService.FormatLunarDateBadge(DateTime.Now);

    [ObservableProperty]
    private string _weatherTempText = "31°C";

    [ObservableProperty]
    private string _weatherCityText = "TP. Hồ Chí Minh";

    [ObservableProperty]
    private string _weatherDetailText = "Nắng nhẹ · Độ ẩm 68%";

    [ObservableProperty]
    private string _weatherIconGlyph = "⛅";

    private readonly DispatcherTimer _clockTimer;
    private DateTime _lastWeatherRefreshUtc = DateTime.MinValue;
    private bool _isFetchingWeather;

    public LockScreenViewModel(IVaultService vaultService, Action<ProfileInfo> onUnlocked)
    {
        _vaultService = vaultService;
        _onUnlocked = onUnlocked;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateClockAndDate();
            _ = RefreshWeatherIfNeededAsync();
        };
        _clockTimer.Start();

        RefreshState();
    }

    private void UpdateClockAndDate()
    {
        var now = DateTime.Now;
        ClockText = now.ToString("HH:mm");
        SolarDateText = VietnameseCalendarService.FormatSolarDate(now);
        LunarDateText = VietnameseCalendarService.FormatLunarDateBadge(now);
    }

    private async Task RefreshWeatherIfNeededAsync()
    {
        if (_isFetchingWeather || DateTime.UtcNow - _lastWeatherRefreshUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        _isFetchingWeather = true;
        try
        {
            var weather = await VietnameseCalendarService.FetchLiveWeatherAsync();
            _lastWeatherRefreshUtc = DateTime.UtcNow;
            WeatherTempText = weather.TemperatureText;
            WeatherCityText = weather.CityName;
            WeatherDetailText = weather.ConditionAndHumidityText;
            WeatherIconGlyph = weather.IconGlyph;
        }
        finally
        {
            _isFetchingWeather = false;
        }
    }

    public void RefreshState()
    {
        IsFirstRun = !_vaultService.IsInitialized;
        CanUseWindowsHelloUnlock = !IsFirstRun && _vaultService.IsWindowsHelloEnabled;
        HasError = false;
        ErrorMessage = string.Empty;
        ClearSensitiveInputs();
        UpdateClockAndDate();
        _ = CheckWindowsHelloAvailabilityAsync();
        _ = RefreshWeatherIfNeededAsync();
    }

    private async Task CheckWindowsHelloAvailabilityAsync()
    {
        IsWindowsHelloAvailable = await _vaultService.IsWindowsHelloAvailableAsync();
        if (IsFirstRun && IsWindowsHelloAvailable)
        {
            EnableWindowsHello = true;
        }
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (IsFirstRun)
        {
            await SetupVaultAsync();
            return;
        }

        string pin = PasswordInput;
        PasswordInput = string.Empty; // Xóa ngay khỏi bộ nhớ ViewModel theo quy tắc bảo mật

        if (string.IsNullOrEmpty(pin))
        {
            ShowError("Vui lòng nhập PIN hoặc Master Password.");
            return;
        }

        IsBusy = true;
        ClearError();
        try
        {
            var result = await _vaultService.UnlockWithPinAsync(pin);
            if (result.Success && result.Profile is not null)
            {
                _onUnlocked(result.Profile);
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Xác thực thất bại.");
            }
        }
        finally
        {
            pin = string.Empty;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetupVaultAsync()
    {
        if (IsBusy)
        {
            return;
        }

        string pin = PasswordInput;
        string confirm = ConfirmPasswordInput;
        string? decoy = string.IsNullOrWhiteSpace(DecoyPinInput) ? null : DecoyPinInput;
        ClearSensitiveInputs();

        if (string.IsNullOrWhiteSpace(pin) || pin.Length < 4)
        {
            ShowError("PIN / Master Password phải có ít nhất 4 ký tự.");
            return;
        }

        if (!string.Equals(pin, confirm, StringComparison.Ordinal))
        {
            ShowError("Mật khẩu xác nhận không khớp.");
            return;
        }

        if (decoy is not null && string.Equals(pin, decoy, StringComparison.Ordinal))
        {
            ShowError("Decoy PIN không được trùng với Master PIN.");
            return;
        }

        IsBusy = true;
        ClearError();
        try
        {
            await _vaultService.InitializeNewVaultAsync(pin, decoy, EnableWindowsHello && IsWindowsHelloAvailable);
            if (_vaultService.CurrentProfile is not null)
            {
                IsFirstRun = false;
                _onUnlocked(_vaultService.CurrentProfile);
            }
        }
        catch (Exception ex)
        {
            ShowError($"Không thể khởi tạo Vault: {ex.Message}");
        }
        finally
        {
            pin = string.Empty;
            confirm = string.Empty;
            decoy = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockWithWindowsHelloAsync()
    {
        if (IsBusy || !CanUseWindowsHelloUnlock)
        {
            return;
        }

        IsBusy = true;
        ClearError();
        try
        {
            var result = await _vaultService.UnlockWithWindowsHelloAsync();
            if (result.Success && result.Profile is not null)
            {
                _onUnlocked(result.Profile);
            }
            else
            {
                ShowError(result.ErrorMessage ?? "Xác thực Windows Hello thất bại.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearSensitiveInputs()
    {
        PasswordInput = string.Empty;
        ConfirmPasswordInput = string.Empty;
        DecoyPinInput = string.Empty;
    }

    public void ReportError(string message) => ShowError(message);

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }

    private void ClearError()
    {
        ErrorMessage = string.Empty;
        HasError = false;
    }
}
