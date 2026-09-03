using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string cls, string title);

    private static bool IsTrayReady()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        IntPtr tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        return tray != IntPtr.Zero;
    }

    private const string ElevatedTaskName = "SwitchPowerTray Elevated";

    internal static bool IsRunningElevated()
    {
        try
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool IsElevatedTaskInstalled()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = "/Query /TN \"" + ElevatedTaskName + "\" /XML",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                string xml = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return false;

                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);

                XmlNode commandNode = doc.SelectSingleNode("//*[local-name()='Command']");
                XmlNode argumentsNode = doc.SelectSingleNode("//*[local-name()='Arguments']");

                string command = commandNode != null ? (commandNode.InnerText ?? "").Trim() : "";
                string arguments = argumentsNode != null ? (argumentsNode.InnerText ?? "").Trim() : "";

                string expected = Path.GetFullPath(Application.ExecutablePath).TrimEnd('\\');
                string actual = command.Trim().Trim('\"');

                bool pointsToThisExe = string.Equals(
                    Path.GetFullPath(actual).TrimEnd('\\'),
                    expected,
                    StringComparison.OrdinalIgnoreCase);

                bool launchesElevatedTray =
                    arguments.IndexOf("/elevated-tray", StringComparison.OrdinalIgnoreCase) >= 0;

                // Older builds used a one-time 20-year task. Treat that task
                // as stale so the next launch asks once more and upgrades it
                // to the permanent ONLOGON task.
                bool isLogonTask =
                    xml.IndexOf("<LogonTrigger", StringComparison.OrdinalIgnoreCase) >= 0;

                // The elevated tray task must be explicitly allowed to run on
                // battery power and must not stop when AC power is removed.
                // Older test registrations can inherit Task Scheduler battery
                // conditions that stop the tray exactly when the laptop is
                // unplugged. Require the corrected values before trusting the
                // persisted task.
                XmlNode disallowBatteryNode =
                    doc.SelectSingleNode("//*[local-name()='DisallowStartIfOnBatteries']");
                XmlNode stopOnBatteryNode =
                    doc.SelectSingleNode("//*[local-name()='StopIfGoingOnBatteries']");

                bool disallowStartOnBatteries =
                    disallowBatteryNode != null &&
                    string.Equals((disallowBatteryNode.InnerText ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

                bool stopIfGoingOnBatteries =
                    stopOnBatteryNode != null &&
                    string.Equals((stopOnBatteryNode.InnerText ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase);

                bool batterySafe = !disallowStartOnBatteries && !stopIfGoingOnBatteries;

                return pointsToThisExe && launchesElevatedTray && isLogonTask && batterySafe;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteElevatedTask()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = "/Delete /TN \"" + ElevatedTaskName + "\" /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                p.WaitForExit(5000);
                return p.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool RunElevatedTask()
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = "/Run /TN \"" + ElevatedTaskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) return false;
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool InstallElevatedTask()
    {
        string exePath = Application.ExecutablePath;
        string escapedExe = System.Security.SecurityElement.Escape(exePath);
        string workingDirectory = Application.StartupPath;
        string escapedWorkingDirectory = System.Security.SecurityElement.Escape(workingDirectory);

        try
        {
            // Register the persistent elevated tray task from explicit XML so
            // the task has no AC/battery restrictions. This is important on
            // laptops: the tray must continue running when AC is unplugged and
            // it must also remain launchable while already on battery power.
            string taskXml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>SwitchPowerTray elevated tray task.</Description></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"Author\">" +
                    "<UserId>" + System.Security.SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) + "</UserId>" +
                    "<LogonType>InteractiveToken</LogonType>" +
                    "<RunLevel>HighestAvailable</RunLevel>" +
                "</Principal></Principals>" +
                "<Settings>" +
                    "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
                    "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                    "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<AllowHardTerminate>true</AllowHardTerminate>" +
                    "<StartWhenAvailable>true</StartWhenAvailable>" +
                    "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>" +
                    "<IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>" +
                    "<AllowStartOnDemand>true</AllowStartOnDemand>" +
                    "<Enabled>true</Enabled>" +
                    "<Hidden>true</Hidden>" +
                    "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>" +
                    "<Priority>7</Priority>" +
                "</Settings>" +
                "<Actions Context=\"Author\"><Exec>" +
                    "<Command>" + escapedExe + "</Command>" +
                    "<Arguments>/elevated-tray</Arguments>" +
                    "<WorkingDirectory>" + escapedWorkingDirectory + "</WorkingDirectory>" +
                "</Exec></Actions>" +
                "</Task>";

            string xmlPath = Path.Combine(Path.GetTempPath(), "SwitchPowerTray_ElevatedTask.xml");
            File.WriteAllText(xmlPath, taskXml, new UnicodeEncoding(false, true));

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    Arguments =
                        "/Create" +
                        " /TN \"" + ElevatedTaskName + "\"" +
                        " /XML \"" + xmlPath.Replace("\"", "\\\"") + "\"" +
                        " /F",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;

                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(5000);

                    if (p.ExitCode != 0)
                    {
                        try
                        {
                            File.AppendAllText(
                                Path.Combine(Path.GetTempPath(), "SwitchPowerTray.log"),
                                DateTime.Now.ToString("s") +
                                "  ElevationTaskInstall failed: " +
                                p.ExitCode + " " + stdout + " " + stderr +
                                Environment.NewLine);
                        }
                        catch { }

                        return false;
                    }
                }
            }
            finally
            {
                try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { }
            }

            return IsElevatedTaskInstalled();
        }
        catch
        {
            return false;
        }
    }

    private static void StartElevatedTaskSoon()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            Thread.Sleep(150);
            RunElevatedTask();
        });
    }

    internal static int ApplyEnergySaverPolicyAsElevated(bool enable)
    {
        const string policyPath = @"SOFTWARE\Policies\Microsoft\Power\EnergySaver";
        const string policyValue = "EnableEnergySaver";

        try
        {
            if (enable)
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(policyPath, true))
                {
                    if (key == null) return 10;
                    key.SetValue(policyValue, 1, RegistryValueKind.DWord);
                }
            }
            else
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(policyPath, true))
                {
                    if (key != null)
                    {
                        try { key.DeleteValue(policyValue, false); } catch { }
                    }
                }
            }

            string gpupdate = Path.Combine(Environment.SystemDirectory, "gpupdate.exe");
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = gpupdate,
                Arguments = "/target:computer /force /wait:0",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) return 11;
                // /wait:0 intentionally means we do not block the tray or the
                // special-mode worker while Windows refreshes policy. The
                // registry policy write above is already complete, and the
                // latest requested mode will be reconciled independently.
                return 0;
            }
        }
        catch (UnauthorizedAccessException) { return 5; }
        catch (Exception) { return 1; }
    }

    private static void WaitForExplorerAndTray(int timeoutMs)
    {
        int start = Environment.TickCount;
        while (Process.GetProcessesByName("explorer").Length == 0)
        {
            if (Environment.TickCount - start > timeoutMs) break;
            Thread.Sleep(300);
        }
        while (!IsTrayReady())
        {
            if (Environment.TickCount - start > timeoutMs) break;
            Thread.Sleep(300);
        }
    }

    private static void ManageLogFile()
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "SwitchPowerTray.log");
            if (File.Exists(logPath))
            {
                long length = new FileInfo(logPath).Length;
                if (length > 1024 * 1024) // If larger than 1 MB
                {
                    File.Copy(logPath, logPath + ".old", true);
                    File.Delete(logPath);
                }
            }
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        // This executable also acts as a tiny elevated helper. The normal tray
        // process stays unelevated; only the machine-level Energy Saver policy
        // change is relaunched with the UAC "runas" verb.
        if (args != null && args.Length >= 1 &&
            string.Equals(args[0], "/install-elevated", StringComparison.OrdinalIgnoreCase))
        {
            bool installed = InstallElevatedTask();
            if (installed)
            {
                // We are already elevated because this branch was launched
                // through UAC. Start the tray directly from this elevated
                // process instead of asking Task Scheduler to launch it and
                // then exiting. This guarantees the very first launch shows
                // the tray immediately and avoids a second UAC prompt.
                try
                {
                    ProcessStartInfo trayPsi = new ProcessStartInfo
                    {
                        FileName = Application.ExecutablePath,
                        Arguments = "/elevated-tray",
                        UseShellExecute = false,
                        WorkingDirectory = Application.StartupPath
                    };
                    Process.Start(trayPsi);
                }
                catch { }
            }

            Environment.ExitCode = installed ? 0 : 1;
            return;
        }

        if (args != null && args.Length >= 2 &&
            string.Equals(args[0], "/elevated-energy-policy", StringComparison.OrdinalIgnoreCase))
        {
            int rc = ApplyEnergySaverPolicyAsElevated(
                string.Equals(args[1], "enable", StringComparison.OrdinalIgnoreCase));
            Environment.ExitCode = rc;
            return;
        }

        bool elevated = IsRunningElevated();

        if (args == null || args.Length == 0 ||
            (!string.Equals(args[0], "/elevated-tray", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(args[0], "/elevated-energy-policy", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(args[0], "/install-elevated", StringComparison.OrdinalIgnoreCase)))
        {
            if (!elevated)
            {
                bool taskInstalledForThisExe = IsElevatedTaskInstalled();

                if (taskInstalledForThisExe)
                {
                    // The persisted task must point to THIS executable.
                    // Older test builds may have left a task pointing at a
                    // previous EXE, which otherwise makes the launcher exit
                    // without ever showing the tray.
                    if (RunElevatedTask())
                        return;

                    DeleteElevatedTask();
                }

                DialogResult answer = MessageBox.Show(
                        "Switch Power Plan Tray can be configured to run with administrator privileges automatically on future launches.\r\n\r\n" +
                        "This requires one administrator approval now. After approval, Windows Task Scheduler will start the app elevated without asking every time.\r\n\r\n" +
                        "Install this one-time elevated startup?",
                        "Switch Power Plan Tray",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Information);

                if (answer == DialogResult.Yes)
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Arguments = "/install-elevated",
                            UseShellExecute = true,
                            Verb = "runas",
                            WorkingDirectory = Application.StartupPath
                        };

                        using (Process p = Process.Start(psi))
                        {
                            if (p != null)
                                p.WaitForExit(20000);
                        }
                    }
                    catch
                    {
                        // UAC canceled or elevation failed. Continue below
                        // in normal mode so the tray remains usable.
                    }

                    if (IsElevatedTaskInstalled())
                    {
                        StartElevatedTaskSoon();
                        return;
                    }
                }
            }
        }

        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Global\\SwitchPowerTray_SingleInstanceMutex", out createdNew))
        {
            if (!createdNew)
            {
                // App is already running, exit silently
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ManageLogFile();

            // Global shutdown guards
            SystemEvents.SessionEnding += (s, e) => TrayContext.BeginShutdown("Program.SessionEnding");
            SystemEvents.SessionEnded += (s, e) => TrayContext.BeginShutdown("Program.SessionEnded");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => TrayContext.BeginShutdown("Program.ProcessExit");

            Application.ThreadException += (s, e) => TrayContext.LogAndShow("ThreadException", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                TrayContext.LogAndShow("UnhandledException", e.ExceptionObject as Exception);

            try
            {
                WaitForExplorerAndTray(7000);
                bool debug = (args.Length > 0 && string.Equals(args[0], "/debug", StringComparison.OrdinalIgnoreCase));
                Application.Run(new TrayContext(debug));
            }
            catch (Exception ex)
            {
                TrayContext.LogAndShow("MainCatch", ex);
            }
        }
    }
}

