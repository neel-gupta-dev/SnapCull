using System.Windows;

namespace SnapCull;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Photo apps are dark so the UI doesn't distract from the pictures.
        ThemeMode = ThemeMode.Dark;
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "SnapCull – something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Lets people run "SnapCull.exe D:\Photos\Shoot" or open the app via "Open with".
        new MainWindow(e.Args.FirstOrDefault()).Show();
    }
}
