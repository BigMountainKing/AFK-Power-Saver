using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AFK Power Saver Setup")]
[assembly: AssemblyProduct("AFK Power Saver")]
[assembly: AssemblyCompany("AFK Power Saver")]
[assembly: AssemblyVersion("1.25.0.0")]
[assembly: AssemblyFileVersion("1.25.0.0")]

namespace AFKPowerSaver.Setup
{
    internal static class Program
    {
        private const string ProductName = "AFK Power Saver";
        private const string ProductVersion = "1.25.0";
        private const string PayloadResourceName = "AFKPowerSaver.Payload.zip";
        private const string UninstallerFileName = "Uninstall AFK Power Saver.exe";
        private const string UninstallRegistryParent = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        private const string UninstallRegistryName = "AFKPowerSaver";
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupRegistryValueName = "AFKPowerSaver";
        private const string TestInstallMarkerName = ".afkpowersaver-test-install";
        private const string TestInstallMarkerValue = "AFK Power Saver setup test marker 1";
        private const long MaximumExpandedBytes = 32L * 1024L * 1024L;

        private static readonly string[] RequiredExecutables =
        {
            "AFKPowerSaver.exe",
            "AFKPowerSaver.CpuRecovery.exe",
            "AFKPowerSaver.Probe.exe",
            "AFKPowerSaver.LiveSession.exe",
            "AFKPowerSaver.ElevatedHost.exe"
        };

        [STAThread]
        private static int Main(string[] args)
        {
            var testInstall = args.Length == 2 &&
                string.Equals(args[0], "--test-install", StringComparison.Ordinal);
            var testUninstall = args.Length == 2 &&
                string.Equals(args[0], "--test-uninstall", StringComparison.Ordinal);
            var quietUninstall = args.Length > 0 &&
                (string.Equals(args[0], "--uninstall-quiet", StringComparison.Ordinal) ||
                 (string.Equals(args[0], "--uninstall-temp", StringComparison.Ordinal) &&
                  args.Length == 4 &&
                  string.Equals(args[3], "true", StringComparison.OrdinalIgnoreCase)));
            try
            {
                CleanupStaleTemporaryUninstallers();

                if (testUninstall)
                {
                    UninstallTestInstallation(Path.GetFullPath(args[1]));
                    return 0;
                }

                if (args.Length == 1 &&
                    (string.Equals(args[0], "--uninstall", StringComparison.Ordinal) ||
                     string.Equals(args[0], "--uninstall-quiet", StringComparison.Ordinal)))
                {
                    if (!quietUninstall)
                    {
                        var confirmation = MessageBox.Show(
                            "Remove AFK Power Saver from this PC?\n\nSaved preferences will be retained.",
                            "Uninstall " + ProductName,
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button2);
                        if (confirmation != DialogResult.Yes)
                        {
                            return 0;
                        }
                    }

                    StartTemporaryUninstaller(quietUninstall);
                    return 0;
                }

                if (args.Length == 4 && string.Equals(args[0], "--uninstall-temp", StringComparison.Ordinal))
                {
                    int parentProcessId;
                    bool quiet;
                    if (!int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentProcessId) ||
                        !bool.TryParse(args[3], out quiet))
                    {
                        throw new InvalidOperationException("The uninstall request is invalid.");
                    }

                    UninstallProductionInstallation(args[1], parentProcessId, quiet);
                    return 0;
                }

                if (args.Length != 0 && !testInstall)
                {
                    throw new InvalidOperationException("The setup command is invalid.");
                }

                var installDirectory = testInstall
                    ? Path.GetFullPath(args[1])
                    : GetDefaultInstallDirectory();
                EnsureNoRuntimeProcessIsActive(testInstall);
                InstallPayload(installDirectory);
                InstallUninstaller(installDirectory);

                if (testInstall)
                {
                    File.WriteAllText(
                        Path.Combine(installDirectory, TestInstallMarkerName),
                        TestInstallMarkerValue);
                    return 0;
                }

                CreateStartMenuShortcut(installDirectory);
                RegisterUninstallEntry(installDirectory);
                if (!IsDotNet10DesktopRuntimeInstalled())
                {
                    MessageBox.Show(
                        ".NET 10 Desktop Runtime (x64) is required. Install it, then open AFK Power Saver from the Start menu.\n\nThe .NET 10 SDK also includes the required runtime.",
                        ProductName + " installed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return 0;
                }

                var applicationPath = Path.Combine(installDirectory, "AFKPowerSaver.exe");
                Process.Start(new ProcessStartInfo(applicationPath)
                {
                    WorkingDirectory = installDirectory,
                    UseShellExecute = true
                });
                return 0;
            }
            catch (Exception exception)
            {
                if (!testInstall && !testUninstall && !quietUninstall)
                {
                    MessageBox.Show(
                        "AFK Power Saver setup could not complete.\n\n" + exception.Message,
                        ProductName,
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                return 1;
            }
        }

        private static string GetCurrentExecutablePath()
        {
            var location = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrWhiteSpace(location))
            {
                throw new InvalidOperationException("The setup executable path is unavailable.");
            }
            return Path.GetFullPath(location);
        }

        private static string GetDefaultInstallDirectory()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
            {
                throw new InvalidOperationException("The current user's local application-data directory is unavailable.");
            }
            return Path.GetFullPath(Path.Combine(local, "Programs", "AFKPowerSaver"));
        }

