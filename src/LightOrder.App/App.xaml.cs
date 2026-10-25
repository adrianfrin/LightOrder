namespace LightOrder.App;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        string? captureDirectory = e.Args.Length == 2 && e.Args[0] == "--capture-dir" ? System.IO.Path.GetFullPath(e.Args[1]) : null;
        MainWindow = new MainWindow(captureDirectory);
        MainWindow.Show();
    }
}
