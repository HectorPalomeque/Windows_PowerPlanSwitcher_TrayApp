# Power Plan Switcher Tray App

A lightweight Windows system-tray utility for quickly switching between Windows power plans, especially useful on laptops, desktops, and docking-station setups.

In addition to switching plans, the app provides a compact interface for editing selected **per-power-plan button, lid, display, and sleep settings** directly through the Windows Power Management API.

The app is portable, has no installer, and provides an English / Español interface with theme-aware tray icons.

---

## ⭐ Features

### Power-plan switching

- Runs quietly in the **Windows system tray**.
- **Left-click** the tray icon to cycle through the configured power-plan slots.
- **Right-click** the tray icon to open the full configuration menu.
- The tray icon follows the currently active power plan when that plan has been assigned to a slot.
- The tray tooltip shows the active plan name.
- Power plans are enumerated directly through **`powrprof.dll`** rather than by calling `powercfg.exe`.
- Changes made outside the app are detected through Windows power-mode notifications so the tray state can refresh automatically.

### Power-plan slots

The app starts with four standard slots:

| Slot | Default icon |
|---|---|
| **A** | Desktop |
| **B** | Laptop |
| **C** | Bolt |
| **D** | Moon |

Slots are assignable independently to any power plan detected by Windows.

The slot system can be expanded beyond the four standard slots: **Add Slot (next letter)…** can create additional slots up to **Z**.

For each slot you can:

- Assign any available Windows power plan.
- Clear the current assignment.
- For additional slots beyond A–D, assign a custom icon.
- Remove additional slots when they are no longer needed.

The **Toggle now** command uses the same assigned-slot cycle as a left-click on the tray icon.

The **Switch to…** submenu provides direct access to the currently assigned slots without cycling through them.

---

## 🎨 Icons and Theme Handling

The four standard slots use embedded light/dark icon variants:

- Desktop
- Laptop
- Bolt
- Moon

The tray icon has three contrast modes:

- **Auto** — follows the Windows application theme and selects the higher-contrast icon variant.
- **Use Light icons** — force the light icon set.
- **Use Dark icons** — force the dark icon set.

In Auto mode:

- Windows light mode → dark icons
- Windows dark mode → light icons

Additional slots (**E–Z**) support custom `.ico` files. A custom slot can be configured with one light or dark icon; when only one variant is available, the app can generate the missing counterpart by inverting the supplied icon.

The tray icon is also refreshed when Windows changes its theme. The application contains handling for the `TaskbarCreated` notification so the tray icon can be restored after the Windows shell/taskbar is recreated.

---

## 🎛 Buttons & Lid Configuration

Under **Customize (Buttons & Lid)**, each detected power plan gets its own configuration branch.

The following settings are available per plan:

- **Power button**
- **Sleep button**
- **Closing lid**

Each setting can be configured independently for:

- **On AC**
- **On battery**

Available actions:

- **Do nothing**
- **Sleep**
- **Hibernate**
- **Shut down**

The app reads and writes these values through the Windows `PowrProf` API for the selected power-plan scheme.

---

## 🌙 Display & Sleep Configuration

Under **Customize (Display & Sleep)**, each detected power plan exposes these settings:

- **Display off timeout**
- **Console lock display off timeout**
- **Sleep after**
- **Hibernate after**
- **Unattended sleep timeout**

Each setting is independently configurable for:

- **On AC**
- **On battery**

### Presets

The timeout menu provides these preset values:

- **Never (0)**
- 1 minute
- 2 minutes
- 3 minutes
- 5 minutes
- 10 minutes
- 15 minutes
- 20 minutes
- 30 minutes
- 1 hour
- 2 hours

### Custom timeout

**Custom…** lets you enter a timeout in minutes. Enter **0** for **Never**.

Changes are written to the selected power-plan scheme immediately.

---

## 🌐 Language Support

The user interface can be switched at any time between:

- **English**
- **Español**

The selected language is saved and restored on the next launch.

If no language preference has previously been saved, the app uses the installed Windows UI language to choose between English and Spanish.

---

## 🚀 Run at Startup

The app has a built-in **Run at Startup** toggle in the tray menu.

The menu item includes a checkmark showing whether startup is currently enabled.

