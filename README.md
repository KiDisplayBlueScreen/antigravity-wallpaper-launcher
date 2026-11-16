# Antigravity Wallpaper Launcher

A Windows launcher that adds a configurable wallpaper to the Antigravity desktop app without modifying installed application files.

## Quick start

Requires Windows, the .NET Framework C# compiler, and Antigravity. The current adapter was tested with Antigravity 2.32.1.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.\AntigravityWallpaperLauncher.exe
```

Choose your own wallpaper and confirm the application path, then click **Save & Apply** or **Start Antigravity**. No Node.js, Python or Cindy installation is required. No wallpaper or personal settings are bundled with the source.

The build script extracts the application icon from your locally installed Antigravity. For a custom installation, use `-AntigravityPath 'D:\Apps\Antigravity\Antigravity.exe'` when running `build.ps1`.

## Settings

1. **Wallpaper file**: choose a JPG, PNG, WebP, GIF or BMP image, or enter its full path.
2. **Wallpaper brightness**: 0–150%. 100% preserves the original brightness.
3. **Sidebar transparency**: 0–100%. Higher values reveal more of the wallpaper.
4. **Text mask strength**: 0–100%. Higher values add a stronger background behind messages for readability. 100% is opaque; 0% disables the message mask. It does not blur text or change wallpaper brightness.
5. Click **Start Antigravity** to launch the application, or **Save & Apply** to update an already running instance.

Existing configuration is preserved when upgrading. First-time defaults are brightness 76%, sidebar transparency 58%, and text mask strength 65%. Settings are created locally when you save them.

The launcher automatically removes the dark bottom fade above the conversation input while the wallpaper is enabled. Message masks, the input background, and other gradients remain available.

**Minimize to tray** hides the settings window and shows the Windows tray icon. Double-click that icon to reopen settings.

The window's **Close (X)** button hides the settings window and removes the tray icon, while the launcher continues running in the background. Run the EXE again to reopen the existing settings window; it reuses the same process. Opening settings also removes the tray icon until you choose **Minimize to tray** again.

The launcher reapplies the wallpaper after page reloads in either background mode. It does not restart Antigravity after you close the application.

**Restore default** removes the injected styling and disables automatic application without clearing your saved wallpaper preferences. Click **Save & Apply** to enable the wallpaper again.

**Exit** stops the launcher completely. It is available in settings and in the tray menu. Styling already applied stays until the Antigravity page reloads or closes. To remove styling immediately, choose **Restore default** before exiting.

## Files

- `AntigravityWallpaperLauncher.exe`: standalone launcher using the local Windows .NET Framework. Node.js, Python and Cindy are not required.
- `settings.json`: wallpaper path, application path, brightness, sidebar transparency and text mask strength.
- Your selected wallpaper: read directly from its existing path without recompression; not distributed with this repository.
- Optional shortcut: create a Windows shortcut to the EXE with the `--start` argument to start Antigravity using the saved settings.
- `antigravity.ico`: generated at build time from the locally installed Antigravity executable. Not distributed with the source. The EXE, window and tray use this icon.
- `Launcher.cs`, `launcher.manifest`, `build.ps1`, `extract-icon.ps1`: source and build files, not needed at runtime.

Wallpapers stored next to the launcher are saved as relative paths. Other images use absolute paths. Keep the EXE, settings file and selected image available, and keep the launcher directory writable.

The launcher uses Antigravity's existing loopback debugging connection. It does not modify installed application files, accounts, projects or conversation history, and does not contact external services. It currently targets the locally installed Antigravity 2.32.1 interface. Changes to the official interface or debugging connection may require an update.

Theme colors come from Antigravity's own variables. Original image bytes are preserved, and the wallpaper is displayed with proportional cover scaling. The current Antigravity dark theme and the launcher interface have been visually checked; the Antigravity light theme has not been visually checked.

## Build

Exit the running launcher from its tray menu first, then run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

To refresh the icon from the installed application, run `extract-icon.ps1` before building.

The maintenance option `--apply-once` only connects to an already running Antigravity and writes `apply-result.json`. It does not launch the application. Normal usage requires only the EXE or shortcut.
