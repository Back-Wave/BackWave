using System.Runtime.CompilerServices;

namespace BackWave.Hosting.Tests;

internal static class NoConfigReload
{
    // WebApplication.CreateBuilder watches appsettings.json for changes. On macOS, starting that
    // file watcher sometimes blocks forever, and the whole run hangs. No test here reloads config.
    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}
