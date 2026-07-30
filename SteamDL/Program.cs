using Adw;
using SteamDL;
using SteamDL.Services;
using XSTH.Blueprint.Helpers;

if (PrivilegedRelayProgram.IsInvocation(args)) return await PrivilegedRelayProgram.RunAsync(args);

if (args.Length > 0) return await CommandLine.RunAsync(args);

Module.Initialize();
GResourceHelper.RegisterAssemblyResources(typeof(Program).Assembly);

var display = Gdk.Display.GetDefault();
if (display is not null) Gtk.IconTheme.GetForDisplay(display).AddResourcePath("/SteamDL/icons");

Gtk.Window.SetDefaultIconName("xsth.steamdl");

var app = App.NewWithProperties([]);
return app.RunWithSynchronizationContext(args);