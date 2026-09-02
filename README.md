SwitchPowerTray v12 test

Based on the fully tested v11 build.

New Temporary Always On feature:
- While a selected window remains open or a selected process is running, the app keeps the system on the Always On power plan.
- The temporary tray icon uses Bolt_active_Dark.ico / Bolt_active_Light.ico.
- When the trigger ends, choose one action:
  * Return to selected slot
  * Lock
  * Sleep
  * Hibernate
  * Shut down
  * Restart
  * Nothing
- "Return to selected slot" shows the existing assigned slots and displays them as "A: Plan Name".
- "Nothing" ends the temporary monitoring mode and leaves the current Always On plan active.

Build:
Run Build.bat in the same folder as all required .ico files.
