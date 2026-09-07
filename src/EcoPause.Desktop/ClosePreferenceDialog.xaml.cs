using System.Windows;

namespace EcoPause.Desktop;

public enum CloseDialogChoice
{
    Cancel,
    MinimizeToTray,
    ExitApplication
}

public partial class ClosePreferenceDialog : Window
{
    public ClosePreferenceDialog()
    {
        InitializeComponent();
    }

    public CloseDialogChoice Choice { get; private set; }

    public bool RememberChoice => RememberChoiceCheckBox.IsChecked == true;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseDialogChoice.MinimizeToTray;
        DialogResult = true;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Choice = CloseDialogChoice.ExitApplication;
        DialogResult = true;
    }
}
