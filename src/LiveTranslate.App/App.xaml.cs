using LiveTranslate.App.Services;
using LiveTranslate.App.ViewModels;
using LiveTranslate.Core.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace LiveTranslate.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton<UserSettingsRepository>();
        services.AddSingleton<ApiKeyStore>();
        services.AddSingleton<SubtitleSessionService>();
        services.AddSingleton<SubtitleViewModel>();
        services.AddSingleton<SettingsViewModel>();
        Services = services.BuildServiceProvider();

        var window = new MainWindow();
        MainAppWindow = window;
        window.Activate();
    }
}
