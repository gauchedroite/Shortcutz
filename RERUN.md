# Rerun Shortcutz

Execute these commands when asked to rerun:

```cmd
taskkill /F /IM Shortcutz.exe >nul 2>&1
start bin/Debug/net10.0-windows/Shortcutz.exe
```

From git bash, run them as a single command:

```bash
cmd //c "taskkill /F /IM Shortcutz.exe >nul 2>&1 && start bin/Debug/net10.0-windows/Shortcutz.exe"
```