public sealed class TrayContext : ApplicationContext
{
    // === PowrProf interop ===
    private const uint ACCESS_SCHEME = 16;
    private const uint ERROR_NO_MORE_ITEMS = 259;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerEnumerate(IntPtr RootPowerKey, IntPtr SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, uint AccessFlags, uint Index, IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadFriendlyName(IntPtr RootPowerKey, ref Guid SchemeGuid, IntPtr SubGroupOfPowerSettingsGuid, IntPtr PowerSettingGuid, IntPtr Buffer, ref uint BufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterForEffectivePowerModeNotifications(uint Version, EffectivePowerModeCallback Callback, IntPtr Context, out IntPtr RegistrationHandle);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetUserConfiguredACPowerMode(ref Guid PowerModeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid PowerModeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid PowerModeGuid);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid PowerModeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterFromEffectivePowerModeNotifications(IntPtr RegistrationHandle);

    private delegate void EffectivePowerModeCallback(int Mode, IntPtr Context);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll") ]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool Hibernate, bool ForceCritical, bool DisableWakeEvent);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out UIntPtr result);

    // Subgroup + setting GUIDs
    private static readonly Guid SUB_BUTTONS = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid SET_PBUTTON = new Guid("7648efa3-dd9c-4e3e-b566-50f929386280");
    private static readonly Guid SET_SBUTTON = new Guid("96996bc0-ad50-47ec-923b-6f41874dd9eb");
    private static readonly Guid SET_LID = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");
    private static readonly Guid SUB_VIDEO = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid SUB_SLEEP = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SET_VIDEOIDLE = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SET_VIDEOCONLOCK = new Guid("8ec4b3a5-6868-48c2-be75-4f3044be88a7");
    private static readonly Guid SET_SLEEPIDLE = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid SET_HIBERNATEIDLE = new Guid("9d7815a6-7ee4-497e-8888-515a05f02364");
    private static readonly Guid SET_UNATTENDSLEEP = new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0");

    // Managed built-in power schemes created by SwitchPowerTray when missing.
    // These GUIDs are deterministic for the app so re-launches never create duplicates.
    private static readonly Guid MANAGED_ALWAYS_ON = new Guid("03b25512-e812-45ae-b15f-f3d257f19bd6");
    private static readonly Guid MANAGED_DESKTOP_DOCK = new Guid("a30c6ddc-23dc-43cb-9a36-90a07ae999e8");
    private static readonly Guid MANAGED_LAPTOP = new Guid("c9500eed-7461-4557-ac04-efda065f31b0");
    private static readonly Guid MANAGED_ENERGY_SAVING = new Guid("9e437415-0f01-4df6-8e22-ed7575fac4b3");
    private static readonly Guid MANAGED_BALANCED = new Guid("8ff9a948-64f6-4079-ad16-189978fb39f2");
    private static readonly Guid MANAGED_MOON = new Guid("14a99604-09ee-4ad7-9e04-6839239ac89d");

    private static readonly Guid WINDOWS_BALANCED_TEMPLATE = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

    private static readonly Guid POWER_MODE_BEST_EFFICIENCY = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid POWER_MODE_BALANCED = Guid.Empty;

    private enum ButtonLidAction : uint { DoNothing = 0, Sleep = 1, Hibernate = 2, Shutdown = 3 }
    private enum UiLanguage { English, Spanish }
    private UiLanguage uiLanguage = UiLanguage.English;
    private bool languageLoadedFromConfig = false;

    private string L(string en, string es) { return (uiLanguage == UiLanguage.Spanish ? es : en); }

    private const string RES_DESKTOP_DARK = "Icon.Desktop.Dark.ico";
    private const string RES_DESKTOP_LIGHT = "Icon.Desktop.Light.ico";
    private const string RES_LAPTOP_DARK = "Icon.Laptop.Dark.ico";
    private const string RES_LAPTOP_LIGHT = "Icon.Laptop.Light.ico";
    private const string RES_BOLT_DARK = "Icon.Bolt.Dark.ico";
    private const string RES_BOLT_LIGHT = "Icon.Bolt.Light.ico";
    private const string RES_BOLT_ACTIVE_DARK = "Icon.BoltActive.Dark.ico";
    private const string RES_BOLT_ACTIVE_LIGHT = "Icon.BoltActive.Light.ico";
    private const string RES_MOON_DARK = "Icon.Moon.Dark.ico";
    private const string RES_MOON_LIGHT = "Icon.Moon.Light.ico";
    private const string RES_BALANCED_DARK = "Icon.Balanced.Dark.ico";
    private const string RES_BALANCED_LIGHT = "Icon.Balanced.Light.ico";
    private const string RES_ENERGYSAVE_DARK = "Icon.EnergySave.Dark.ico";
    private const string RES_ENERGYSAVE_LIGHT = "Icon.EnergySave.Light.ico";

    internal const string AppId = "SwitchPowerTray";
    internal const string ShortcutName = "Switch Power Plan Tray.lnk";

    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwitchPowerTray");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.txt");
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "SwitchPowerTray.log");

    private readonly NotifyIcon tray;
    private readonly bool debug;
    private static volatile bool _blockLaunch = false;
    private enum IconSet { Auto, Light, Dark }
    private IconSet iconSetPref = IconSet.Auto;

    private sealed class SlotConfig
    {
        public char Key;
        public string Guid = "";
        public string LightIconPath = "";
        public string DarkIconPath = "";
        // Whether this slot participates in left-click Toggle cycling.
        // Legacy configurations default to true to preserve existing behavior.
        public bool CycleEnabled = true;
    }

    private readonly SortedDictionary<char, SlotConfig> slots = new SortedDictionary<char, SlotConfig>();
    private Icon exeIcon, lastIcon;
    private readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> fileIcons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> generatedIcons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

    private enum TemporaryAlwaysOnTrigger { Window, Process }
    private enum TemporaryEndAction { ReturnToSlot, Lock, Sleep, Hibernate, ShutDown, Restart, Nothing }

    private sealed class WindowTarget
    {
        public IntPtr Handle;
        public int ProcessId;
        public string Title;
        public string ProcessName;
        public string Display;
        public override string ToString() { return Display; }
    }

    private sealed class ProcessTarget
    {
        public int ProcessId;
        public string ProcessName;
        public string Display;
        public override string ToString() { return Display; }
    }

    private bool _temporaryAlwaysOnActive;
    private TemporaryAlwaysOnTrigger _temporaryTrigger;
    private IntPtr _temporaryWindowHandle = IntPtr.Zero;
    private int _temporaryProcessId;
    private string _temporaryReturnGuid = "";
    private char _temporaryReturnSlot = '\0';
    private TemporaryEndAction _temporaryEndAction = TemporaryEndAction.ReturnToSlot;
    private string _temporaryTriggerDescription = "";
    private System.Windows.Forms.Timer _temporaryAlwaysOnTimer;

    private string activeGuid = "";
    private List<Plan> plans = new List<Plan>();

    private sealed class Plan
    {
        public string Guid;
        public string Name;
        public bool IsActive;
        public override string ToString() { return Name + " (" + Guid + (IsActive ? ", Active" : "") + ")"; }
    }

    private struct AssignTagDynamic
    {
        public char SlotKey;
        public string Guid;
        public AssignTagDynamic(char s, string g) { SlotKey = s; Guid = g; }
    }

    private bool _busy;
    private ContextMenuStrip _openContextMenu;

    // User-configured Windows 11 Power Mode values saved when entering
    // Energy Saving, so leaving the mode can restore the previous user choice.
    private Guid _savedAcPowerMode = Guid.Empty;
    private Guid _savedDcPowerMode = Guid.Empty;
    private bool _savedPowerModesForEnergyMode;
    private readonly object _specialModeLock = new object();
    private bool _specialModeWorkerRunning;
    private bool _specialModeRequestPending;
    private bool _nightLightEnabledByApp;
    private IntPtr powerNotifyHandle = IntPtr.Zero;
    private EffectivePowerModeCallback _powerModeCallback;
    private readonly Control _syncControl;

    // === Event Watchers ===
    private sealed class SystemEventWatcher : NativeWindow, IDisposable
    {
        private const int WM_SETTINGCHANGE = 0x001A;
        private readonly Action _onThemeChange;
        private readonly Action _onTaskbarCreated;
        private readonly uint _wmTaskbarCreated;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        public SystemEventWatcher(Action onThemeChange, Action onTaskbarCreated)
        {
            CreateHandle(new CreateParams());
            _onThemeChange = onThemeChange;
            _onTaskbarCreated = onTaskbarCreated;
            _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                string area = Marshal.PtrToStringUni(m.LParam);
                if (area == "ImmersiveColorSet" && _onThemeChange != null) _onThemeChange();
            }
            else if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
            {
                if (_onTaskbarCreated != null) _onTaskbarCreated();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    private sealed class EndSessionWatcher : NativeWindow, IDisposable
    {
        private const int WM_QUERYENDSESSION = 0x0011;
        private const int WM_ENDSESSION = 0x0016;

        public EndSessionWatcher() { CreateHandle(new CreateParams()); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION) TrayContext.BeginShutdown("WM_ENDSESSION");
            base.WndProc(ref m);
        }

        public void Dispose() { try { if (this.Handle != IntPtr.Zero) this.DestroyHandle(); } catch { } }
    }

    private SystemEventWatcher themeWatcher;
    private EndSessionWatcher endWatcher;