        private static void EnsureNoRuntimeProcessIsActive(bool testMode)
        {
            if (testMode)
            {
                return;
            }

            foreach (var executable in RequiredExecutables)
            {
                var processName = Path.GetFileNameWithoutExtension(executable);
                if (Process.GetProcessesByName(processName).Length != 0)
                {
                    throw new InvalidOperationException(
                        "Close AFK Power Saver and wait for its helper processes to exit before installing or uninstalling.");
                }
            }
        }

        private static void InstallUninstaller(string installDirectory)
        {
            var source = GetCurrentExecutablePath();
            var destination = Path.Combine(installDirectory, UninstallerFileName);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            File.Copy(source, destination, true);
        }

        private static void RegisterUninstallEntry(string installDirectory)
        {
            var uninstallerPath = Path.Combine(installDirectory, UninstallerFileName);
            var applicationPath = Path.Combine(installDirectory, "AFKPowerSaver.exe");
            long installedBytes = 0;
            foreach (var file in Directory.GetFiles(installDirectory, "*", SearchOption.AllDirectories))
            {
                installedBytes += new FileInfo(file).Length;
            }
            var estimatedKilobytes = (int)Math.Min(int.MaxValue, Math.Max(1L, (installedBytes + 1023L) / 1024L));

            using (var parent = Registry.CurrentUser.CreateSubKey(UninstallRegistryParent, true))
            using (var key = parent == null ? null : parent.CreateSubKey(UninstallRegistryName, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows could not create the Installed apps registration.");
                }

                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
                key.SetValue("Publisher", ProductName, RegistryValueKind.String);
                key.SetValue("InstallLocation", installDirectory, RegistryValueKind.String);
                key.SetValue("DisplayIcon", applicationPath + ",0", RegistryValueKind.String);
                key.SetValue("UninstallString", QuoteArgument(uninstallerPath) + " --uninstall", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", QuoteArgument(uninstallerPath) + " --uninstall-quiet", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", estimatedKilobytes, RegistryValueKind.DWord);
            }
        }

