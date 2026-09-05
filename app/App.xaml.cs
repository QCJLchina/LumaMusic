using Microsoft.UI.Xaml;
namespace LumaMusic;
public partial class App : Application
{
    public static MainWindow? Main { get; private set; }
    public App(){InitializeComponent();UnhandledException+=(_,e)=>{Services.AppPaths.Log(e.Exception.ToString());};}
    protected override void OnLaunched(LaunchActivatedEventArgs args){Main=new MainWindow();Main.Activate();}
}