    public TrayContext(bool debugMode)
    {
        debug = debugMode;
        try { exeIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { exeIcon = SystemIcons.Application; }

        LoadAllIcons();
        LoadConfig();
        EnsureDefaultSlots();
        EnsureBuiltInPowerSchemes();
        DetectLanguageIfNotSet();

        tray = new NotifyIcon();
        tray.Visible = false;
        tray.Text = L("Switch Power Plan", "Cambiar plan de energía");
        tray.ContextMenuStrip = BuildMenu();
        tray.MouseClick += OnTrayMouseClick;

        endWatcher = new EndSessionWatcher();
        themeWatcher = new SystemEventWatcher(
            () => { if (iconSetPref == IconSet.Auto) UpdateTrayIcon(); },
            () => { try { if (tray != null) { tray.Visible = false; tray.Visible = true; UpdateTrayIcon(); } } catch { } }
        );

        // Create an invisible dummy control to handle thread marshalling
        _syncControl = new Control();
        IntPtr forceHandle = _syncControl.Handle; // Force the OS handle to be created

        // Store the delegate in the class variable to prevent Garbage Collection
        _powerModeCallback = delegate (int mode, IntPtr ctx)
        {
            try
            {
                // Safely marshal the background notification to the main UI thread
                _syncControl.BeginInvoke(new Action(delegate ()
                {
                    activeGuid = GetActiveSchemeGuid();
                    RefreshPlansAndIcon();
                    QueueSpecialModeReconcile();
                }));
            }
            catch { }
        };

        // Register the safe callback
        PowerRegisterForEffectivePowerModeNotifications(1, _powerModeCallback, IntPtr.Zero, out powerNotifyHandle);

        for (int i = 0; i < 5; i++)
        {
            RefreshPlansAndIcon();
            if (!string.IsNullOrEmpty(activeGuid)) break;
            Thread.Sleep(250);
        }

        // Reconcile special Windows integrations in the background so startup
        // cannot be blocked by UAC, gpupdate, or CloudStore work.
        QueueSpecialModeReconcile();

        tray.Visible = true;

        SystemEvents.SessionEnding += (s, e) => { BeginShutdown("Context.SessionEnding"); };
        SystemEvents.SessionEnded += (s, e) => { BeginShutdown("Context.SessionEnded"); };
        Application.ApplicationExit += (s, e) => { BeginShutdown("Context.ApplicationExit"); };
    }

    private void EnsureDefaultSlots()
    {
        if (!slots.ContainsKey('A')) slots['A'] = new SlotConfig { Key = 'A' };
        if (!slots.ContainsKey('B')) slots['B'] = new SlotConfig { Key = 'B' };
        if (!slots.ContainsKey('C')) slots['C'] = new SlotConfig { Key = 'C' };
        if (!slots.ContainsKey('D')) slots['D'] = new SlotConfig { Key = 'D' };
        if (!slots.ContainsKey('E')) slots['E'] = new SlotConfig { Key = 'E' };
        if (!slots.ContainsKey('F')) slots['F'] = new SlotConfig { Key = 'F' };
    }


    private void EnsureBuiltInPowerSchemes()
    {
        try
        {
            // Balanced is the first template. If Windows' standard Balanced scheme
            // is missing, create a replacement Balanced scheme from any available
            // scheme and keep its generated/fixed GUID under our control.
            plans = ListPlans();
            string balancedGuid = EnsureManagedPlan(
                "Balanced",
                MANAGED_BALANCED,
                WINDOWS_BALANCED_TEMPLATE,
                "Balanced power plan managed by SwitchPowerTray.",
                null);

            if (string.IsNullOrEmpty(balancedGuid))
                return;

            // Create/locate the five companion modes. Existing plans with the
            // requested names are never reconfigured; only newly created plans
            // receive the layouts below.
            string alwaysOn = EnsureManagedPlan(
                "Always On",
                MANAGED_ALWAYS_ON,
                Guid.Parse(balancedGuid),
                "Keeps the computer and display on until the user changes power settings.",
                ConfigureAlwaysOnPlan);

            string dock = EnsureManagedPlan(
                "Desktop Docking Station",
                MANAGED_DESKTOP_DOCK,
                Guid.Parse(balancedGuid),
                "Docked desktop-style mode: closing the lid does not sleep or hibernate.",
                ConfigureDesktopDockPlan);

            string laptop = EnsureManagedPlan(
                "Laptop On The Go",
                MANAGED_LAPTOP,
                Guid.Parse(balancedGuid),
                "Portable laptop mode with hibernation-oriented lid and power behavior.",
                ConfigureLaptopPlan);

            string energy = EnsureManagedPlan(
                "Energy Saving",
                MANAGED_ENERGY_SAVING,
                Guid.Parse(balancedGuid),
                "Power-saving mode with shorter idle timers and energy saver behavior.",
                ConfigureEnergySavingPlan);

            string moon = EnsureManagedPlan(
                "Night",
                MANAGED_MOON,
                Guid.Parse(balancedGuid),
                "Night mode: turns off the display after 2.5 minutes of inactivity and enables Night light when supported.",
                ConfigureMoonPlan);

            AssignManagedSchemeToSlot('A', dock);
            AssignManagedSchemeToSlot('B', laptop);
            AssignManagedSchemeToSlot('C', alwaysOn);
            AssignManagedSchemeToSlot('D', moon);
            AssignManagedSchemeToSlot('E', balancedGuid);
            AssignManagedSchemeToSlot('F', energy);

            SaveConfig();
            EnsurePlanList();
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  EnsureBuiltInPowerSchemes: " + ex + Environment.NewLine); } catch { }
        }
    }

    private delegate void NewPlanConfigurator(Guid scheme);

    private string EnsureManagedPlan(string desiredName, Guid managedGuid, Guid preferredTemplate,
                                     string description, NewPlanConfigurator configurator)
    {
        List<Plan> currentPlans = ListPlans();
        foreach (Plan p in currentPlans)
        {
            if (string.Equals(p.Name, desiredName, StringComparison.OrdinalIgnoreCase))
                return p.Guid;
        }

        Guid template = preferredTemplate;
        bool templateExists = false;
        foreach (Plan p in currentPlans)
        {
            if (string.Equals(p.Guid, template.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                templateExists = true;
                break;
            }
        }

        if (!templateExists)
        {
            // For Balanced only, the Windows standard GUID may be unavailable.
            // Fall back to the first scheme that exists.
            if (string.Equals(desiredName, "Balanced", StringComparison.OrdinalIgnoreCase) && currentPlans.Count > 0)
            {
                template = Guid.Parse(currentPlans[0].Guid);
                templateExists = true;
            }
        }

        if (!templateExists)
            return "";

        // If our managed GUID already exists, use it as-is. This is useful after
        // an interrupted startup where duplication completed but naming did not.
        foreach (Plan p in currentPlans)
        {
            if (string.Equals(p.Guid, managedGuid.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(p.Name, desiredName, StringComparison.Ordinal))
                    RunPowerCfg(new[] { "/changename", p.Guid, desiredName, description });
                return managedGuid.ToString();
            }
        }

        int rc = RunPowerCfg(new[] { "/duplicatescheme", template.ToString(), managedGuid.ToString() });
        if (rc != 0)
        {
            // Do not create a duplicate under an arbitrary GUID on failure.
            return "";
        }

        RunPowerCfg(new[] { "/changename", managedGuid.ToString(), desiredName, description });

        if (configurator != null)
            configurator(managedGuid);

        return managedGuid.ToString();
    }

    private int RunPowerCfg(string[] args)
    {
        try
        {
            string argText = "";
            foreach (string arg in args)
            {
                if (arg == null) continue;
                if (argText.Length > 0) argText += " ";
                argText += QuoteCommandArg(arg);
            }

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                Arguments = argText,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                if (p == null) return -1;
                string output = p.StandardOutput.ReadToEnd();
                string error = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0)
                {
                    try
                    {
                        File.AppendAllText(
                            LogPath,
                            DateTime.Now.ToString("s") + "  powercfg " + argText +
                            " => " + p.ExitCode + "\r\n" +
                            output + "\r\n" + error + "\r\n");
                    }
                    catch { }
                }
                return p.ExitCode;
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  powercfg exception: " + ex + Environment.NewLine); } catch { }
            return -1;
        }
    }

    private static string QuoteCommandArg(string s)
    {
        if (s == null) return "\"\"";
        if (s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    private void ConfigureAlwaysOnPlan(Guid scheme)
    {
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, false, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, false, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, false, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, true, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, true, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, true, 0);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, false, 0);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, true, 0);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, false, 0);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, false, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, false, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, true, 0);
    }

    private void ConfigureDesktopDockPlan(Guid scheme)
    {
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, false, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, true, 0);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, false, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, true, 1);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, true, 15 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, false, 5 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, true, 15 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, false, 5 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, false, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, false, 0);
        SetPlanValue(scheme, SUB_DISK, SET_DISKIDLE, true, 30 * 60);
        SetPlanValue(scheme, SUB_DISK, SET_DISKIDLE, false, 30 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, false, 0);
    }

    private void ConfigureLaptopPlan(Guid scheme)
    {
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, false, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, true, 1);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, true, 5 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, false, 3 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, true, 60 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, false, 20 * 60);
    }

    private void ConfigureEnergySavingPlan(Guid scheme)
    {
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, false, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, true, 1);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, true, 5 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, false, 2 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, true, 2 * 60);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, false, 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, true, 15 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, false, 5 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, true, 60 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, false, 20 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, false, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_HYBRIDSLEEP, true, 1);
        SetPlanValue(scheme, SUB_SLEEP, SET_HYBRIDSLEEP, false, 1);
        SetPlanValue(scheme, SUB_DISK, SET_DISKIDLE, true, 10 * 60);
        SetPlanValue(scheme, SUB_DISK, SET_DISKIDLE, false, 5 * 60);
        SetPlanValue(scheme, SUB_ENERGYSAVER, SET_ESBRIGHTNESS, true, 50);
        SetPlanValue(scheme, SUB_ENERGYSAVER, SET_ESBRIGHTNESS, false, 50);
    }

    private void ConfigureMoonPlan(Guid scheme)
    {
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, false, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_LID, true, 2);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, false, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_PBUTTON, true, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, false, 1);
        SetPlanValue(scheme, SUB_BUTTONS, SET_SBUTTON, true, 1);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, true, 150);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOIDLE, false, 150);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, true, 150);
        SetPlanValue(scheme, SUB_VIDEO, SET_VIDEOCONLOCK, false, 150);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, true, 5 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_SLEEPIDLE, false, 2 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, true, 30 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_HIBERNATEIDLE, false, 10 * 60);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, true, 0);
        SetPlanValue(scheme, SUB_SLEEP, SET_ALLOWWAKE, false, 0);
    }

    private static readonly Guid SUB_DISK = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid SET_DISKIDLE = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static readonly Guid SET_HYBRIDSLEEP = new Guid("94ac6d29-73ce-41a6-809f-6363ba21b47e");
    private static readonly Guid SET_ALLOWWAKE = new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");
    private static readonly Guid SUB_ENERGYSAVER = new Guid("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid SET_ESBRIGHTNESS = new Guid("13d09884-f74e-474a-a852-b6bde8ad03a8");

    private void SetPlanValue(Guid scheme, Guid subgroup, Guid setting, bool ac, uint value)
    {
        try
        {
            Guid sub = subgroup, set = setting;
            uint rc = ac
                ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value)
                : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value);

            if (rc != 0)
            {
                try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  PowerWrite " + scheme + " " + setting + " " + (ac ? "AC" : "DC") + " = " + value + " => " + rc + Environment.NewLine); } catch { }
            }
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  SetPlanValue exception: " + ex + Environment.NewLine); } catch { }
        }
    }

    private void AssignManagedSchemeToSlot(char slotKey, string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;

        // Keep the one-plan/one-slot invariant: remove the managed plan from any
        // other slot before assigning its canonical built-in slot.
        foreach (KeyValuePair<char, SlotConfig> kv in slots)
        {
            if (kv.Key == slotKey) continue;
            if (string.Equals(kv.Value.Guid, guid, StringComparison.OrdinalIgnoreCase))
                kv.Value.Guid = "";
        }

        if (!slots.ContainsKey(slotKey))
            slots[slotKey] = new SlotConfig { Key = slotKey };

        slots[slotKey].Guid = guid;
    }

    private void DetectLanguageIfNotSet()
    {
        if (languageLoadedFromConfig) return;
        try
        {
            string ui = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            uiLanguage = string.Equals(ui, "es", StringComparison.OrdinalIgnoreCase) ? UiLanguage.Spanish : UiLanguage.English;
        }
        catch { uiLanguage = UiLanguage.English; }
    }

    internal static void BeginShutdown(string src)
    {
        _blockLaunch = true;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  BeginShutdown: " + src + Environment.NewLine); } catch { }
    }

    // ---------- Event handlers ----------
    private void OnTrayMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_temporaryAlwaysOnActive && !string.IsNullOrEmpty(_temporaryReturnGuid))
                TrySetActive(_temporaryReturnGuid);
            else
                ToggleToNextAssigned();
        }
    }

    private bool IsPlanAssignedToAnotherSlot(char currentSlotKey, string guid)
    {
        if (string.IsNullOrEmpty(guid)) return false;

        foreach (KeyValuePair<char, SlotConfig> kv in slots)
        {
            if (kv.Key == currentSlotKey) continue;
            if (!string.IsNullOrEmpty(kv.Value.Guid) &&
                string.Equals(kv.Value.Guid, guid, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void OnAssignClickDynamic(object sender, EventArgs e)
    {
        ToolStripMenuItem mi = sender as ToolStripMenuItem;
        if (mi == null || mi.Tag == null) return;
        AssignTagDynamic t = (AssignTagDynamic)mi.Tag;

        EnsureDefaultSlots();
        if (!slots.ContainsKey(t.SlotKey)) slots[t.SlotKey] = new SlotConfig { Key = t.SlotKey };

        if (IsPlanAssignedToAnotherSlot(t.SlotKey, t.Guid))
            return;

        slots[t.SlotKey].Guid = t.Guid ?? "";
        SaveConfig();
        UpdateTrayIcon();
    }

    private void OnSwitchToClick(object sender, EventArgs e)
    {
        ToolStripMenuItem mi = sender as ToolStripMenuItem;
        if (mi == null || mi.Tag == null) return;
        string guid = mi.Tag as string;
        if (!string.IsNullOrEmpty(guid)) TrySetActive(guid);
    }

    private void OnThemeAuto(object sender, EventArgs e) { iconSetPref = IconSet.Auto; SaveConfig(); UpdateTrayIcon(); }
    private void OnThemeLight(object sender, EventArgs e) { iconSetPref = IconSet.Light; SaveConfig(); UpdateTrayIcon(); }
    private void OnThemeDark(object sender, EventArgs e) { iconSetPref = IconSet.Dark; SaveConfig(); UpdateTrayIcon(); }

    private void OnLanguageEnglish(object sender, EventArgs e) { uiLanguage = UiLanguage.English; SaveConfig(); CloseContextMenu(); RebuildMenu(); }
    private void OnLanguageSpanish(object sender, EventArgs e) { uiLanguage = UiLanguage.Spanish; SaveConfig(); CloseContextMenu(); RebuildMenu(); }

    private void RebuildMenu()
    {
        if (tray == null) return;
        tray.ContextMenuStrip = BuildMenu();
        tray.Text = L("Switch Power Plan", "Cambiar plan de energía");
    }

    private void OnOpenPowerOptions(object sender, EventArgs e) { try { Process.Start("control.exe", "powercfg.cpl"); } catch { } }

    private void OnExit(object sender, EventArgs e) { ExitThread(); }

    private void WireNoCloseOnItemClick(ToolStripDropDown drop)
    {
        // Kept for source compatibility only.
        // The old Closing handler made WinForms menus stay open when the user
        // clicked outside them. Normal WinForms closing behavior is intentional.
    }

    private void CloseContextMenu()
    {
        try
        {
            if (_openContextMenu != null && !_openContextMenu.IsDisposed)
                _openContextMenu.Close(ToolStripDropDownCloseReason.Keyboard);
        }
        catch { }
    }

    private void OnContextMenuOpening(object sender, CancelEventArgs e)
    {
        _openContextMenu = sender as ContextMenuStrip;
    }

    private void OnContextMenuClosed(object sender, ToolStripDropDownClosedEventArgs e)
    {
        if (ReferenceEquals(_openContextMenu, sender))
            _openContextMenu = null;
    }

    private bool IsStandardSlot(char slotKey) { return slotKey >= 'A' && slotKey <= 'F'; }

    private string GetStandardIconName(char slotKey)
    {
        switch (slotKey)
        {
            case 'A': return L("Desktop Icon", "Icono de escritorio");
            case 'B': return L("Laptop Icon", "Icono de laptop");
            case 'C': return L("Bolt Icon", "Icono de rayo");
            case 'D': return L("Night Icon", "Icono de noche");
            case 'E': return L("Balanced Icon", "Icono de equilibrado");
            case 'F': return L("Energy Saving Icon", "Icono de ahorro de energía");
            default: return "";
        }
    }

    private string SlotMenuTitle(char slotKey)
    {
        if (IsStandardSlot(slotKey)) return L("Assign Slot " + slotKey + " (" + GetStandardIconName(slotKey) + ") →", "Asignar ranura " + slotKey + " (" + GetStandardIconName(slotKey) + ") →");
        return L("Assign Slot " + slotKey + " →", "Asignar ranura " + slotKey + " →");
    }

    // ---------- Menu construction ----------
    private ContextMenuStrip BuildMenu()
    {
        EnsureDefaultSlots();
        var menu = new ContextMenuStrip();

        // Main menu intentionally kept compact. Advanced/customization features live under Advanced Settings.
        var temporaryAlwaysOnItem = new ToolStripMenuItem(L("Set Temporary Always On…", "Configurar Siempre encendido temporal…"));
        temporaryAlwaysOnItem.Click += delegate { OnTemporaryAlwaysOnMenuClick(); };
        menu.Opening += delegate
        {
            temporaryAlwaysOnItem.Text = _temporaryAlwaysOnActive
                ? L("Stop Temporary Always On", "Detener Siempre encendido temporal")
                : L("Set Temporary Always On…", "Configurar Siempre encendido temporal…");
            temporaryAlwaysOnItem.Checked = _temporaryAlwaysOnActive;
        };
        menu.Items.Add(temporaryAlwaysOnItem);

        // Startup Registry Toggle
        var startupItem = new ToolStripMenuItem(L("Run at Startup", "Ejecutar al iniciar Windows"));
        menu.Opening += delegate { startupItem.Checked = IsRunAtStartupEnabled(); };
        startupItem.Click += delegate { ToggleRunAtStartup(); };
        menu.Items.Add(startupItem);

        menu.Items.Add(BuildAdvancedSettingsMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L("Exit (Close Program)", "Salir (Cerrar Programa)"), null, OnExit));

        menu.Opening += OnContextMenuOpening;
        menu.Closed += OnContextMenuClosed;
        return menu;
    }

    private ToolStripMenuItem BuildAdvancedSettingsMenu()
    {
        var root = new ToolStripMenuItem(L("Advanced Settings →", "Configuración avanzada →"));
        root.DropDownOpening += delegate
        {
            root.DropDownItems.Clear();

            root.DropDownItems.Add(new ToolStripMenuItem(L("Toggle now", "Cambiar ahora"), null, (EventHandler)OnToggleNow));
            root.DropDownItems.Add(new ToolStripMenuItem(L("Configure Toggle Cycle…", "Configurar ciclo de cambio…"), null, OnConfigureToggleCycle));
            root.DropDownItems.Add(new ToolStripSeparator());

            EnsureDefaultSlots();
            foreach (KeyValuePair<char, SlotConfig> kv in slots)
                root.DropDownItems.Add(BuildAssignSubmenuDynamic(kv.Key));

            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem(L("Add Slot (next letter)…", "Agregar ranura (siguiente letra)…"), null, OnAddSlot));
            root.DropDownItems.Add(new ToolStripSeparator());

            root.DropDownItems.Add(BuildSwitchToSubmenu());
            root.DropDownItems.Add(BuildThemeMenu());
            root.DropDownItems.Add(BuildLanguageMenu());
            root.DropDownItems.Add(BuildCustomizeButtonsMenu());
            root.DropDownItems.Add(BuildCustomizeDisplaySleepMenu());

            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem(L("Open Power Options…", "Abrir opciones de energía…"), null, OnOpenPowerOptions));
        };
        WireNoCloseOnItemClick(root.DropDown);
        return root;
    }

    private ToolStripMenuItem BuildLanguageMenu()
    {
        var sub = new ToolStripMenuItem(L("Language", "Idioma"));
        sub.DropDownOpening += delegate
        {
            sub.DropDownItems.Clear();
            var enItem = new ToolStripMenuItem("English", null, OnLanguageEnglish) { Checked = (uiLanguage == UiLanguage.English) };
            var esItem = new ToolStripMenuItem("Español", null, OnLanguageSpanish) { Checked = (uiLanguage == UiLanguage.Spanish) };
            sub.DropDownItems.Add(enItem);
            sub.DropDownItems.Add(esItem);
        };
        WireNoCloseOnItemClick(sub.DropDown);
        return sub;
    }

    private void OnToggleNow(object sender, EventArgs e) { ToggleToNextAssigned(); }

    private void OnTemporaryAlwaysOnMenuClick()
    {
        CloseContextMenu();

        if (_temporaryAlwaysOnActive)
        {
            string returnGuid = _temporaryReturnGuid;
            char returnSlot = _temporaryReturnSlot;
            TemporaryEndAction endAction = _temporaryEndAction;
            ClearTemporaryAlwaysOnState();
            if (endAction == TemporaryEndAction.ReturnToSlot && !string.IsNullOrEmpty(returnGuid))
                TrySetActive(returnGuid);
            return;
        }

        ShowTemporaryAlwaysOnDialog();
    }

    private void ShowTemporaryAlwaysOnDialog()
    {
        EnsureDefaultSlots();
        EnsurePlanList();

        string alwaysOnGuid = FindGuidForPlanNameOrManagedAlwaysOn();
        if (string.IsNullOrEmpty(alwaysOnGuid))
        {
            MessageBox.Show(L("The Always On power plan could not be found.", "No se encontró el plan de energía Siempre encendido."),
                L("Temporary Always On", "Siempre encendido temporal"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var returnItems = new List<Tuple<char, string, string>>();
        foreach (KeyValuePair<char, SlotConfig> kv in slots)
        {
            if (string.IsNullOrEmpty(kv.Value.Guid)) continue;
            returnItems.Add(Tuple.Create(kv.Key, kv.Value.Guid, kv.Key + ": " + FindPlanName(kv.Value.Guid)));
        }

        using (Form f = new Form
        {
            Text = L("Temporary Always On", "Siempre encendido temporal"),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(610, 405)
        })
        {
            Label intro = new Label
            {
                Left = 14, Top = 14, Width = 580, Height = 54,
                Text = L(
                    "Keep the system in Always On while a selected window remains open or a selected application/process is running. When it ends, Switch Power Plan Tray automatically returns to the slot you choose below.",
                    "Mantén el sistema en Siempre encendido mientras una ventana seleccionada permanezca abierta o una aplicación/proceso seleccionado siga ejecutándose. Cuando termine, Switch Power Plan Tray volverá automáticamente a la ranura elegida abajo."),
                AutoSize = false
            };
            f.Controls.Add(intro);

            RadioButton windowRadio = new RadioButton
            {
                Left = 18, Top = 82, Width = 210,
                Text = L("Selected window", "Ventana seleccionada"),
                Checked = true
            };
            RadioButton processRadio = new RadioButton
            {
                Left = 235, Top = 82, Width = 260,
                Text = L("Running task / process", "Tarea / proceso en ejecución")
            };
            f.Controls.Add(windowRadio); f.Controls.Add(processRadio);

            ComboBox windowCombo = new ComboBox
            {
                Left = 18, Top = 112, Width = 470, Height = 26,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            ComboBox processCombo = new ComboBox
            {
                Left = 18, Top = 112, Width = 470, Height = 26,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Visible = false
            };
            Button refreshTargets = new Button
            {
                Left = 500, Top = 111, Width = 92, Height = 28,
                Text = L("Refresh", "Actualizar")
            };
            f.Controls.Add(windowCombo); f.Controls.Add(processCombo); f.Controls.Add(refreshTargets);

            Label targetHint = new Label
            {
                Left = 18, Top = 145, Width = 570, Height = 36,
                Text = L("The selected window/process must be running when you start the temporary mode.", "La ventana/proceso seleccionado debe estar ejecutándose al iniciar el modo temporal."),
                AutoSize = false
            };
            f.Controls.Add(targetHint);

            Label endActionLabel = new Label
            {
                Left = 18, Top = 191, Width = 260, Height = 22,
                Text = L("When it ends:", "Cuando termine:")
            };
            f.Controls.Add(endActionLabel);

            ComboBox endActionCombo = new ComboBox
            {
                Left = 18, Top = 216, Width = 390, Height = 27,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            endActionCombo.Items.Add(L("Return to selected slot", "Volver a la ranura seleccionada"));
            endActionCombo.Items.Add(L("Lock", "Bloquear"));
            endActionCombo.Items.Add(L("Sleep", "Suspender"));
            endActionCombo.Items.Add(L("Hibernate", "Hibernar"));
            endActionCombo.Items.Add(L("Shut down", "Apagar"));
            endActionCombo.Items.Add(L("Restart", "Reiniciar"));
            endActionCombo.Items.Add(L("Nothing", "Nada"));
            endActionCombo.SelectedIndex = 0;
            f.Controls.Add(endActionCombo);

            Label returnLabel = new Label
            {
                Left = 18, Top = 250, Width = 300, Height = 22,
                Text = L("Return to slot:", "Volver a la ranura:")
            };
            f.Controls.Add(returnLabel);

            ComboBox returnCombo = new ComboBox
            {
                Left = 18, Top = 275, Width = 390, Height = 27,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DisplayMember = "Item3"
            };
            foreach (Tuple<char, string, string> item in returnItems) returnCombo.Items.Add(item);
            int defaultReturn = 0;
            for (int i = 0; i < returnItems.Count; i++)
            {
                if (string.Equals(returnItems[i].Item2, activeGuid, StringComparison.OrdinalIgnoreCase)) { defaultReturn = i; break; }
            }
            if (returnItems.Count > 0) returnCombo.SelectedIndex = defaultReturn;
            f.Controls.Add(returnCombo);

            Button ok = new Button
            {
                Text = L("Start", "Iniciar"),
                Left = 420, Top = 350, Width = 80, Height = 30,
                DialogResult = DialogResult.None
            };
            Button cancel = new Button
            {
                Text = L("Cancel", "Cancelar"),
                Left = 510, Top = 350, Width = 80, Height = 30,
                DialogResult = DialogResult.Cancel
            };
            f.Controls.Add(ok); f.Controls.Add(cancel);
            f.AcceptButton = ok; f.CancelButton = cancel;

            Action populateWindows = delegate
            {
                IntPtr selectedHandle = IntPtr.Zero;
                WindowTarget selectedTarget = windowCombo.SelectedItem as WindowTarget;
                if (selectedTarget != null) selectedHandle = selectedTarget.Handle;
                windowCombo.Items.Clear();

                foreach (WindowTarget wt in EnumerateWindowTargets()) windowCombo.Items.Add(wt);

                if (selectedHandle != IntPtr.Zero)
                {
                    for (int i = 0; i < windowCombo.Items.Count; i++)
                    {
                        WindowTarget wt = windowCombo.Items[i] as WindowTarget;
                        if (wt != null && wt.Handle == selectedHandle) { windowCombo.SelectedIndex = i; break; }
                    }
                }
                if (windowCombo.SelectedIndex < 0 && windowCombo.Items.Count > 0) windowCombo.SelectedIndex = 0;
            };

            Action populateProcesses = delegate
            {
                int selectedPid = 0;
                ProcessTarget selectedTarget = processCombo.SelectedItem as ProcessTarget;
                if (selectedTarget != null) selectedPid = selectedTarget.ProcessId;
                processCombo.Items.Clear();

                foreach (ProcessTarget pt in EnumerateProcessTargets()) processCombo.Items.Add(pt);

                if (selectedPid != 0)
                {
                    for (int i = 0; i < processCombo.Items.Count; i++)
                    {
                        ProcessTarget pt = processCombo.Items[i] as ProcessTarget;
                        if (pt != null && pt.ProcessId == selectedPid) { processCombo.SelectedIndex = i; break; }
                    }
                }
                if (processCombo.SelectedIndex < 0 && processCombo.Items.Count > 0) processCombo.SelectedIndex = 0;
            };

            populateWindows();
            populateProcesses();

            Action syncEndAction = delegate
            {
                bool useReturnSlot = endActionCombo.SelectedIndex == (int)TemporaryEndAction.ReturnToSlot;
                returnLabel.Visible = useReturnSlot;
                returnCombo.Visible = useReturnSlot;
            };

            Action syncMode = delegate
            {
                windowCombo.Visible = windowRadio.Checked;
                processCombo.Visible = processRadio.Checked;
                targetHint.Text = windowRadio.Checked
                    ? L("The selected window must remain open. Minimizing it is fine; closing it ends the temporary mode.", "La ventana seleccionada debe permanecer abierta. Puede minimizarse; al cerrarla termina el modo temporal.")
                    : L("The selected process is monitored by its current process ID. When that process exits, the temporary mode ends.", "El proceso seleccionado se vigila por su ID actual. Cuando ese proceso termina, finaliza el modo temporal.");
            };
            windowRadio.CheckedChanged += delegate { syncMode(); };
            processRadio.CheckedChanged += delegate { syncMode(); };
            endActionCombo.SelectedIndexChanged += delegate { syncEndAction(); };
            refreshTargets.Click += delegate { if (windowRadio.Checked) populateWindows(); else populateProcesses(); };
            syncMode();
            syncEndAction();

            ok.Click += delegate
            {
                TemporaryEndAction endAction = (TemporaryEndAction)Math.Max(0, endActionCombo.SelectedIndex);
                char returnSlot = '\0';
                string returnGuid = "";

                if (endAction == TemporaryEndAction.ReturnToSlot)
                {
                    if (returnCombo.SelectedIndex < 0 || returnItems.Count == 0)
                    {
                        MessageBox.Show(f, L("Select a return slot.", "Selecciona una ranura de retorno."));
                        return;
                    }

                    returnSlot = returnItems[returnCombo.SelectedIndex].Item1;
                    returnGuid = returnItems[returnCombo.SelectedIndex].Item2;

                    if (string.Equals(returnGuid, alwaysOnGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        DialogResult confirm = MessageBox.Show(f,
                            L("The selected return slot is Always On, so no power-plan change will occur when the trigger ends. Continue?",
                              "La ranura de retorno seleccionada es Siempre encendido, por lo que no habrá cambio de plan cuando termine el activador. ¿Continuar?"),
                            L("Temporary Always On", "Siempre encendido temporal"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (confirm != DialogResult.Yes) return;
                    }
                }

                if (windowRadio.Checked)
                {
                    WindowTarget wt = windowCombo.SelectedItem as WindowTarget;
                    if (wt == null || !IsWindow(wt.Handle))
                    {
                        MessageBox.Show(f, L("Select an open window.", "Selecciona una ventana abierta."));
                        return;
                    }

                    _temporaryTrigger = TemporaryAlwaysOnTrigger.Window;
                    _temporaryWindowHandle = wt.Handle;
                    _temporaryProcessId = wt.ProcessId;
                    _temporaryTriggerDescription = wt.Title;
                }
                else
                {
                    ProcessTarget pt = processCombo.SelectedItem as ProcessTarget;
                    if (pt == null)
                    {
                        MessageBox.Show(f, L("Select a running process.", "Selecciona un proceso en ejecución."));
                        return;
                    }

                    if (!IsProcessRunning(pt.ProcessId))
                    {
                        MessageBox.Show(f, L("That process is no longer running. Refresh the list and choose it again.", "Ese proceso ya no está ejecutándose. Actualiza la lista y vuelve a elegirlo."));
                        return;
                    }

                    _temporaryTrigger = TemporaryAlwaysOnTrigger.Process;
                    _temporaryWindowHandle = IntPtr.Zero;
                    _temporaryProcessId = pt.ProcessId;
                    _temporaryTriggerDescription = pt.Display;
                }

                _temporaryReturnGuid = returnGuid;
                _temporaryReturnSlot = returnSlot;
                _temporaryEndAction = endAction;
                _temporaryAlwaysOnActive = true;

                if (_temporaryAlwaysOnTimer != null) _temporaryAlwaysOnTimer.Stop();
                if (_temporaryAlwaysOnTimer == null)
                {
                    _temporaryAlwaysOnTimer = new System.Windows.Forms.Timer();
                    _temporaryAlwaysOnTimer.Interval = 400;
                    _temporaryAlwaysOnTimer.Tick += delegate { CheckTemporaryAlwaysOnTrigger(); };
                }

                f.DialogResult = DialogResult.OK;
                f.Close();

                bool activated = TrySetActiveInternal(alwaysOnGuid, true);

                if (!activated)
                {
                    ClearTemporaryAlwaysOnState();
                    MessageBox.Show(
                        L("Could not activate the Always On power plan. The temporary mode was not started.",
                          "No se pudo activar el plan Siempre encendido. El modo temporal no se inició."),
                        L("Temporary Always On", "Siempre encendido temporal"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_temporaryAlwaysOnActive)
                    _temporaryAlwaysOnTimer.Start();
            };

            f.ShowDialog();
        }
    }

    private string FindGuidForPlanNameOrManagedAlwaysOn()
    {
        // Prefer the actual installed plan named "Always On". This makes the
        // temporary feature work regardless of which slot is currently active
        // and also survives cases where the managed GUID changed or an older
        // test build provisioned the plan differently.
        foreach (Plan p in plans)
            if (string.Equals(p.Name, "Always On", StringComparison.OrdinalIgnoreCase))
                return p.Guid;

        string managed = MANAGED_ALWAYS_ON.ToString();
        foreach (Plan p in plans)
            if (string.Equals(p.Guid, managed, StringComparison.OrdinalIgnoreCase))
                return p.Guid;

        return "";
    }

    private List<WindowTarget> EnumerateWindowTargets()
    {
        var result = new List<WindowTarget>();
        try
        {
            int ownPid = Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                try
                {
                    if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd) || !IsWindow(hWnd)) return true;
                    int length = GetWindowTextLength(hWnd);
                    if (length <= 0) return true;
                    StringBuilder sb = new StringBuilder(length + 1);
                    GetWindowText(hWnd, sb, sb.Capacity);
                    string title = sb.ToString().Trim();
                    if (string.IsNullOrEmpty(title)) return true;

                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0 || pid == (uint)ownPid) return true;

                    string processName = "Process";
                    try { using (Process p = Process.GetProcessById((int)pid)) processName = p.ProcessName; } catch { }

                    result.Add(new WindowTarget
                    {
                        Handle = hWnd,
                        ProcessId = (int)pid,
                        Title = title,
                        ProcessName = processName,
                        Display = title + "  [" + processName + "]"
                    });
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            result.Sort(delegate (WindowTarget a, WindowTarget b) { return string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase); });
        }
        catch { }
        return result;
    }

    private List<ProcessTarget> EnumerateProcessTargets()
    {
        var result = new List<ProcessTarget>();
        try
        {
            int ownPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == ownPid) continue;
                    string name = p.ProcessName;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new ProcessTarget
                    {
                        ProcessId = p.Id,
                        ProcessName = name,
                        Display = name + ".exe  (PID " + p.Id.ToString(CultureInfo.InvariantCulture) + ")"
                    });
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            result.Sort(delegate (ProcessTarget a, ProcessTarget b)
            {
                int c = string.Compare(a.ProcessName, b.ProcessName, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : a.ProcessId.CompareTo(b.ProcessId);
            });
        }
        catch { }
        return result;
    }

    private bool IsProcessRunning(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using (Process p = Process.GetProcessById(pid)) return !p.HasExited;
        }
        catch { return false; }
    }

    private void RunShutdownCommand(bool restart)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
                Arguments = restart ? "/r /t 0" : "/s /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch { }
    }

    private void CheckTemporaryAlwaysOnTrigger()
    {
        if (!_temporaryAlwaysOnActive)
        {
            if (_temporaryAlwaysOnTimer != null) _temporaryAlwaysOnTimer.Stop();
            return;
        }

        bool stillActive = _temporaryTrigger == TemporaryAlwaysOnTrigger.Window
            ? (_temporaryWindowHandle != IntPtr.Zero && IsWindow(_temporaryWindowHandle))
            : IsProcessRunning(_temporaryProcessId);

        if (stillActive) return;

        string returnGuid = _temporaryReturnGuid;
        char returnSlot = _temporaryReturnSlot;
        TemporaryEndAction endAction = _temporaryEndAction;
        ClearTemporaryAlwaysOnState();

        switch (endAction)
        {
            case TemporaryEndAction.ReturnToSlot:
                if (!string.IsNullOrEmpty(returnGuid))
                    TrySetActive(returnGuid);
                try
                {
                    tray.ShowBalloonTip(2500, L("Temporary Always On ended", "Terminó Siempre encendido temporal"),
                        L("The trigger ended. Returned to slot " + returnSlot + ".", "El activador terminó. Se volvió a la ranura " + returnSlot + "."), ToolTipIcon.Info);
                }
                catch { }
                break;

            case TemporaryEndAction.Lock:
                try { LockWorkStation(); } catch { }
                break;

            case TemporaryEndAction.Sleep:
                try { SetSuspendState(false, false, false); } catch { }
                break;

            case TemporaryEndAction.Hibernate:
                try { SetSuspendState(true, false, false); } catch { }
                break;

            case TemporaryEndAction.ShutDown:
                RunShutdownCommand(false);
                break;

            case TemporaryEndAction.Restart:
                RunShutdownCommand(true);
                break;

            case TemporaryEndAction.Nothing:
            default:
                try
                {
                    tray.ShowBalloonTip(2000, L("Temporary Always On ended", "Terminó Siempre encendido temporal"),
                        L("The trigger ended. No further action was taken.", "El activador terminó. No se realizó ninguna acción adicional."), ToolTipIcon.Info);
                }
                catch { }
                break;
        }
    }

    private void ClearTemporaryAlwaysOnState()
    {
        _temporaryAlwaysOnActive = false;
        _temporaryWindowHandle = IntPtr.Zero;
        _temporaryProcessId = 0;
        _temporaryReturnGuid = "";
        _temporaryReturnSlot = '\0';
        _temporaryEndAction = TemporaryEndAction.ReturnToSlot;
        _temporaryTriggerDescription = "";
        if (_temporaryAlwaysOnTimer != null) _temporaryAlwaysOnTimer.Stop();
        UpdateTrayIcon();
    }

    private void OnConfigureToggleCycle(object sender, EventArgs e)
    {
        CloseContextMenu();
        EnsureDefaultSlots();

        using (Form f = new Form())
        {
            f.Text = L("Configure Toggle Cycle", "Configurar ciclo de cambio");
            f.StartPosition = FormStartPosition.CenterScreen;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MinimizeBox = false;
            f.MaximizeBox = false;
            f.ShowInTaskbar = false;
            f.ClientSize = new Size(420, Math.Min(520, 180 + slots.Count * 34));

            Label info = new Label
            {
                Left = 12,
                Top = 12,
                Width = 396,
                Height = 42,
                Text = L(
                    "Choose which slots are included when you left-click the tray icon. Slots are cycled in A–Z order.",
                    "Elige qué ranuras se incluyen al hacer clic izquierdo en el icono. Las ranuras se recorren en orden A–Z.")
            };
            f.Controls.Add(info);

            FlowLayoutPanel panel = new FlowLayoutPanel
            {
                Left = 12,
                Top = 62,
                Width = 396,
                Height = f.ClientSize.Height - 122,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(6)
            };
            f.Controls.Add(panel);

            Dictionary<char, CheckBox> checks = new Dictionary<char, CheckBox>();
            foreach (KeyValuePair<char, SlotConfig> kv in slots)
            {
                SlotConfig sc = kv.Value;
                string caption;
                if (IsStandardSlot(kv.Key))
                    caption = kv.Key + ": " + GetStandardIconName(kv.Key);
                else
                    caption = kv.Key + ": " + L("Custom Slot", "Ranura personalizada");

                string planName = FindPlanName(sc.Guid);
                if (!string.IsNullOrEmpty(sc.Guid))
                    caption += " — " + planName;
                else
                    caption += " — " + L("unassigned", "sin asignar");

                CheckBox cb = new CheckBox
                {
                    Text = caption,
                    AutoSize = false,
                    Width = 365,
                    Height = 28,
                    Checked = sc.CycleEnabled,
                    Tag = kv.Key,
                    Margin = new Padding(0, 0, 0, 4)
                };
                checks[kv.Key] = cb;
                panel.Controls.Add(cb);
            }

            Button allAssigned = new Button
            {
                Text = L("All assigned", "Todas las asignadas"),
                Left = 12,
                Top = f.ClientSize.Height - 52,
                Width = 105,
                Height = 30
            };
            allAssigned.Click += delegate
            {
                foreach (KeyValuePair<char, CheckBox> kv in checks)
                {
                    SlotConfig sc;
                    bool assigned = slots.TryGetValue(kv.Key, out sc) && !string.IsNullOrEmpty(sc.Guid);
                    kv.Value.Checked = assigned;
                }
            };
            f.Controls.Add(allAssigned);

            Button clear = new Button
            {
                Text = L("Clear all", "Limpiar todas"),
                Left = 123,
                Top = f.ClientSize.Height - 52,
                Width = 90,
                Height = 30
            };
            clear.Click += delegate
            {
                foreach (CheckBox cb in checks.Values) cb.Checked = false;
            };
            f.Controls.Add(clear);

            Button save = new Button
            {
                Text = L("Save", "Guardar"),
                Left = f.ClientSize.Width - 174,
                Top = f.ClientSize.Height - 52,
                Width = 75,
                Height = 30,
                DialogResult = DialogResult.OK
            };
            f.Controls.Add(save);

            Button cancel = new Button
            {
                Text = L("Cancel", "Cancelar"),
                Left = f.ClientSize.Width - 92,
                Top = f.ClientSize.Height - 52,
                Width = 80,
                Height = 30,
                DialogResult = DialogResult.Cancel
            };
            f.Controls.Add(cancel);

            f.AcceptButton = save;
            f.CancelButton = cancel;

            if (f.ShowDialog() == DialogResult.OK)
            {
                foreach (KeyValuePair<char, CheckBox> kv in checks)
                {
                    SlotConfig sc;
                    if (slots.TryGetValue(kv.Key, out sc))
                        sc.CycleEnabled = kv.Value.Checked;
                }

                SaveConfig();
            }
        }
    }

    private void OnAddSlot(object sender, EventArgs e)
    {
        EnsureDefaultSlots();
        char next = GetNextSlotLetter();
        if (next == '\0')
        {
            tray.ShowBalloonTip(3000, L("Switch Power Plan", "Cambiar plan de energía"), L("No more slots available (A–Z).", "No hay más ranuras disponibles (A–Z)."), ToolTipIcon.Info);
            return;
        }

        slots[next] = new SlotConfig { Key = next };
        CloseContextMenu();
        PromptIconsForSlot(next);
        SaveConfig();
        RebuildMenu();
        UpdateTrayIcon();
    }

    private char GetNextSlotLetter()
    {
        for (char c = 'A'; c <= 'Z'; c++) if (!slots.ContainsKey(c)) return c;
        return '\0';
    }

    private ToolStripMenuItem BuildAssignSubmenuDynamic(char slotKey)
    {
        var sub = new ToolStripMenuItem(SlotMenuTitle(slotKey));
        sub.DropDownOpening += delegate
        {
            sub.DropDownItems.Clear();
            EnsurePlanList();
            foreach (Plan p in plans)
            {
                var item = new ToolStripMenuItem(p.Name + (p.IsActive ? "  (" + L("Active", "Activo") + ")" : ""), null, OnAssignClickDynamic)
                {
                    Tag = new AssignTagDynamic(slotKey, p.Guid),
                    Enabled = !IsPlanAssignedToAnotherSlot(slotKey, p.Guid)
                };

                SlotConfig sc;
                if (slots.TryGetValue(slotKey, out sc) && string.Equals(sc.Guid, p.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    // The plan currently assigned to this slot remains selectable
                    // and checked; only plans assigned to OTHER slots are disabled.
                    item.Enabled = true;
                    item.Checked = true;
                }

                sub.DropDownItems.Add(item);
            }
            sub.DropDownItems.Add(new ToolStripSeparator());

            if (IsStandardSlot(slotKey))
            {
                sub.DropDownItems.Add(new ToolStripMenuItem(L("Icon: ", "Icono: ") + GetStandardIconName(slotKey)) { Enabled = false });
            }
            else
            {
                sub.DropDownItems.Add(new ToolStripMenuItem(L("Set ONE icon (Light or Dark)…", "Configurar UN icono (Claro u Oscuro)…"), null, (s, e) => { CloseContextMenu(); PromptIconsForSlot(slotKey); SaveConfig(); UpdateTrayIcon(); }));
            }

            sub.DropDownItems.Add(new ToolStripMenuItem(L("Clear this slot", "Limpiar esta ranura"), null, (s, e) => { if (!slots.ContainsKey(slotKey)) slots[slotKey] = new SlotConfig { Key = slotKey }; slots[slotKey].Guid = ""; SaveConfig(); UpdateTrayIcon(); }));
            if (slotKey > 'F') sub.DropDownItems.Add(new ToolStripMenuItem(L("Remove this slot", "Eliminar esta ranura"), null, (s, e) => { slots.Remove(slotKey); SaveConfig(); CloseContextMenu(); RebuildMenu(); UpdateTrayIcon(); }));
        };
        WireNoCloseOnItemClick(sub.DropDown);
        return sub;
    }

    private void PromptIconsForSlot(char slotKey)
    {
        if (IsStandardSlot(slotKey)) return;
        EnsureDefaultSlots();
        if (!slots.ContainsKey(slotKey)) slots[slotKey] = new SlotConfig { Key = slotKey };
        SlotConfig s = slots[slotKey];
        string picked = PickIcoFile(L("Pick ONE icon (.ico)", "Elige UN icono (.ico)"));
        if (string.IsNullOrEmpty(picked)) return;

        DialogResult dr = MessageBox.Show(L("Is this the LIGHT icon?\r\n\r\nYes = Light\r\nNo = Dark\r\nCancel = Don't change", "¿Este es el icono CLARO?\r\n\r\nSí = Claro\r\nNo = Oscuro\r\nCancelar = No cambiar"), L("Icon type", "Tipo de icono"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (dr == DialogResult.Cancel) return;
        if (dr == DialogResult.Yes) s.LightIconPath = picked; else s.DarkIconPath = picked;
        slots[slotKey] = s;
    }

    private string PickIcoFile(string title)
    {
        using (var dlg = new OpenFileDialog { Title = title, Filter = "Icon files (*.ico)|*.ico", CheckFileExists = true, Multiselect = false })
            return (dlg.ShowDialog() == DialogResult.OK) ? dlg.FileName : "";
    }

    private ToolStripMenuItem BuildThemeMenu()
    {
        var sub = new ToolStripMenuItem(L("Icon contrast", "Contraste de iconos"));
        sub.DropDownOpening += delegate
        {
            sub.DropDownItems.Clear();
            var iAuto = new ToolStripMenuItem(L("Auto (match system, high contrast)", "Auto (según sistema, alto contraste)"), null, OnThemeAuto) { Checked = (iconSetPref == IconSet.Auto) };
            var iLight = new ToolStripMenuItem(L("Use Light icons", "Usar iconos claros"), null, OnThemeLight) { Checked = (iconSetPref == IconSet.Light) };
            var iDark = new ToolStripMenuItem(L("Use Dark icons", "Usar iconos oscuros"), null, OnThemeDark) { Checked = (iconSetPref == IconSet.Dark) };
            sub.DropDownItems.Add(iAuto); sub.DropDownItems.Add(iLight); sub.DropDownItems.Add(iDark);
        };
        WireNoCloseOnItemClick(sub.DropDown);
        return sub;
    }

    private ToolStripMenuItem BuildSwitchToSubmenu()
    {
        var sub = new ToolStripMenuItem(L("Switch to…", "Cambiar a…"));
        sub.DropDownOpening += delegate
        {
            sub.DropDownItems.Clear();
            EnsureDefaultSlots(); EnsurePlanList();
            int count = 0;
            foreach (var kv in slots)
            {
                if (string.IsNullOrEmpty(kv.Value.Guid)) continue;
                var item = new ToolStripMenuItem(kv.Value.Key + ": " + FindPlanName(kv.Value.Guid), null, OnSwitchToClick) { Tag = kv.Value.Guid };
                if (!string.IsNullOrEmpty(activeGuid) && string.Equals(activeGuid, kv.Value.Guid, StringComparison.OrdinalIgnoreCase)) item.Checked = true;
                sub.DropDownItems.Add(item); count++;
            }
            if (count == 0) sub.DropDownItems.Add(L("(no slots assigned)", "(sin ranuras asignadas)"));
        };
        WireNoCloseOnItemClick(sub.DropDown);
        return sub;
    }

    private ToolStripMenuItem BuildCustomizeButtonsMenu()
    {
        var root = new ToolStripMenuItem(L("Customize (Buttons && Lid) →", "Personalizar (Botones y tapa) →"));
        root.DropDownOpening += delegate
        {
            root.DropDownItems.Clear(); EnsurePlanList();
            foreach (Plan p in plans)
            {
                var planItem = new ToolStripMenuItem(p.Name + (p.IsActive ? "  (" + L("Active", "Activo") + ")" : "")) { Tag = p.Guid };
                Guid scheme = Guid.Parse(p.Guid);
                planItem.DropDownItems.Add(BuildSettingMenu(L("Power button", "Botón de encendido"), scheme, SET_PBUTTON));
                planItem.DropDownItems.Add(BuildSettingMenu(L("Sleep button", "Botón de suspensión"), scheme, SET_SBUTTON));
                planItem.DropDownItems.Add(BuildSettingMenu(L("Closing lid", "Cerrar tapa"), scheme, SET_LID));
                WireNoCloseOnItemClick(planItem.DropDown); root.DropDownItems.Add(planItem);
            }
            if (root.DropDownItems.Count == 0) root.DropDownItems.Add(L("(no power plans found)", "(no se encontraron planes de energía)"));
        };
        WireNoCloseOnItemClick(root.DropDown);
        return root;
    }

    private ToolStripMenuItem BuildSettingMenu(string caption, Guid scheme, Guid setting)
    {
        var settingItem = new ToolStripMenuItem(caption + " →");
        settingItem.DropDownOpening += delegate
        {
            settingItem.DropDownItems.Clear();
            var acMenu = new ToolStripMenuItem(L("On AC →", "Con corriente →")); AddActionChoiceItems(acMenu, scheme, setting, true); WireNoCloseOnItemClick(acMenu.DropDown); settingItem.DropDownItems.Add(acMenu);
            var dcMenu = new ToolStripMenuItem(L("On battery →", "Con batería →")); AddActionChoiceItems(dcMenu, scheme, setting, false); WireNoCloseOnItemClick(dcMenu.DropDown); settingItem.DropDownItems.Add(dcMenu);
        };
        WireNoCloseOnItemClick(settingItem.DropDown);
        return settingItem;
    }

    private void AddActionChoiceItems(ToolStripMenuItem parent, Guid scheme, Guid setting, bool ac)
    {
        parent.DropDownItems.Clear();
        uint current = ReadAction(scheme, setting, ac);
        Action<uint> add = delegate (uint val)
        {
            var mi = new ToolStripMenuItem(ActionName(val)) { Checked = (current == val) };
            mi.Click += delegate { WriteAction(scheme, setting, ac, val); foreach (ToolStripItem tsi in parent.DropDownItems) { ToolStripMenuItem tmi = tsi as ToolStripMenuItem; if (tmi != null) tmi.Checked = (tmi == mi); } };
            parent.DropDownItems.Add(mi);
        };
        add((uint)ButtonLidAction.DoNothing); add((uint)ButtonLidAction.Sleep); add((uint)ButtonLidAction.Hibernate); add((uint)ButtonLidAction.Shutdown);
    }

    private static uint ReadAction(Guid scheme, Guid setting, bool ac)
    {
        try
        {
            uint v; Guid sub = SUB_BUTTONS, set = setting;
            if (ac) { if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v) == 0) return v; }
            else { if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v) == 0) return v; }
        }
        catch { }
        return 0;
    }

    private void WriteAction(Guid scheme, Guid setting, bool ac, uint value)
    {
        try
        {
            Guid sub = SUB_BUTTONS, set = setting;
            if (ac) PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value); else PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, value);
            if (!string.IsNullOrEmpty(activeGuid) && string.Equals(scheme.ToString(), activeGuid, StringComparison.OrdinalIgnoreCase)) { Guid g = scheme; try { PowerSetActiveScheme(IntPtr.Zero, ref g); } catch { } }
        }
        catch { }
    }

    private string ActionName(uint v)
    {
        switch ((ButtonLidAction)v)
        {
            case ButtonLidAction.DoNothing: return L("Do nothing", "No hacer nada");
            case ButtonLidAction.Sleep: return L("Sleep", "Suspender");
            case ButtonLidAction.Hibernate: return L("Hibernate", "Hibernar");
            case ButtonLidAction.Shutdown: return L("Shut down", "Apagar");
            default: return v.ToString();
        }
    }

    private ToolStripMenuItem BuildCustomizeDisplaySleepMenu()
    {
        var root = new ToolStripMenuItem(L("Customize (Display && Sleep) →", "Personalizar (Pantalla y suspensión) →"));
        root.DropDownOpening += delegate
        {
            root.DropDownItems.Clear(); EnsurePlanList();
            foreach (Plan p in plans)
            {
                var planItem = new ToolStripMenuItem(p.Name + (p.IsActive ? "  (" + L("Active", "Activo") + ")" : "")) { Tag = p.Guid };
                Guid scheme = Guid.Parse(p.Guid);
                planItem.DropDownItems.Add(BuildTimeoutMenu(L("Display off timeout", "Tiempo para apagar pantalla"), scheme, SUB_VIDEO, SET_VIDEOIDLE));
                planItem.DropDownItems.Add(BuildTimeoutMenu(L("Console lock display off timeout", "Tiempo de pantalla al bloquear"), scheme, SUB_VIDEO, SET_VIDEOCONLOCK));
                planItem.DropDownItems.Add(new ToolStripSeparator());
                planItem.DropDownItems.Add(BuildTimeoutMenu(L("Sleep after", "Suspender después de"), scheme, SUB_SLEEP, SET_SLEEPIDLE));
                planItem.DropDownItems.Add(BuildTimeoutMenu(L("Hibernate after", "Hibernar después de"), scheme, SUB_SLEEP, SET_HIBERNATEIDLE));
                planItem.DropDownItems.Add(BuildTimeoutMenu(L("Unattended sleep timeout", "Tiempo de suspensión desatendida"), scheme, SUB_SLEEP, SET_UNATTENDSLEEP));
                WireNoCloseOnItemClick(planItem.DropDown); root.DropDownItems.Add(planItem);
            }
            if (root.DropDownItems.Count == 0) root.DropDownItems.Add(L("(no power plans found)", "(no se encontraron planes de energía)"));
        };
        WireNoCloseOnItemClick(root.DropDown);
        return root;
    }

    private ToolStripMenuItem BuildTimeoutMenu(string caption, Guid scheme, Guid subgroup, Guid setting)
    {
        var settingItem = new ToolStripMenuItem(caption + " →");
        settingItem.DropDownOpening += delegate
        {
            settingItem.DropDownItems.Clear();
            var acMenu = new ToolStripMenuItem(L("On AC →", "Con corriente →")); AddTimeoutChoiceItems(acMenu, scheme, subgroup, setting, true); WireNoCloseOnItemClick(acMenu.DropDown); settingItem.DropDownItems.Add(acMenu);
            var dcMenu = new ToolStripMenuItem(L("On battery →", "Con batería →")); AddTimeoutChoiceItems(dcMenu, scheme, subgroup, setting, false); WireNoCloseOnItemClick(dcMenu.DropDown); settingItem.DropDownItems.Add(dcMenu);
        };
        WireNoCloseOnItemClick(settingItem.DropDown);
        return settingItem;
    }

    private void AddTimeoutChoiceItems(ToolStripMenuItem parent, Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        parent.DropDownItems.Clear();
        uint current = ReadTimeoutSeconds(scheme, subgroup, setting, ac);
        int[] mins = { 0, 1, 2, 3, 5, 10, 15, 20, 30, 60, 120 };

        foreach (int m in mins)
        {
            uint secs = (uint)(m * 60);
            string label = m == 0 ? L("Never", "Nunca") : (m < 60 ? m + " " + L("min", "min") : (m / 60 == 1 ? "1 " + L("hour", "hora") : m / 60 + " " + L("hours", "horas")));
            var mi = new ToolStripMenuItem(label) { Tag = secs, Checked = (current == secs) };
            mi.Click += delegate { WriteTimeoutSeconds(scheme, subgroup, setting, ac, secs); foreach (ToolStripItem tsi in parent.DropDownItems) { ToolStripMenuItem tmi = tsi as ToolStripMenuItem; if (tmi != null && tmi.Tag is uint) tmi.Checked = ((uint)tmi.Tag == secs); } };
            parent.DropDownItems.Add(mi);
        }

        parent.DropDownItems.Add(new ToolStripSeparator());
        var customItem = new ToolStripMenuItem(L("Custom…", "Personalizado…"));
        customItem.Click += delegate
        {
            CloseContextMenu();
            uint newSeconds = PromptCustomTimeoutSeconds(current);
            if (newSeconds == current) return;
            WriteTimeoutSeconds(scheme, subgroup, setting, ac, newSeconds);
            foreach (ToolStripItem tsi in parent.DropDownItems) { ToolStripMenuItem tmi = tsi as ToolStripMenuItem; if (tmi != null && tmi.Tag is uint) tmi.Checked = ((uint)tmi.Tag == newSeconds); }
        };
        parent.DropDownItems.Add(customItem);
    }

    private static uint ReadTimeoutSeconds(Guid scheme, Guid subgroup, Guid setting, bool ac)
    {
        try
        {
            uint v; Guid sub = subgroup, set = setting;
            if (ac) { if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v) == 0) return v; }
            else { if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out v) == 0) return v; }
        }
        catch { }
        return 0;
    }

    private void WriteTimeoutSeconds(Guid scheme, Guid subgroup, Guid setting, bool ac, uint seconds)
    {
        try
        {
            Guid sub = subgroup, set = setting;
            if (ac) PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, seconds); else PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, seconds);
            if (!string.IsNullOrEmpty(activeGuid) && string.Equals(scheme.ToString(), activeGuid, StringComparison.OrdinalIgnoreCase)) { Guid g = scheme; try { PowerSetActiveScheme(IntPtr.Zero, ref g); } catch { } }
        }
        catch { }
    }

    private uint PromptCustomTimeoutSeconds(uint currentSeconds)
    {
        uint currentMinutes = currentSeconds / 60;
        using (Form f = new Form { Text = L("Custom timeout", "Tiempo personalizado"), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(320, 130) })
        {
            TextBox tb = new TextBox { Left = 12, Top = 35, Width = 100, Text = currentMinutes.ToString() };
            Button ok = new Button { Text = "OK", Left = 90, Top = 75, Width = 80, DialogResult = DialogResult.None };
            ok.Click += delegate { uint dummy; if (!uint.TryParse(tb.Text.Trim(), out dummy)) { MessageBox.Show(f, L("Invalid number.", "Número inválido.")); return; } f.DialogResult = DialogResult.OK; f.Close(); };
            f.Controls.Add(new Label { Left = 9, Top = 9, Width = 300, Text = L("Enter minutes (0 = Never):", "Ingresa minutos (0 = Nunca):") }); f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(new Button { Text = L("Cancel", "Cancelar"), Left = 180, Top = 75, Width = 80, DialogResult = DialogResult.Cancel });
            f.AcceptButton = ok;
            return f.ShowDialog() == DialogResult.OK ? uint.Parse(tb.Text.Trim()) * 60 : currentSeconds;
        }
    }

    private void ToggleToNextAssigned()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            EnsureDefaultSlots(); EnsurePlanList();
            List<string> cycle = new List<string>();
            foreach (var kv in slots)
            {
                if (kv.Value.CycleEnabled && !string.IsNullOrEmpty(kv.Value.Guid))
                    cycle.Add(kv.Value.Guid);
            }

            if (cycle.Count == 0)
            {
                tray.ShowBalloonTip(3000,
                    L("Switch Power Plan", "Cambiar plan de energía"),
                    L("Select at least one assigned slot in ‘Configure Toggle Cycle…’.", "Selecciona al menos una ranura asignada en ‘Configurar ciclo de cambio…’.") ,
                    ToolTipIcon.Warning);
                return;
            }

            int idx = cycle.FindIndex(g => string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase));
            string target = (idx >= 0 && idx + 1 < cycle.Count) ? cycle[idx + 1] : cycle[0];
            TrySetActive(target);
        }
        catch (Exception ex) { tray.ShowBalloonTip(3000, L("Toggle error", "Error al cambiar"), ex.Message, ToolTipIcon.Error); }
        finally { _busy = false; }
    }

    private bool IsEnergySavingGuid(string guid)
    {
        return string.Equals(
            guid,
            MANAGED_ENERGY_SAVING.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool IsNightGuid(string guid)
    {
        return string.Equals(
            guid,
            MANAGED_MOON.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool SaveCurrentUserPowerModes()
    {
        Guid ac, dc;
        uint rcAc = PowerGetUserConfiguredACPowerMode(out ac);
        uint rcDc = PowerGetUserConfiguredDCPowerMode(out dc);

        if (rcAc != 0 || rcDc != 0)
            return false;

        _savedAcPowerMode = ac;
        _savedDcPowerMode = dc;
        _savedPowerModesForEnergyMode = true;
        return true;
    }

    private bool SetBestEfficiencyPowerMode()
    {
        Guid best = POWER_MODE_BEST_EFFICIENCY;
        uint rcAc = PowerSetUserConfiguredACPowerMode(ref best);
        uint rcDc = PowerSetUserConfiguredDCPowerMode(ref best);

        try
        {
            if (rcAc != 0 || rcDc != 0)
            {
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("s") +
                    "  EnergySaving Power Mode set failed: AC=" + rcAc +
                    " DC=" + rcDc + Environment.NewLine);
                return false;
            }
        }
        catch { }

        return true;
    }

    private void RestorePreviousUserPowerModes()
    {
        if (!_savedPowerModesForEnergyMode)
        {
            // No captured state (for example after a restart while F was active).
            // Restore the normal Windows Balanced mode rather than leaving an
            // old Best Efficiency overlay behind.
            Guid balanced = POWER_MODE_BALANCED;
            try { PowerSetUserConfiguredACPowerMode(ref balanced); } catch { }
            try { PowerSetUserConfiguredDCPowerMode(ref balanced); } catch { }
            return;
        }

        Guid ac = _savedAcPowerMode;
        Guid dc = _savedDcPowerMode;

        try { PowerSetUserConfiguredACPowerMode(ref ac); } catch { }
        try { PowerSetUserConfiguredDCPowerMode(ref dc); } catch { }

        _savedPowerModesForEnergyMode = false;
    }

    private bool IsEnergySaverPolicyEnabled()
    {
        try
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Policies\Microsoft\Power\EnergySaver", false))
            {
                if (key == null) return false;

                object value = key.GetValue("EnableEnergySaver", null);
                if (value == null) return false;

                return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
            }
        }
        catch
        {
            return false;
        }
    }

    private bool RunElevatedEnergyPolicy(bool enable)
    {
        if (Program.IsRunningElevated())
        {
            try
            {
                return Program.ApplyEnergySaverPolicyAsElevated(enable) == 0;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            string verb = enable ? "enable" : "disable";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = "/elevated-energy-policy " + verb,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Application.StartupPath
            };

            // UAC happens in the elevated child. Do not wait here: the tray
            // process must remain responsive while Windows refreshes policy.
            Process.Start(psi);
            return true;
        }
        catch
        {
            // UAC canceled or elevation failed.
            return false;
        }
    }

    private bool WaitForEnergySaverPolicyState(bool enabled, int timeoutMs)
    {
        int started = Environment.TickCount;

        while (Environment.TickCount - started < timeoutMs)
        {
            if (IsEnergySaverPolicyEnabled() == enabled)
                return true;

            Thread.Sleep(100);
        }

        return IsEnergySaverPolicyEnabled() == enabled;
    }

    private void ApplyPendingEnergySaverPolicy(bool enable)
    {
        if (!RunElevatedEnergyPolicy(enable))
        {
            LogSpecialIntegration(
                "EnergySaver",
                "UAC/elevated helper could not be started.");
        }
    }

    private void HandleNightLightForPlan(string guid)
    {
        bool isNight = IsNightGuid(guid ?? "");

        try
        {
            // Night Light is deliberately handled synchronously immediately
            // after the power scheme is activated. This keeps it independent
            // of the slower Energy Saver policy refresh.
            if (isNight)
            {
                bool nightOn;
                if (TryGetNightLightEnabled(out nightOn) && !nightOn)
                {
                    if (TrySetNightLight(true))
                    {
                        _nightLightEnabledByApp = true;
                        try { SaveConfig(); } catch { }
                    }
                }
            }
            else if (_nightLightEnabledByApp)
            {
                if (TrySetNightLight(false))
                {
                    _nightLightEnabledByApp = false;
                    try { SaveConfig(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            LogSpecialIntegration("NightLightTransition", ex.Message);
        }
    }

    private void QueueSpecialModeReconcile()
    {
        lock (_specialModeLock)
        {
            _specialModeRequestPending = true;
            if (_specialModeWorkerRunning)
                return;

            _specialModeWorkerRunning = true;
        }

        ThreadPool.QueueUserWorkItem(delegate { SpecialModeWorker(); });
    }

    private void SpecialModeWorker()
    {
        try
        {
            while (true)
            {
                string guid;

                lock (_specialModeLock)
                {
                    if (!_specialModeRequestPending)
                    {
                        _specialModeWorkerRunning = false;
                        return;
                    }

                    _specialModeRequestPending = false;
                    guid = activeGuid ?? "";
                }

                bool isEnergySaving = IsEnergySavingGuid(guid);

                if (isEnergySaving)
                {
                    if (!_savedPowerModesForEnergyMode)
                        SaveCurrentUserPowerModes();

                    SetBestEfficiencyPowerMode();

                    if (!IsEnergySaverPolicyEnabled())
                        ApplyPendingEnergySaverPolicy(true);
                }
                else
                {
                    if (IsEnergySaverPolicyEnabled())
                        ApplyPendingEnergySaverPolicy(false);

                    if (_savedPowerModesForEnergyMode)
                        RestorePreviousUserPowerModes();
                }

                // The policy operation is intentionally non-blocking now. If
                // the user clicked another mode while it was being dispatched,
                // immediately reconcile to the newest active plan.
                lock (_specialModeLock)
                {
                    if (!_specialModeRequestPending)
                    {
                        _specialModeWorkerRunning = false;
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSpecialIntegration("SpecialModeWorker", ex.Message);
        }
        finally
        {
            bool restart = false;

            lock (_specialModeLock)
            {
                _specialModeWorkerRunning = false;
                if (_specialModeRequestPending)
                {
                    _specialModeWorkerRunning = true;
                    restart = true;
                }
            }

            if (restart)
                ThreadPool.QueueUserWorkItem(delegate { SpecialModeWorker(); });
        }
    }

    private const string NIGHTLIGHT_KEY_PATH =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    private bool TryGetNightLightEnabled(out bool enabled)
    {
        enabled = false;

        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(NIGHTLIGHT_KEY_PATH, false))
            {
                if (key == null) return false;

                byte[] data = key.GetValue("Data", null) as byte[];
                if (data == null || data.Length < 19) return false;

                if (data.Length == 43 && data[18] == 0x15)
                {
                    enabled = true;
                    return true;
                }

                if (data.Length == 41 && data[18] == 0x13)
                {
                    enabled = false;
                    return true;
                }

                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private bool TrySetNightLight(bool enable)
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(NIGHTLIGHT_KEY_PATH, true))
            {
                if (key == null)
                {
                    LogSpecialIntegration("NightLight", "CloudStore key not found.");
                    return false;
                }

                byte[] data = key.GetValue("Data", null) as byte[];
                if (data == null)
                {
                    LogSpecialIntegration("NightLight", "CloudStore Data value not found.");
                    return false;
                }

                bool currentlyEnabled;
                if (data.Length == 43 && data[18] == 0x15)
                    currentlyEnabled = true;
                else if (data.Length == 41 && data[18] == 0x13)
                    currentlyEnabled = false;
                else
                {
                    LogSpecialIntegration(
                        "NightLight",
                        "Unexpected CloudStore state: length=" + data.Length +
                        ", byte18=0x" + (data.Length > 18 ? data[18].ToString("X2") : "??"));
                    return false;
                }

                if (currentlyEnabled == enable)
                    return true;

                byte[] newData;

                if (enable)
                {
                    // Exact OFF -> ON transformation proven by the successful
                    // PowerShell test on this Windows 11 installation.
                    newData = new byte[43];

                    Array.Copy(data, 0, newData, 0, 22);
                    Array.Copy(data, 23, newData, 25, 18);

                    newData[18] = 0x15;
                    newData[23] = 0x10;
                    newData[24] = 0x00;

                    // Same sequence-byte increment used in the PowerShell test.
                    for (int i = 10; i < 15; i++)
                    {
                        if (newData[i] != 0xFF)
                        {
                            newData[i]++;
                            break;
                        }
                    }
                }
                else
                {
                    // Exact ON -> OFF counterpart matching the reverse branch
                    // of the proven PowerShell toggle format.
                    newData = new byte[41];

                    Array.Copy(data, 0, newData, 0, 22);
                    Array.Copy(data, 25, newData, 23, 16);

                    newData[18] = 0x13;

                    // CloudStore sequence-byte update.
                    for (int i = 10; i < 15; i++)
                    {
                        if (newData[i] != 0xFF)
                        {
                            newData[i]++;
                            break;
                        }
                    }
                }

                key.SetValue("Data", newData, RegistryValueKind.Binary);
            }

            NotifyUserProfileSettingsChanged();
            return true;
        }
        catch (Exception ex)
        {
            LogSpecialIntegration("NightLight", ex.Message);
            return false;
        }
    }

    private static void NotifyUserProfileSettingsChanged()
    {
        try
        {
            UIntPtr result;
            SendMessageTimeout(
                new IntPtr(0xffff),
                0x001A,
                UIntPtr.Zero,
                IntPtr.Zero,
                0x0002,
                1000,
                out result);
        }
        catch { }
    }

    private static void LogSpecialIntegration(string where, string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                DateTime.Now.ToString("s") + "  " + where + ": " +
                (message ?? "(none)") + Environment.NewLine);
        }
        catch { }
    }

    private bool TrySetActive(string guid)
    {
        return TrySetActiveInternal(guid, false);
    }

    private bool IsAlwaysOnGuid(string guid)
    {
        return string.Equals(guid, MANAGED_ALWAYS_ON.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private bool TrySetActiveInternal(string guid, bool keepTemporaryAlwaysOn)
    {
        if (string.IsNullOrEmpty(guid) || _blockLaunch || Environment.HasShutdownStarted) return false;

        // A manual power-plan change cancels an active temporary mode without
        // first returning to the old fallback slot. The user explicitly chose
        // the new target, so go directly there.
        if (_temporaryAlwaysOnActive && !keepTemporaryAlwaysOn && !IsAlwaysOnGuid(guid))
            ClearTemporaryAlwaysOnState();

        PreselectIconForGuid(guid);

        Guid g;
        bool activated = false;
        if (Guid.TryParse(guid, out g))
        {
            try
            {
                activated = (PowerSetActiveScheme(IntPtr.Zero, ref g) == 0);
            }
            catch { }

            // Fall back to powercfg when the native API rejects a scheme.
            // This is especially useful for the temporary Always On feature
            // when a test build has encountered a stale/reprovisioned scheme.
            if (!activated)
                activated = (RunPowerCfg(new[] { "/setactive", guid }) == 0);

            if (activated)
            {
                string nowActive = GetActiveSchemeGuid();
                if (!string.Equals(nowActive, guid, StringComparison.OrdinalIgnoreCase))
                    activated = false;
            }
        }

        RefreshPlansAndIcon();

        if (activated)
        {
            // Handle Night Light immediately so it cannot be held up by an
            // Energy Saver policy transition from the previous mode.
            HandleNightLightForPlan(guid);

            // Energy Saver / Power Mode changes remain asynchronous.
            QueueSpecialModeReconcile();

            if (keepTemporaryAlwaysOn && _temporaryAlwaysOnActive)
                UpdateTrayIcon();
        }

        return activated;
    }

    private void PreselectIconForGuid(string guid)
    {
        Icon icon = IconForGuid(guid);
        if (icon != null) { tray.Icon = icon; lastIcon = icon; }
    }

    private void RefreshPlansAndIcon() { EnsurePlanList(); UpdateTrayIcon(); }

    private void EnsurePlanList() { plans = ListPlans(); activeGuid = GetActiveSchemeGuid(); }

    private void UpdateTrayIcon()
    {
        Icon icon = exeIcon;

        if (_temporaryAlwaysOnActive)
        {
            string variant = iconSetPref == IconSet.Auto ? (SystemIsLight() ? "Dark" : "Light") : (iconSetPref == IconSet.Light ? "Light" : "Dark");
            Icon activeIcon;
            if (icons.TryGetValue("BoltActive." + variant, out activeIcon) && activeIcon != null)
                icon = activeIcon;
            else
            {
                Icon fallback;
                if (icons.TryGetValue("Bolt." + variant, out fallback) && fallback != null) icon = fallback;
            }

            string returnName = FindPlanName(_temporaryReturnGuid);
            if (_temporaryEndAction == TemporaryEndAction.ReturnToSlot)
            {
                tray.Text = string.IsNullOrEmpty(returnName)
                    ? L("Always On (temporary)", "Siempre encendido (temporal)")
                    : TrimForTray(L("Always On (temporary) → return " + _temporaryReturnSlot, "Siempre encendido (temporal) → volver a " + _temporaryReturnSlot));
            }
            else
            {
                tray.Text = TrimForTray(L("Always On (temporary) → " + TemporaryEndActionName(_temporaryEndAction), "Siempre encendido (temporal) → " + TemporaryEndActionName(_temporaryEndAction)));
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(activeGuid)) { Icon chosen = IconForGuid(activeGuid); if (chosen != null) icon = chosen; }
            string activeName = FindPlanName(activeGuid != null ? activeGuid : "");
            tray.Text = string.IsNullOrEmpty(activeName) ? L("Switch Power Plan", "Cambiar plan de energía") : TrimForTray(activeName);
        }

        if (icon == null) icon = lastIcon != null ? lastIcon : (exeIcon != null ? exeIcon : SystemIcons.Application);
        tray.Icon = icon; lastIcon = icon;
    }

    private string TemporaryEndActionName(TemporaryEndAction action)
    {
        switch (action)
        {
            case TemporaryEndAction.ReturnToSlot: return L("Return to slot", "Volver a la ranura");
            case TemporaryEndAction.Lock: return L("Lock", "Bloquear");
            case TemporaryEndAction.Sleep: return L("Sleep", "Suspender");
            case TemporaryEndAction.Hibernate: return L("Hibernate", "Hibernar");
            case TemporaryEndAction.ShutDown: return L("Shut down", "Apagar");
            case TemporaryEndAction.Restart: return L("Restart", "Reiniciar");
            case TemporaryEndAction.Nothing: return L("Nothing", "Nada");
            default: return "";
        }
    }

    private Icon CreateInvertedIcon(Icon src, int size)
    {
        if (src == null) return null;
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawIcon(src, new Rectangle(0, 0, size, size));
            }
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                for (int i = 0; i < data.Height * data.Stride; i += 4)
                {
                    ptr[i] = (byte)(255 - ptr[i]);         // Blue
                    ptr[i + 1] = (byte)(255 - ptr[i + 1]); // Green
                    ptr[i + 2] = (byte)(255 - ptr[i + 2]); // Red
                }
            }
            bmp.UnlockBits(data);
            IntPtr hIcon = bmp.GetHicon();
            Icon result = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return result;
        }
    }

    private Icon LoadIconOrGeneratedCounterpart(string desiredPath, string otherPath, int size)
    {
        Icon direct = LoadIconFromFileCached(desiredPath); if (direct != null) return direct;
        if (!string.IsNullOrEmpty(otherPath))
        {
            Icon baseIcon = LoadIconFromFileCached(otherPath); if (baseIcon == null) return null;
            string key = "invert|" + otherPath + "|" + size;
            Icon cached; if (generatedIcons.TryGetValue(key, out cached) && cached != null) return cached;
            Icon inverted = CreateInvertedIcon(baseIcon, size);
            if (inverted != null) generatedIcons[key] = inverted;
            return inverted;
        }
        return null;
    }

    private Icon IconForGuid(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return null;
        EnsureDefaultSlots();
        char? slotKey = null;
        foreach (var kv in slots) if (string.Equals(kv.Value.Guid, guid, StringComparison.OrdinalIgnoreCase)) { slotKey = kv.Key; break; }
        if (slotKey == null) return null;

        string variant = iconSetPref == IconSet.Auto ? (SystemIsLight() ? "Dark" : "Light") : (iconSetPref == IconSet.Light ? "Light" : "Dark");
        if (IsStandardSlot(slotKey.Value))
        {
            string slotName = slotKey.Value == 'A' ? "Desktop" :
                (slotKey.Value == 'B' ? "Laptop" :
                (slotKey.Value == 'C' ? "Bolt" :
                (slotKey.Value == 'D' ? "Moon" :
                (slotKey.Value == 'E' ? "Balanced" : "EnergySave"))));
            Icon embedded; return icons.TryGetValue(slotName + "." + variant, out embedded) ? embedded : null;
        }

        SlotConfig sc = slots[slotKey.Value];
        string desiredPath = variant == "Light" ? sc.LightIconPath : sc.DarkIconPath;
        string otherPath = variant == "Light" ? sc.DarkIconPath : sc.LightIconPath;
        return LoadIconOrGeneratedCounterpart(desiredPath, otherPath, 16);
    }

    private Icon LoadIconFromFileCached(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        Icon cached; if (fileIcons.TryGetValue(path, out cached) && cached != null) return cached;
        try { using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { var ic = new Icon(fs); fileIcons[path] = ic; return ic; } } catch { return null; }
    }

    private static string TrimForTray(string s) { s = (s != null) ? s.Replace("\r", "").Replace("\n", " · ") : ""; return s.Length > 63 ? s.Substring(0, 63) : s; }

    private string FindPlanName(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return ""; EnsurePlanList();
        foreach (Plan p in plans) if (string.Equals(p.Guid, guid, StringComparison.OrdinalIgnoreCase)) return p.Name;
        return guid;
    }

    private static List<Plan> ListPlans()
    {
        var list = new List<Plan>();
        if (_blockLaunch || Environment.HasShutdownStarted) return list;
        string active = GetActiveSchemeGuid();
        uint index = 0, size = (uint)Marshal.SizeOf(typeof(Guid));
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            while (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buf, ref size) != ERROR_NO_MORE_ITEMS)
            {
                Guid g = (Guid)Marshal.PtrToStructure(buf, typeof(Guid));
                string name = ReadFriendlyName(g);
                list.Add(new Plan { Guid = g.ToString(), Name = !string.IsNullOrEmpty(name) ? name : g.ToString(), IsActive = string.Equals(active, g.ToString(), StringComparison.OrdinalIgnoreCase) });
                index++; size = (uint)Marshal.SizeOf(typeof(Guid));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    private static string ReadFriendlyName(Guid scheme)
    {
        uint needed = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref needed) != 0 || needed == 0) return null;
        IntPtr mem = Marshal.AllocHGlobal((int)needed);
        try { return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, mem, ref needed) == 0 ? Marshal.PtrToStringUni(mem) : null; }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private static string GetActiveSchemeGuid()
    {
        try { IntPtr ptr; if (PowerGetActiveScheme(IntPtr.Zero, out ptr) == 0 && ptr != IntPtr.Zero) { Guid g = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid)); LocalFree(ptr); return g.ToString(); } } catch { }
        return "";
    }

    internal static void LogAndShow(string where, Exception ex)
    {
        try { File.AppendAllText(LogPath, string.Format("==== {0} {1} ====\r\n{2}: {3}\r\n{4}\r\n\r\n", DateTime.Now.ToShortDateString(), DateTime.Now.ToLongTimeString(), where, ex != null ? ex.Message : "(null)", ex != null ? ex.ToString() : "(no stack)")); MessageBox.Show((ex != null ? ex.Message : "(no exception)") + "\r\n\r\nLog: " + LogPath, "SwitchPowerTray error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }

    protected override void ExitThreadCore()
    {
        if (_temporaryAlwaysOnActive && !Environment.HasShutdownStarted)
        {
            string returnGuid = _temporaryReturnGuid;
            ClearTemporaryAlwaysOnState();
            if (!string.IsNullOrEmpty(returnGuid))
            {
                try { TrySetActive(returnGuid); } catch { }
            }
        }

        try { if (_temporaryAlwaysOnTimer != null) { _temporaryAlwaysOnTimer.Stop(); _temporaryAlwaysOnTimer.Dispose(); } } catch { }
        BeginShutdown("Context.ExitThreadCore");
        if (powerNotifyHandle != IntPtr.Zero) PowerUnregisterFromEffectivePowerModeNotifications(powerNotifyHandle);
        try { if (endWatcher != null) endWatcher.Dispose(); if (themeWatcher != null) themeWatcher.Dispose(); if (tray != null) { tray.Visible = false; tray.Dispose(); } if (exeIcon != null) exeIcon.Dispose(); } catch { }
        foreach (var kv in icons) if (kv.Value != null) kv.Value.Dispose(); 
        foreach (var kv in fileIcons) if (kv.Value != null) kv.Value.Dispose(); 
        foreach (var kv in generatedIcons) if (kv.Value != null) kv.Value.Dispose();
        icons.Clear(); fileIcons.Clear(); generatedIcons.Clear();
        base.ExitThreadCore();
    }

    private void LoadAllIcons()
    {
        AddIcon("Desktop.Dark", RES_DESKTOP_DARK);
        AddIcon("Desktop.Light", RES_DESKTOP_LIGHT);
        AddIcon("Laptop.Dark", RES_LAPTOP_DARK);
        AddIcon("Laptop.Light", RES_LAPTOP_LIGHT);
        AddIcon("Bolt.Dark", RES_BOLT_DARK);
        AddIcon("Bolt.Light", RES_BOLT_LIGHT);
        AddIcon("BoltActive.Dark", RES_BOLT_ACTIVE_DARK);
        AddIcon("BoltActive.Light", RES_BOLT_ACTIVE_LIGHT);
        AddIcon("Moon.Dark", RES_MOON_DARK);
        AddIcon("Moon.Light", RES_MOON_LIGHT);
        AddIcon("Balanced.Dark", RES_BALANCED_DARK);
        AddIcon("Balanced.Light", RES_BALANCED_LIGHT);
        AddIcon("EnergySave.Dark", RES_ENERGYSAVE_DARK);
        AddIcon("EnergySave.Light", RES_ENERGYSAVE_LIGHT);
    }

    private void AddIcon(string key, string resName) { Icon ic = LoadEmbeddedIcon(resName); if (ic != null) icons[key] = ic; }

    private static Icon LoadEmbeddedIcon(string logicalName) { try { using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)) { if (s != null) return new Icon(s); } } catch { } return null; }

    private bool SystemIsLight() { try { using (RegistryKey p = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) { if (p != null) { object v = p.GetValue("AppsUseLightTheme"); if (v is int) return ((int)v) != 0; } } } catch { } return true; }

    private bool IsRunAtStartupEnabled() { try { using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false)) { return key != null && key.GetValue(AppId) != null; } } catch { return false; } }

    private void ToggleRunAtStartup()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (IsRunAtStartupEnabled()) key.DeleteValue(AppId, false); else key.SetValue(AppId, Application.ExecutablePath);
            }
        }
        catch (Exception ex) { LogAndShow("ToggleRunAtStartup", ex); }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            string oldA = null, oldB = null, oldC = null, oldD = null;
            string cycleSelection = null;
            bool cycleConfigLoaded = false;
            foreach (string line in File.ReadAllLines(ConfigPath))
            {
                string[] kv = line.Split(new[] { '=' }, 2); if (kv.Length != 2) continue;
                string k = kv[0].Trim().ToUpperInvariant(), v = kv[1].Trim();
                if (k == "SLOT")
                {
                    string[] parts = v.Split('|');
                    if (parts.Length >= 1 && parts[0].Length == 1)
                    {
                        char c = char.ToUpperInvariant(parts[0][0]);
                        if (c >= 'A' && c <= 'Z')
                        {
                            SlotConfig sc; if (!slots.TryGetValue(c, out sc)) sc = new SlotConfig { Key = c };
                            if (parts.Length >= 2) sc.Guid = parts[1] ?? ""; if (parts.Length >= 3) sc.LightIconPath = parts[2] ?? ""; if (parts.Length >= 4) sc.DarkIconPath = parts[3] ?? "";
                            if (IsStandardSlot(c)) { sc.LightIconPath = ""; sc.DarkIconPath = ""; }
                            slots[c] = sc;
                        }
                    }
                }
                else if (k == "GUIDA") oldA = v; else if (k == "GUIDB") oldB = v; else if (k == "GUIDC") oldC = v; else if (k == "GUIDD") oldD = v;
                else if (k == "ICONSET") { try { iconSetPref = (IconSet)Enum.Parse(typeof(IconSet), v, true); } catch { } }
                else if (k == "LANG") { languageLoadedFromConfig = true; uiLanguage = (string.Equals(v, "Spanish", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "Español", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "Es", StringComparison.OrdinalIgnoreCase)) ? UiLanguage.Spanish : UiLanguage.English; }
                else if (k == "NIGHTAUTO") { _nightLightEnabledByApp = (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)); }
                else if (k == "CYCLE") { cycleSelection = v ?? ""; cycleConfigLoaded = true; }
            }
            if (slots.Count == 0 && (oldA != null || oldB != null || oldC != null || oldD != null))
            {
                if (oldA != null) slots['A'] = new SlotConfig { Key = 'A', Guid = oldA }; if (oldB != null) slots['B'] = new SlotConfig { Key = 'B', Guid = oldB }; if (oldC != null) slots['C'] = new SlotConfig { Key = 'C', Guid = oldC }; if (oldD != null) slots['D'] = new SlotConfig { Key = 'D', Guid = oldD };
            }

            if (cycleConfigLoaded)
            {
                foreach (KeyValuePair<char, SlotConfig> kv in slots)
                    kv.Value.CycleEnabled = cycleSelection.IndexOf(kv.Key) >= 0;
            }

            foreach (char c in new[] { 'A', 'B', 'C', 'D', 'E', 'F' }) { SlotConfig sc; if (slots.TryGetValue(c, out sc)) { sc.LightIconPath = ""; sc.DarkIconPath = ""; slots[c] = sc; } }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir); EnsureDefaultSlots(); var lines = new List<string>();
            foreach (var kv in slots) { SlotConfig s = kv.Value; lines.Add("SLOT=" + s.Key + "|" + (s.Guid ?? "") + "|" + (IsStandardSlot(s.Key) ? "" : (s.LightIconPath ?? "")) + "|" + (IsStandardSlot(s.Key) ? "" : (s.DarkIconPath ?? ""))); }
            StringBuilder cycle = new StringBuilder();
            foreach (var kv in slots) if (kv.Value.CycleEnabled) cycle.Append(kv.Key);
            lines.Add("CYCLE=" + cycle.ToString());
            lines.Add("ICONSET=" + iconSetPref.ToString()); lines.Add("LANG=" + uiLanguage.ToString()); lines.Add("NIGHTAUTO=" + (_nightLightEnabledByApp ? "1" : "0"));
            File.WriteAllLines(ConfigPath, lines.ToArray());
        }
        catch { }
    }
}
