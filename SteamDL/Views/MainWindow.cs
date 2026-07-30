using Adw;
using Gtk;
using SteamDL.Services;
using XSTH.Blueprint.Helpers;
using ApplicationWindow = Adw.ApplicationWindow;
using Functions = GLib.Functions;
using MessageDialog = Adw.MessageDialog;
using Window = Gtk.Window;

namespace SteamDL.Views;

public partial class MainWindow : WindowBase<ApplicationWindow>
{
    private readonly Button _connectButton;
    private readonly ConnectionCoordinator _connection;
    private readonly SettingsStore _settingsStore;
    private readonly Label _trafficLabel;

    internal MainWindow(ConnectionCoordinator connection, SettingsStore settingsStore)
    {
        _connection = connection;
        _settingsStore = settingsStore;
        _connectButton = GetRequiredObject<Button>("connect_button");
        _trafficLabel = GetRequiredObject<Label>("traffic_label");
        _connection.SnapshotChanged += OnSnapshotChanged;
        Widget.OnCloseRequest += OnCloseRequest;
        ApplySnapshot(_connection.Snapshot);
    }

    public void ShowSettingsDialog()
    {
        PresentPreferences(false);
    }

    public void ShowSetupWizard()
    {
        PresentPreferences(true);
    }

    private async void OnConnectButton_Clicked(object? sender, EventArgs eventArgs)
    {
        try
        {
            if (_connection.Snapshot.State == ConnectionState.Connected)
                await _connection.DisconnectAsync();
            else
                await _connection.ConnectAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private bool OnCloseRequest(Window sender, EventArgs eventArgs)
    {
        Widget.Hide();
        return true;
    }

    private void PresentPreferences(bool firstRun)
    {
        var settings = _settingsStore.Load();
        var dialog = PreferencesDialog.New();
        dialog.Title = firstRun ? "Setup" : "Settings";

        var page = PreferencesPage.New();
        var group = PreferencesGroup.New();
        if (firstRun) group.Title = "Get a token at steamdl.ir";

        var token = EntryRow.New();
        token.Title = "Token";
        token.Text_ = settings.Token;
        token.OnChanged += async (_, _) =>
        {
            var latest = _settingsStore.Load();
            await _settingsStore.SaveAsync(latest with
            {
                Token = token.Text_.Trim(),
                SetupCompleted = latest.SetupCompleted || !string.IsNullOrWhiteSpace(token.Text_)
            });
        };
        group.Add(token);

        if (!firstRun)
        {
            var mitmdumpPath = EntryRow.New();
            mitmdumpPath.Title = "mitmdump path";
            mitmdumpPath.Text_ = settings.MitmdumpPath ?? string.Empty;
            mitmdumpPath.OnChanged += async (_, _) =>
            {
                var latest = _settingsStore.Load();
                await _settingsStore.SaveAsync(latest with
                {
                    MitmdumpPath = string.IsNullOrWhiteSpace(mitmdumpPath.Text_) ? null : mitmdumpPath.Text_.Trim()
                });
            };
            group.Add(mitmdumpPath);
        }

        page.Add(group);
        dialog.Add(page);
        dialog.Present(Widget);
    }

    private void ShowError(string message)
    {
        var dialog = MessageDialog.New(Widget, "SteamDL", message);
        dialog.AddResponse("close", "Close");
        dialog.Present();
    }

    private void OnSnapshotChanged(ConnectionSnapshot snapshot)
    {
        Functions.IdleAdd(0, () =>
        {
            ApplySnapshot(snapshot);
            return false;
        });
    }

    private void ApplySnapshot(ConnectionSnapshot snapshot)
    {
        var connecting = snapshot.State is ConnectionState.Connecting or ConnectionState.Disconnecting;
        _connectButton.Sensitive = !connecting;
        _connectButton.Label = snapshot.State switch
        {
            ConnectionState.Connected => "Disconnect",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Disconnecting => "Disconnecting…",
            _ => "Connect"
        };
        _trafficLabel.Label_ = snapshot.State == ConnectionState.Connected
            ? $"{DataSizeFormatter.Format(snapshot.DownloadedBytes)} used · {DataSizeFormatter.Format(snapshot.PipelineBytes)} through SteamDL"
            : $"{DataSizeFormatter.Format(snapshot.DownloadedBytes)} used";
    }
}