@echo off
setlocal
rem F1: one-click, discoverable entry. Equivalent to `observer.cmd serve`.
rem Launches the official Web console via the official launcher; the Launcher binds
rem 127.0.0.1 only (loopback) and opens the local browser automatically.
call "%~dp0observer.cmd" serve
exit /b %ERRORLEVEL%