When enabled, the app registers itself under the current user's Windows startup registry key:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
```

The registry value name is:

```text
SwitchPowerTray
```

This is a **per-user** startup setting and does not require putting the executable into a Startup folder manually.

To change it:

1. Right-click the tray icon.
2. Click **Run at Startup**.
3. The checkmark indicates the current state.

---

## ⚙️ Open Windows Power Options

The tray menu includes **Open Power Options…**, which opens the classic Windows Power Options control panel (`powercfg.cpl`).

This provides a convenient way to access the complete set of Windows power settings outside the subset exposed directly by this app.

---

## 🖱 Menu Interaction

The tray menu and its nested submenus use normal Windows Forms menu behavior.

- Clicking outside an open menu closes the entire menu chain.
- **Esc** closes the most recently opened submenu and continues back through the submenu hierarchy one level at a time.
- Dialog-based commands close the menu before displaying the dialog.
- The custom timeout dialog does not leave the previous menu chain stuck open when cancelled.
- Language and slot operations that rebuild the menu close the old menu before rebuilding it.

---

## 💾 Configuration and Persistence

User configuration is stored at:

```text
%APPDATA%\SwitchPowerTray\config.txt
```

The configuration stores:

- Assigned slot letters and power-plan GUIDs.
- Custom light/dark icon paths for additional slots.
- Icon contrast preference (`Auto`, `Light`, or `Dark`).
- Selected language.

The application also maintains a temporary diagnostic/error log at:

```text
%TEMP%\SwitchPowerTray.log
```

The log is rotated when it grows beyond approximately 1 MB.

---

## 🛡 Runtime and Windows integration

The application includes several safeguards and Windows-integration behaviors:

- Waits briefly for the Windows Explorer/taskbar environment to be ready at startup.
- Uses a single-instance mutex so launching the program a second time does not start another copy.
- Registers for Windows effective power-mode notifications and refreshes the active scheme/tray state when notified.
- Handles Explorer/taskbar recreation so the tray icon can be re-established.
- Handles Windows session-ending events and performs orderly shutdown cleanup.
- Cleans up native icon resources and power-mode notification registrations on exit.

---

## 🧩 Technology

- **C#**
- **.NET Framework 4.x / `csc.exe`**
- Windows Forms (`System.Windows.Forms`)
- GDI+/System.Drawing for icon handling
- Native Windows **Power Profile / PowrProf** APIs

The source communicates directly with the Windows power-management APIs for plan enumeration, activation, and the supported per-plan settings.

---

## 🛠 Build

No Visual Studio project is required.

The repository includes `Build.bat`, which locates the .NET Framework C# compiler and builds the application from the source and embedded icons.

### Required files

Keep these files together:

```text
SwitchPowerTray.cs
Build.bat
appicon.ico
Desktop_Dark.ico
Desktop_Light.ico
Laptop_Dark.ico
Laptop_Light.ico
Bolt_Dark.ico
Bolt_Light.ico
Moon_Dark.ico
Moon_Light.ico
```

Then run:

```text
Build.bat
```

The resulting executable is:

```text
SwitchPowerTray.exe
```

The program is designed to run portably from its folder; user settings are stored separately under `%APPDATA%`.

---

## 📁 Repository Structure

```text
Windows_PowerPlanSwitcher_TrayApp/
│
├── SwitchPowerTray.cs
├── Build.bat
├── appicon.ico
├── Desktop_Dark.ico
├── Desktop_Light.ico
├── Laptop_Dark.ico
├── Laptop_Light.ico
├── Bolt_Dark.ico
├── Bolt_Light.ico
├── Moon_Dark.ico
├── Moon_Light.ico
├── README.md
└── LICENSE
```

---

## 🪟 Windows Compatibility

The application is intended for Windows systems that provide the classic Windows Power Profile (`PowrProf`) APIs and Windows Forms. It is designed for **Windows 10 and Windows 11**.

The exact power-plan options exposed by Windows can depend on the hardware, firmware, and available power schemes on the machine.

---

## 📄 License

This project is licensed under the **MIT License**.

See the included `LICENSE` file for the complete license text.

---

## 🙌 Contributions & Feedback

Issues, improvements, translations, and feature requests are welcome.
