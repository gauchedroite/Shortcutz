# Development

Instructions for building and running the project:

After code changes, always rebuild and restart with the command below. Stop the running app first, even if it is open; do not substitute a separate-output build or leave the old app running.

To rebuild and restart:

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1"
dotnet build
cmd //c "start bin/Debug/net10.0-windows/Shortcutz.exe"
```

For quick restart only (no build):
```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1 & start bin/Debug/net10.0-windows/Shortcutz.exe"
```
