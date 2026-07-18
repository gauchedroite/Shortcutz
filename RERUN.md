# Rerun Shortcutz

## Run attached (blocks the terminal)

```cmd
dotnet run --project Shortcutz.csproj
```

This keeps the WinForms message loop tied to pi; use Ctrl+C or close the window to stop it.

## Run detached from the terminal (recommended)

Create `run_shortcutz.bat` in the project root:

```bat
@echo off
start "" "%~dp0bin\Debug\net10.0-windows\Shortcutz.exe"
```

Then launch it:

```cmd
cmd //c run_shortcutz.bat
```

The app opens in its own process and the pi prompt returns immediately.

## Notes

- `Shortcutz.exe` is built to `bin\Debug\net10.0-windows\Shortcutz.exe` after `dotnet build`.
- Replace `Debug` with `Release` if you publish a release build.