        private static void StartTemporaryUninstaller(bool quiet)
        {
            var temporaryPath = Path.Combine(
                Path.GetTempPath(),
                "AFKPowerSaver-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(GetCurrentExecutablePath(), temporaryPath, false);

            var installDirectory = GetDefaultInstallDirectory();
            var arguments = "--uninstall-temp " + QuoteArgument(installDirectory) + " " +
                Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " " +
                (quiet ? "true" : "false");
            Process.Start(new ProcessStartInfo(temporaryPath)
            {
                Arguments = arguments,
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        private static void UninstallProductionInstallation(string requestedDirectory, int parentProcessId, bool quiet)
        {
            var installDirectory = Path.GetFullPath(requestedDirectory);
            if (!string.Equals(installDirectory, GetDefaultInstallDirectory(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The uninstall target does not match the AFK Power Saver install directory.");
            }

            WaitForProcessExit(parentProcessId);
            EnsureNoRuntimeProcessIsActive(false);
            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, true);
            }
            RemoveStartupRegistration();
            RemoveStartMenuShortcut();
            RemoveUninstallEntry();

            if (!quiet)
            {
                MessageBox.Show(
                    "AFK Power Saver was removed.\n\nSaved preferences were retained in Local AppData.",
                    ProductName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static void UninstallTestInstallation(string installDirectory)
        {
            var markerPath = Path.Combine(installDirectory, TestInstallMarkerName);
            if (!File.Exists(markerPath) ||
                !string.Equals(File.ReadAllText(markerPath), TestInstallMarkerValue, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test uninstall target is not a setup-created test installation.");
            }
            Directory.Delete(installDirectory, true);
        }

        private static void WaitForProcessExit(int processId)
        {
            if (processId <= 0 || processId == Process.GetCurrentProcess().Id)
            {
                throw new InvalidOperationException("The uninstall parent process is invalid.");
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.WaitForExit(30000))
                    {
                        throw new InvalidOperationException("The installed uninstaller did not exit in time.");
                    }
                }
            }
            catch (ArgumentException)
            {
                // The installed uninstaller already exited.
            }
        }

        private static void RemoveStartupRegistration()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true))
            {
                if (key != null)
                {
                    key.DeleteValue(StartupRegistryValueName, false);
                }
            }
        }

        private static void RemoveStartMenuShortcut()
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!string.IsNullOrWhiteSpace(programs))
            {
                var shortcutPath = Path.Combine(programs, ProductName + ".lnk");
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
            }
        }

        private static void RemoveUninstallEntry()
        {
            using (var parent = Registry.CurrentUser.OpenSubKey(UninstallRegistryParent, true))
            {
                if (parent != null)
                {
                    parent.DeleteSubKeyTree(UninstallRegistryName, false);
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value.IndexOf('"') >= 0)
            {
                throw new InvalidOperationException("A setup path contains an invalid quote character.");
            }
            return "\"" + value + "\"";
        }

        private static void CleanupStaleTemporaryUninstallers()
        {
            var current = GetCurrentExecutablePath();
            foreach (var path in Directory.GetFiles(Path.GetTempPath(), "AFKPowerSaver-Uninstall-*.exe"))
            {
                if (string.Equals(Path.GetFullPath(path), current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static void InstallPayload(string installDirectory)
        {
            var parent = Directory.GetParent(installDirectory);
            if (parent == null)
            {
                throw new InvalidOperationException("The installation directory is invalid.");
            }
            Directory.CreateDirectory(parent.FullName);

            var stagingDirectory = installDirectory + ".installing-" + Guid.NewGuid().ToString("N");
            var backupDirectory = installDirectory + ".previous-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                ExtractPayload(stagingDirectory);
                foreach (var executable in RequiredExecutables)
                {
                    if (!File.Exists(Path.Combine(stagingDirectory, executable)))
                    {
                        throw new InvalidDataException("The setup payload is incomplete: " + executable);
                    }
                }

                var hadExistingInstall = Directory.Exists(installDirectory);
                if (hadExistingInstall)
                {
                    Directory.Move(installDirectory, backupDirectory);
                }

                try
                {
                    Directory.Move(stagingDirectory, installDirectory);
                }
                catch
                {
                    if (hadExistingInstall && !Directory.Exists(installDirectory) && Directory.Exists(backupDirectory))
                    {
                        Directory.Move(backupDirectory, installDirectory);
                    }
                    throw;
                }

                if (Directory.Exists(backupDirectory))
                {
                    Directory.Delete(backupDirectory, true);
                }
            }
            finally
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, true);
                }
            }
        }

        private static void ExtractPayload(string stagingDirectory)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var payload = assembly.GetManifestResourceStream(PayloadResourceName))
            {
                if (payload == null)
                {
                    throw new InvalidDataException("The embedded AFK Power Saver payload is missing.");
                }

                using (var archive = new ZipArchive(payload, ZipArchiveMode.Read, false))
                {
                    long expandedBytes = 0;
                    var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            continue;
                        }

                        expandedBytes += entry.Length;
                        if (expandedBytes > MaximumExpandedBytes)
                        {
                            throw new InvalidDataException("The setup payload exceeds its allowed size.");
                        }

                        var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("The setup payload contains an unsafe path.");
                        }

                        var destinationParent = Path.GetDirectoryName(destination);
                        if (destinationParent == null)
                        {
                            throw new InvalidDataException("A setup destination is invalid.");
                        }
                        Directory.CreateDirectory(destinationParent);
                        using (var input = entry.Open())
                        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            input.CopyTo(output);
                        }
                    }
                }
            }
        }

        private static void CreateStartMenuShortcut(string installDirectory)
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (string.IsNullOrWhiteSpace(programs))
            {
                throw new InvalidOperationException("The current user's Start menu directory is unavailable.");
            }

            var shortcutPath = Path.Combine(programs, ProductName + ".lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("Windows shortcut support is unavailable.");
            }

            object shell = null;
            object shortcut = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                dynamic dynamicShell = shell;
                shortcut = dynamicShell.CreateShortcut(shortcutPath);
                dynamic dynamicShortcut = shortcut;
                dynamicShortcut.TargetPath = Path.Combine(installDirectory, "AFKPowerSaver.exe");
                dynamicShortcut.WorkingDirectory = installDirectory;
                dynamicShortcut.Description = ProductName;
                dynamicShortcut.Save();
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }

        private static bool IsDotNet10DesktopRuntimeInstalled()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var sharedFramework = Path.Combine(
                programFiles,
                "dotnet",
                "shared",
                "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(sharedFramework))
            {
                return false;
            }

            foreach (var directory in Directory.GetDirectories(sharedFramework))
            {
                Version version;
                if (Version.TryParse(Path.GetFileName(directory), out version) && version.Major >= 10)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
