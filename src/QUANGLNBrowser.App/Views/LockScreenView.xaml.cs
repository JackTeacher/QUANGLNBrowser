using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QUANGLNBrowser.App.ViewModels;

namespace QUANGLNBrowser.App.Views;

public partial class LockScreenView : UserControl
{
    public LockScreenView()
    {
        InitializeComponent();
    }

    private void OnLockScreenIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.InvokeAsync(() => PinBox.Focus());
        }
    }

    private void OnPinBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitPasswords();
            e.Handled = true;
        }
    }

    private void OnSubmitClicked(object sender, RoutedEventArgs e)
    {
        SubmitPasswords();
    }

    private void SubmitPasswords()
    {
        if (DataContext is not LockScreenViewModel vm)
        {
            return;
        }

        vm.PasswordInput = PinBox.Password;
        vm.ConfirmPasswordInput = ConfirmPinBox.Password;
        vm.DecoyPinInput = DecoyPinBox.Password;

        PinBox.Password = string.Empty;
        ConfirmPinBox.Password = string.Empty;
        DecoyPinBox.Password = string.Empty;

        if (vm.UnlockCommand.CanExecute(null))
        {
            vm.UnlockCommand.Execute(null);
        }
    }
}
