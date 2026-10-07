using System.Windows;

namespace TopicMonitor.Viewer.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        new MainWindow(ViewerOptions.Parse(e.Args)).Show();
    }
}

