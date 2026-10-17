# Bug-fix verification

Do not claim a bug is fixed based only on code inspection, a successful build, or the app starting. Verify the original failure path after the change. If it cannot be reproduced or exercised, state exactly what remains unverified and do not claim the fix is confirmed. Report build, startup, and failure-path results separately.

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
