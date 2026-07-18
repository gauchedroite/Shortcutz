# Rerun Shortcutz

To launch the app:

```cmd
taskkill /F /IM Shortcutz.exe >nul 2>&1
start bin/Debug/net10.0-windows/Shortcutz.exe
```

The app opens in its own process and the pi prompt returns immediately.

## Notes

- `Shortcutz.exe` is built to `bin\Debug\net10.0-windows\Shortcutz.exe` after `dotnet build`.
- Replace `Debug` with `Release` if you publish a release build.
