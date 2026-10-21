@echo off
rem A dry run of the ClassScribe worktree this file is in: double-click it in Explorer, so that the window and its
rem recorder start outside any agent's or editor's job. Options may follow, as for Start-DryRun.ps1 (see README.md).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-DryRun.ps1" %*
echo.
pause
