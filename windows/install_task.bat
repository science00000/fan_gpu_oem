@echo off
rem Install Mi50FanWin to run at logon with administrator rights.
rem Run this once from an elevated command prompt (or just double-click; UAC will ask).
set EXE=%~dp0Mi50FanWin.exe
schtasks /create /tn Mi50FanWin /tr "\"%EXE%\"" /sc onlogon /rl highest /f
if %errorlevel%==0 (
  schtasks /run /tn Mi50FanWin
  echo Mi50FanWin installed and started. Remove later with:  schtasks /delete /tn Mi50FanWin /f
) else (
  echo Failed to create task. Open this file from an Administrator command prompt.
)
pause
