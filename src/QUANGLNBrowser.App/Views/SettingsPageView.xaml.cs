using System.Windows;
using System.Windows.Controls;
using QUANGLNBrowser.App.ViewModels;

namespace QUANGLNBrowser.App.Views;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
    }

    private async void OnChangePinClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        string currentPin = CurrentPinBox.Password;
        string newPin = NewPinBox.Password;
        string confirmPin = ConfirmNewPinBox.Password;

        CurrentPinBox.Password = string.Empty;
        NewPinBox.Password = string.Empty;
        ConfirmNewPinBox.Password = string.Empty;

        try
        {
            await vm.ChangeVaultPinAsync(currentPin, newPin, confirmPin);
        }
        finally
        {
            currentPin = string.Empty;
            newPin = string.Empty;
            confirmPin = string.Empty;
        }
    }

    private async void OnSaveDecoyPinClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        string decoyPin = NewDecoyPinBox.Password;
        NewDecoyPinBox.Password = string.Empty;

        try
        {
            await vm.SaveDecoyPinAsync(decoyPin);
        }
        finally
        {
            decoyPin = string.Empty;
        }
    }
}
