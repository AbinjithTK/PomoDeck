namespace Loupedeck.PomoDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using Microsoft.Win32;

    /// <summary>
    /// Finds and launches the PomoDeck desktop app.
    /// 
    /// Detection order:
    /// 1. Check if already running (process name)
    /// 2. Registry: Tauri NSIS installer writes InstallLocation
    /// 3. Known paths: AppData, Program Files
    /// 4. Dev build path (for development)
    /// 
    /// The bridge handles connectivity — this class only handles launching.
    /// </summary>
    public class PomodoroApplication : ClientApplication
    {
        private const String ProcessName = "PomoDeck";
        private const String ExeName = "PomoDeck.exe";

        // Registry keys written by Tauri NSIS installer
        private static readonly String[] RegistryKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PomoDeck",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PomoDeck",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{com.pomodeck.app}",
        };

        private static String _cachedPath;

        public PomodoroApplication() { }

        protected override Boolean IsProcessNameSupported(String processName) =>
            processName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        /// <summary>Is the PomoDeck process running?</summary>
        public static new Boolean IsRunning()
        {
            try { return Process.GetProcessesByName(ProcessName).Length > 0 || Process.GetProcessesByName("pomodeck").Length > 0; }
            catch { return false; }
        }

        /// <summary>Find the PomoDeck executable. Caches result.</summary>
        public static String FindExe()
        {
            if (_cachedPath != null && File.Exists(_cachedPath))
                return _cachedPath;

            // 1. Registry (installed via Tauri NSIS)
            var path = FindInRegistry();
            if (path != null) { _cachedPath = path; return path; }

            // 2. Known paths
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProcessName, ExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProcessName, ExeName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProcessName, ExeName),
                // User desktop shortcut target — common for portable installs
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ExeName),
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        _cachedPath = candidate;
                        return candidate;
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>Launch PomoDeck if not running. Returns true if running after call.</summary>
        public static Boolean Launch()
        {
            if (IsRunning()) return true;

            var exe = FindExe();
            if (exe == null) return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
                PluginLog.Info($"Launched PomoDeck: {exe}");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "Failed to launch PomoDeck");
                return false;
            }
        }

        private static String FindInRegistry()
        {
            foreach (var keyPath in RegistryKeys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(keyPath);
                    if (key == null) continue;

                    var installLocation = key.GetValue("InstallLocation") as String;
                    if (String.IsNullOrEmpty(installLocation)) continue;

                    var exePath = Path.Combine(installLocation, ExeName);
                    if (File.Exists(exePath)) return exePath;
                }
                catch { }

                // Also check current user
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                    if (key == null) continue;

                    var installLocation = key.GetValue("InstallLocation") as String;
                    if (String.IsNullOrEmpty(installLocation)) continue;

                    var exePath = Path.Combine(installLocation, ExeName);
                    if (File.Exists(exePath)) return exePath;
                }
                catch { }
            }

            return null;
        }
    }
}
