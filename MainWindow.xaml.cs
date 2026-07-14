using System.Windows;
using UnrealManager.Services;

namespace UnrealManager;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void OnReportBug(object sender, RoutedEventArgs e) => BugReportService.Open();
}
