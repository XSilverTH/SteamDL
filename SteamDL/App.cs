using Gio;
using SteamDL.Services;
using SteamDL.Tray;
using SteamDL.Views;
using Application = Adw.Application;
using Task = System.Threading.Tasks.Task;

namespace SteamDL;

[GObject.Subclass<Application>]
public partial class App
{
    private ConnectionCoordinator _connection = null!;
    private readonly RuntimePaths _paths = RuntimePaths.CreateForCurrentUser();
    private SettingsStore _settingsStore = null!;
    private MainWindow? _mainWindow;
    private bool _shuttingDown;
    private StatusNotifierTrayManager? _tray;

    partial void Initialize()
    {
        _settingsStore = new SettingsStore(_paths);
        _connection = new ConnectionCoordinator(_paths, _settingsStore, new MitmdumpResolver(_paths));
        ApplicationId = "xsth.steamdl";
        Flags = ApplicationFlags.FlagsNone;
        OnActivate += Activate;
        OnShutdown += Shutdown;

        var settingsAction = SimpleAction.New("settings", null);
        settingsAction.OnActivate += (_, _) => _mainWindow?.ShowSettingsDialog();
        AddAction(settingsAction);

        var quitAction = SimpleAction.New("quit", null);
        quitAction.OnActivate += (_, _) => QuitApplication();
        AddAction(quitAction);
    }

    private void Activate(Gio.Application sender, EventArgs args)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Widget.Present();
            return;
        }

        _mainWindow = new MainWindow(_connection, _settingsStore);
        var mainWindow = _mainWindow.Widget;
        mainWindow.Application = this;
        AddWindow(mainWindow);
        mainWindow.Present();

        _tray = new StatusNotifierTrayManager(mainWindow.Present);
        _ = InitializeTrayAsync(_tray);

        if (!_settingsStore.Load().SetupCompleted) _mainWindow.ShowSetupWizard();
    }

    private async void QuitApplication()
    {
        try
        {
            await ShutdownAsync();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
        }
        finally
        {
            Quit();
        }
    }

    private void Shutdown(Gio.Application sender, EventArgs args)
    {
        ShutdownAsync().GetAwaiter().GetResult();
    }

    private static async Task InitializeTrayAsync(StatusNotifierTrayManager tray)
    {
        try
        {
            await tray.InitializeAsync();
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"SteamDL system tray is unavailable: {exception.Message}");
        }
    }

    private async Task ShutdownAsync()
    {
        if (_shuttingDown) return;

        _shuttingDown = true;
        if (_tray is not null)
        {
            await _tray.DisposeAsync();
            _tray = null;
        }

        try
        {
            await _connection.DisposeAsync();
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"SteamDL could not complete its shutdown cleanup: {exception.Message}");
        }
    }
}