using System;
using System.IO;

namespace Padsign.Manager;

internal sealed class AppPaths
{
    public string RootDirectory { get; }
    public string ConfigPath { get; }
    public string LegacyInstallConfigPath { get; }
    public string ListenerConfigPath { get; }
    public string ListenerExePath { get; }
    public string InstallPrinterScriptPath { get; }
    public string RemovePrinterScriptPath { get; }
    public string LogPath { get; }
    public string SpoolPath { get; }

    private AppPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        var userConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Padsign");
        ConfigPath = Path.Combine(userConfigDir, "padsign.json");
        LegacyInstallConfigPath = Path.Combine(rootDirectory, "config", "padsign.json");
        var installedListenerExe = Path.Combine(rootDirectory, "listener", "Padsign.Listener.exe");
        var devListenerExe = Path.Combine(rootDirectory, "out", "Padsign.Listener.exe");
        var isInstalledLayout = File.Exists(installedListenerExe);

        ListenerExePath = isInstalledLayout ? installedListenerExe : devListenerExe;
        ListenerConfigPath = isInstalledLayout
            ? Path.Combine(rootDirectory, "listener", "padsign.json")
            : Path.Combine(rootDirectory, "out", "padsign.json");

        InstallPrinterScriptPath = Path.Combine(rootDirectory, "scripts", "install-printer.ps1");
        RemovePrinterScriptPath = Path.Combine(rootDirectory, "scripts", "remove-printer.ps1");
        LogPath = isInstalledLayout
            ? Path.Combine(rootDirectory, "listener", "logs", "padsign.log")
            : Path.Combine(rootDirectory, "out", "logs", "padsign.log");
        SpoolPath = isInstalledLayout
            ? Path.Combine(rootDirectory, "listener", "spool")
            : Path.Combine(rootDirectory, "out", "spool");
    }

    public static AppPaths Discover()
    {
        var baseDir = AppContext.BaseDirectory;
        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            var script = Path.Combine(current.FullName, "scripts", "install-printer.ps1");
            if (File.Exists(script))
                return new AppPaths(current.FullName);
            current = current.Parent;
        }

        return new AppPaths(Directory.GetCurrentDirectory());
    }
}
