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
using System.Text;
using System.Threading;
using System.Windows.Forms;
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
        bool createdNew;
        using (Mutex mutex = new Mutex(true, "Global\\SwitchPowerTray_SingleInstanceMutex", out createdNew))
        {
            if (!createdNew)
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ManageLogFile();
            SystemEvents.SessionEnding += (s, e) => TrayContext.BeginShutdown("Program.SessionEnding");
            SystemEvents.SessionEnded += (s, e) => TrayContext.BeginShutdown("Program.SessionEnded");
            AppDomain.CurrentDomain.ProcessExit += (s, e) => TrayContext.BeginShutdown("Program.ProcessExit");
            Application.ThreadException += (s, e) => TrayContext.LogAndShow("ThreadException", e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => TrayContext.LogAndShow("UnhandledException", e.ExceptionObject as Exception);
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
    private const string RES_MOON_DARK = "Icon.Moon.Dark.ico";
    private const string RES_MOON_LIGHT = "Icon.Moon.Light.ico";

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
    }
    private readonly SortedDictionary<char, SlotConfig> slots = new SortedDictionary<char, SlotConfig>();
    private Icon exeIcon, lastIcon;
    private readonly Dictionary<string, Icon> icons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> fileIcons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Icon> generatedIcons = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);
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
    private IntPtr powerNotifyHandle = IntPtr.Zero;
    private EffectivePowerModeCallback _powerModeCallback;
    private readonly Control _syncControl;

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
        _syncControl = new Control();
        IntPtr forceHandle = _syncControl.Handle;
        _powerModeCallback = delegate (int mode, IntPtr ctx)
        {
            try
            {
                _syncControl.BeginInvoke(new Action(delegate ()
                {
                    activeGuid = GetActiveSchemeGuid();
                    RefreshPlansAndIcon();
                }));
            }
            catch { }
        };
        PowerRegisterForEffectivePowerModeNotifications(1, _powerModeCallback, IntPtr.Zero, out powerNotifyHandle);
        for (int i = 0; i < 5; i++)
        {
            RefreshPlansAndIcon();
            if (!string.IsNullOrEmpty(activeGuid)) break;
            Thread.Sleep(250);
        }
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

    private void OnTrayMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ToggleToNextAssigned();
    }

    private void OnAssignClickDynamic(object sender, EventArgs e)
    {
        ToolStripMenuItem mi = sender as ToolStripMenuItem;
        if (mi == null || mi.Tag == null) return;
        AssignTagDynamic t = (AssignTagDynamic)mi.Tag;
        EnsureDefaultSlots();
        if (!slots.ContainsKey(t.SlotKey)) slots[t.SlotKey] = new SlotConfig { Key = t.SlotKey };
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
        // Kept for source compatibility only. Normal WinForms menu closing is intentional.
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

    private bool IsStandardSlot(char slotKey) { return slotKey >= 'A' && slotKey <= 'D'; }

    private string GetStandardIconName(char slotKey)
    {
        switch (slotKey)
        {
            case 'A': return L("Desktop Icon", "Icono de escritorio");
            case 'B': return L("Laptop Icon", "Icono de laptop");
            case 'C': return L("Bolt Icon", "Icono de rayo");
            case 'D': return L("Moon Icon", "Icono de luna");
            default: return "";
        }
    }

    private string SlotMenuTitle(char slotKey)
    {
        if (IsStandardSlot(slotKey)) return L("Assign Slot " + slotKey + " (" + GetStandardIconName(slotKey) + ") →", "Asignar ranura " + slotKey + " (" + GetStandardIconName(slotKey) + ") →");
        return L("Assign Slot " + slotKey + " →", "Asignar ranura " + slotKey + " →");
    }

    private ContextMenuStrip BuildMenu()
    {
        EnsureDefaultSlots();
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(L("Toggle now", "Cambiar ahora"), null, (EventHandler)OnToggleNow));
        foreach (KeyValuePair<char, SlotConfig> kv in slots) menu.Items.Add(BuildAssignSubmenuDynamic(kv.Key));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L("Add Slot (next letter)…", "Agregar ranura (siguiente letra)…"), null, OnAddSlot));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildSwitchToSubmenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildThemeMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildCustomizeButtonsMenu());
        menu.Items.Add(BuildCustomizeDisplaySleepMenu());
        menu.Items.Add(new ToolStripSeparator());
        var startupItem = new ToolStripMenuItem(L("Run at Startup", "Ejecutar al iniciar Windows"));
        menu.Opening += (s, e) => { startupItem.Checked = IsRunAtStartupEnabled(); };
        startupItem.Click += (s, e) => { ToggleRunAtStartup(); };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripMenuItem(L("Open Power Options…", "Abrir opciones de energía…"), null, OnOpenPowerOptions));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L("Exit (Close Program)", "Salir (Cerrar Programa)"), null, OnExit));
        menu.Opening += OnContextMenuOpening;
        menu.Closed += OnContextMenuClosed;
        return menu;
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
                var item = new ToolStripMenuItem(p.Name + (p.IsActive ? "  (" + L("Active", "Activo") + ")" : ""), null, OnAssignClickDynamic) { Tag = new AssignTagDynamic(slotKey, p.Guid) };
                SlotConfig sc;
                if (slots.TryGetValue(slotKey, out sc) && string.Equals(sc.Guid, p.Guid, StringComparison.OrdinalIgnoreCase)) item.Checked = true;
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
            if (slotKey > 'D') sub.DropDownItems.Add(new ToolStripMenuItem(L("Remove this slot", "Eliminar esta ranura"), null, (s, e) => { slots.Remove(slotKey); SaveConfig(); CloseContextMenu(); RebuildMenu(); UpdateTrayIcon(); }));
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
            var mi = new ToolStripMenuItem(ActionName(val)) { Tag = val, Checked = (current == val) };
            mi.Click += delegate
            {
                WriteAction(scheme, setting, ac, val);
                uint actual = ReadAction(scheme, setting, ac);
                foreach (ToolStripItem tsi in parent.DropDownItems)
                {
                    ToolStripMenuItem tmi = tsi as ToolStripMenuItem;
                    if (tmi != null && tmi.Tag is uint) tmi.Checked = ((uint)tmi.Tag == actual);
                }
            };
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
            mi.Click += delegate
            {
                WriteTimeoutSeconds(scheme, subgroup, setting, ac, secs);
                uint actual = ReadTimeoutSeconds(scheme, subgroup, setting, ac);
                foreach (ToolStripItem tsi in parent.DropDownItems)
                {
                    ToolStripMenuItem tmi = tsi as ToolStripMenuItem;
                    if (tmi != null && tmi.Tag is uint) tmi.Checked = ((uint)tmi.Tag == actual);
                }
            };
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
            uint actual = ReadTimeoutSeconds(scheme, subgroup, setting, ac);
            foreach (ToolStripItem tsi in parent.DropDownItems)
            {
                ToolStripMenuItem tmi = tsi as ToolStripMenuItem;
                if (tmi != null && tmi.Tag is uint) tmi.Checked = ((uint)tmi.Tag == actual);
            }
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
            foreach (var kv in slots) if (!string.IsNullOrEmpty(kv.Value.Guid)) cycle.Add(kv.Value.Guid);
            if (cycle.Count == 0)
            {
                tray.ShowBalloonTip(3000, L("Switch Power Plan", "Cambiar plan de energía"), L("Assign at least one slot in the tray menu first.", "Asigna al menos una ranura en el menú primero."), ToolTipIcon.Warning);
                return;
            }
            int idx = cycle.FindIndex(g => string.Equals(g, activeGuid, StringComparison.OrdinalIgnoreCase));
            string target = (idx >= 0 && idx + 1 < cycle.Count) ? cycle[idx + 1] : cycle[0];
            TrySetActive(target);
        }
        catch (Exception ex) { tray.ShowBalloonTip(3000, L("Toggle error", "Error al cambiar"), ex.Message, ToolTipIcon.Error); }
        finally { _busy = false; }
    }

    private void TrySetActive(string guid)
    {
        if (string.IsNullOrEmpty(guid) || _blockLaunch || Environment.HasShutdownStarted) return;
        PreselectIconForGuid(guid);
        Guid g; if (Guid.TryParse(guid, out g)) { try { PowerSetActiveScheme(IntPtr.Zero, ref g); } catch { } }
        RefreshPlansAndIcon();
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
        if (!string.IsNullOrEmpty(activeGuid)) { Icon chosen = IconForGuid(activeGuid); if (chosen != null) icon = chosen; }
        if (icon == null) icon = lastIcon != null ? lastIcon : (exeIcon != null ? exeIcon : SystemIcons.Application);
        tray.Icon = icon; lastIcon = icon;
        string activeName = FindPlanName(activeGuid != null ? activeGuid : "");
        tray.Text = string.IsNullOrEmpty(activeName) ? L("Switch Power Plan", "Cambiar plan de energía") : TrimForTray(activeName);
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
                    ptr[i] = (byte)(255 - ptr[i]);
                    ptr[i + 1] = (byte)(255 - ptr[i + 1]);
                    ptr[i + 2] = (byte)(255 - ptr[i + 2]);
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
            string slotName = slotKey.Value == 'A' ? "Desktop" : (slotKey.Value == 'B' ? "Laptop" : (slotKey.Value == 'C' ? "Bolt" : "Moon"));
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
        BeginShutdown("Context.ExitThreadCore");
        if (powerNotifyHandle != IntPtr.Zero) PowerUnregisterFromEffectivePowerModeNotifications(powerNotifyHandle);
        try { if (endWatcher != null) endWatcher.Dispose(); if (themeWatcher != null) themeWatcher.Dispose(); if (tray != null) { tray.Visible = false; tray.Dispose(); } if (exeIcon != null) exeIcon.Dispose(); } catch { }
        foreach (var kv in icons) if (kv.Value != null) kv.Value.Dispose();
        foreach (var kv in fileIcons) if (kv.Value != null) kv.Value.Dispose();
        foreach (var kv in generatedIcons) if (kv.Value != null) kv.Value.Dispose();
        icons.Clear(); fileIcons.Clear(); generatedIcons.Clear();
        base.ExitThreadCore();
    }

    private void LoadAllIcons() { AddIcon("Desktop.Dark", RES_DESKTOP_DARK); AddIcon("Desktop.Light", RES_DESKTOP_LIGHT); AddIcon("Laptop.Dark", RES_LAPTOP_DARK); AddIcon("Laptop.Light", RES_LAPTOP_LIGHT); AddIcon("Bolt.Dark", RES_BOLT_DARK); AddIcon("Bolt.Light", RES_BOLT_LIGHT); AddIcon("Moon.Dark", RES_MOON_DARK); AddIcon("Moon.Light", RES_MOON_LIGHT); }
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
            }
            if (slots.Count == 0 && (oldA != null || oldB != null || oldC != null || oldD != null))
            {
                if (oldA != null) slots['A'] = new SlotConfig { Key = 'A', Guid = oldA }; if (oldB != null) slots['B'] = new SlotConfig { Key = 'B', Guid = oldB }; if (oldC != null) slots['C'] = new SlotConfig { Key = 'C', Guid = oldC }; if (oldD != null) slots['D'] = new SlotConfig { Key = 'D', Guid = oldD };
            }
            foreach (char c in new[] { 'A', 'B', 'C', 'D' }) { SlotConfig sc; if (slots.TryGetValue(c, out sc)) { sc.LightIconPath = ""; sc.DarkIconPath = ""; slots[c] = sc; } }
        }
        catch { }
    }
    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir); EnsureDefaultSlots(); var lines = new List<string>();
            foreach (var kv in slots) { SlotConfig s = kv.Value; lines.Add("SLOT=" + s.Key + "|" + (s.Guid ?? "") + "|" + (IsStandardSlot(s.Key) ? "" : (s.LightIconPath ?? "")) + "|" + (IsStandardSlot(s.Key) ? "" : (s.DarkIconPath ?? ""))); }
            lines.Add("ICONSET=" + iconSetPref.ToString()); lines.Add("LANG=" + uiLanguage.ToString());
            File.WriteAllLines(ConfigPath, lines.ToArray());
        }
        catch { }
    }
}
