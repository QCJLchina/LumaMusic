using Microsoft.UI.Xaml;
namespace LumaMusic;
public partial class App : Application
{
    public static MainWindow? Main { get; private set; }
    public App(){InitializeComponent();UnhandledException+=(_,e)=>{Services.AppPaths.Log(e.Exception.ToString());};}
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.AppPaths.Log("OnLaunched");
        Main=new MainWindow();
        Services.AppPaths.Log("MainWindow constructed");
        Main.Activate();
        Services.AppPaths.Log("Activated");
    }
}
