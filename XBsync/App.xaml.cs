using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using XBsync.Models;
using XBsync.Services;
using XBsync.ViewModels;

namespace XBsync;

public partial class App : Application
{
    private readonly ServiceProvider _services;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient(new HttpClientHandler
        { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 });
        services.AddKeyedSingleton<HttpClient>("CloudStorage", (_, _) => new HttpClient(new HttpClientHandler
        { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 });
        services.AddSingleton<XboxAuthenticationOptions>();
        services.AddSingleton<DesktopUiService>();
        services.AddSingleton<IDesktopUiService>(provider => provider.GetRequiredService<DesktopUiService>());
        services.AddSingleton<XboxAuthenticationService>();
        services.AddSingleton<IXboxAuthenticationService>(provider => provider.GetRequiredService<XboxAuthenticationService>());
        services.AddSingleton<IXboxApiService, XboxApiService>();
        services.AddSingleton<ICloudSaveService>(provider => new CloudSaveService(
            provider.GetRequiredKeyedService<HttpClient>("CloudStorage"),
            provider.GetRequiredService<XboxAuthenticationService>()));
        services.AddSingleton<MainViewModel>();
        _services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UnhandledException += (_, e) => LogCrash("Xaml", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        _window = new MainWindow(_services.GetRequiredService<MainViewModel>(), _services.GetRequiredService<DesktopUiService>());
        _window.Closed += (_, _) =>
        {
            _services.GetRequiredService<MainViewModel>().Stop();
            _services.Dispose();
        };
        _window.Activate();
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XBsync");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"[{DateTimeOffset.Now:O}] {source}: {exception}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
