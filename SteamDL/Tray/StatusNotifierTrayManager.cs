using System.Text;
using GLib;
using Tmds.DBus.Protocol;

namespace SteamDL.Tray;

internal sealed class StatusNotifierTrayManager(Action activateWindow) : IAsyncDisposable
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    private DBusConnection? _connection;
    private StatusNotifierItemHandler? _item;
    private string? _serviceName;

    public async ValueTask DisposeAsync()
    {
        if (_connection is null) return;

        if (_item is not null) _connection.RemoveMethodHandler(_item.Path);

        if (_serviceName is not null) await _connection.ReleaseNameAsync(_serviceName);

        _connection.Dispose();
        _connection = null;
        _item = null;
        _serviceName = null;
    }

    public async Task InitializeAsync()
    {
        _connection = new DBusConnection(
            Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")
            ?? throw new InvalidOperationException("No session D-Bus is available."));
        await _connection.ConnectAsync();

        _item = new StatusNotifierItemHandler(ActivateWindow);
        _connection.AddMethodHandler(_item);
        _serviceName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
        await _connection.RequestNameAsync(_serviceName, RequestNameOptions.None);

        try
        {
            MessageBuffer message;
            using (var writer = _connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(
                    WatcherService,
                    WatcherPath,
                    WatcherInterface,
                    "RegisterStatusNotifierItem",
                    "s");
                writer.WriteString(_serviceName);
                message = writer.CreateMessage();
            }

            await _connection.CallMethodAsync(message);
        }
        catch (DBusExceptionBase)
        {
            // A session without a StatusNotifier watcher still keeps the application running safely.
        }
    }

    private void ActivateWindow()
    {
        Functions.IdleAdd(0, () =>
        {
            activateWindow();
            return false;
        });
    }

    private sealed class StatusNotifierItemHandler(Action activateWindow) : IPathMethodHandler
    {
        private const string ItemInterface = "org.kde.StatusNotifierItem";
        private const string PeerInterface = "org.freedesktop.DBus.Peer";

        private const string IntrospectionXml = """
                                                <interface name="org.kde.StatusNotifierItem">
                                                  <method name="Activate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
                                                  <method name="SecondaryActivate"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
                                                  <method name="ContextMenu"><arg name="x" type="i" direction="in"/><arg name="y" type="i" direction="in"/></method>
                                                  <method name="Scroll"><arg name="delta" type="i" direction="in"/><arg name="orientation" type="s" direction="in"/></method>
                                                  <property name="Category" type="s" access="read"/>
                                                  <property name="Id" type="s" access="read"/>
                                                  <property name="Title" type="s" access="read"/>
                                                  <property name="Status" type="s" access="read"/>
                                                  <property name="IconName" type="s" access="read"/>
                                                  <property name="OverlayIconName" type="s" access="read"/>
                                                  <property name="AttentionIconName" type="s" access="read"/>
                                                  <property name="IsMenu" type="b" access="read"/>
                                                  <property name="Menu" type="o" access="read"/>
                                                </interface>
                                                <interface name="org.freedesktop.DBus.Properties">
                                                  <method name="Get">
                                                    <arg name="interface_name" type="s" direction="in"/>
                                                    <arg name="property_name" type="s" direction="in"/>
                                                    <arg name="value" type="v" direction="out"/>
                                                  </method>
                                                  <method name="GetAll">
                                                    <arg name="interface_name" type="s" direction="in"/>
                                                    <arg name="properties" type="a{sv}" direction="out"/>
                                                  </method>
                                                  <method name="Set">
                                                    <arg name="interface_name" type="s" direction="in"/>
                                                    <arg name="property_name" type="s" direction="in"/>
                                                    <arg name="value" type="v" direction="in"/>
                                                  </method>
                                                  <signal name="PropertiesChanged">
                                                    <arg name="interface_name" type="s"/>
                                                    <arg name="changed_properties" type="a{sv}"/>
                                                    <arg name="invalidated_properties" type="as"/>
                                                  </signal>
                                                </interface>
                                                """;

        private static readonly Dictionary<string, VariantValue> Properties =
            new()
            {
                ["Category"] = "ApplicationStatus",
                ["Id"] = "steamdl",
                ["Title"] = "SteamDL",
                ["Status"] = "Active",
                ["IconName"] = "network-vpn-symbolic",
                ["OverlayIconName"] = string.Empty,
                ["AttentionIconName"] = string.Empty,
                ["IsMenu"] = false,
                ["Menu"] = new ObjectPath("/")
            };

        public string Path => "/StatusNotifierItem";

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            try
            {
                if (context.IsDBusIntrospectRequest)
                {
                    context.ReplyIntrospectXml([Encoding.UTF8.GetBytes(IntrospectionXml)], []);
                    return ValueTask.CompletedTask;
                }

                var request = context.Request;
                var interfaceName = request.InterfaceAsString;
                var member = request.MemberAsString;
                if (context.IsPropertiesInterfaceRequest)
                {
                    HandleProperties(context, member ?? string.Empty);
                    return ValueTask.CompletedTask;
                }

                if (string.Equals(interfaceName, PeerInterface, StringComparison.Ordinal)
                    && string.Equals(member, "Ping", StringComparison.Ordinal))
                {
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;
                }

                if (string.Equals(interfaceName, ItemInterface, StringComparison.Ordinal)
                    && member is "Activate" or "SecondaryActivate" or "ContextMenu")
                {
                    activateWindow();
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;
                }

                if (string.Equals(interfaceName, ItemInterface, StringComparison.Ordinal)
                    && string.Equals(member, "Scroll", StringComparison.Ordinal))
                {
                    ReplyEmpty(context);
                    return ValueTask.CompletedTask;
                }

                context.ReplyUnknownMethodError();
            }
            catch (Exception exception)
            {
                context.HandleException(exception);
            }

            return ValueTask.CompletedTask;
        }

        private static void HandleProperties(MethodContext context, string member)
        {
            var reader = context.Request.GetBodyReader();
            switch (member)
            {
                case "Get":
                    _ = reader.ReadString();
                    ReplyProperty(context, reader.ReadString());
                    break;
                case "GetAll":
                    _ = reader.ReadString();
                    ReplyAllProperties(context);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private static void ReplyProperty(MethodContext context, string propertyName)
        {
            if (!Properties.TryGetValue(propertyName, out var value))
            {
                context.ReplyError(
                    "org.freedesktop.DBus.Error.InvalidArgs",
                    $"Unknown StatusNotifierItem property '{propertyName}'.");
                return;
            }

            using var writer = context.CreateReplyWriter("v");
            writer.WriteVariant(value);
            context.Reply(writer.CreateMessage());
        }

        private static void ReplyAllProperties(MethodContext context)
        {
            using var writer = context.CreateReplyWriter("a{sv}");
            writer.WriteDictionary(Properties);
            context.Reply(writer.CreateMessage());
        }

        private static void ReplyEmpty(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }
}