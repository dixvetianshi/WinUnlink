@echo off
setlocal
set "restore_output_dir=%~dp0..\artifacts\temporary-menu"
if not exist "%restore_output_dir%" mkdir "%restore_output_dir%"
set "restore_report=%restore_output_dir%\remove-%RANDOM%-%RANDOM%.json"
start "" /wait "%~dp0..\artifacts\bin\WinUnlink.exe" --uninstall-menu --yes --report "%restore_report%"
if errorlevel 1 (
    echo Menu removal did not complete. See the report:
    echo %restore_report%
    pause
    exit /b 1
)
echo WinUnlink menu removed. The original menu state is restored.
echo %restore_report%
timeout /t 2 /nobreak >nul
exit /b 0
