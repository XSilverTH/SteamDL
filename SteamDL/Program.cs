using Adw;
using SteamDL;
using SteamDL.Services;
using XSTH.Blueprint.Helpers;

if (PrivilegedRelayProgram.IsInvocation(args)) return await PrivilegedRelayProgram.RunAsync(args);

if (args.Length > 0) return await CommandLine.RunAsync(args);

Module.Initialize();
GResourceHelper.RegisterAssemblyResources(typeof(Program).Assembly);

var app = new App();
return app.RunWithSynchronizationContext(args);