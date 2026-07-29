@echo off
setlocal
set "ROOT=%~dp0"
"%ROOT%runtime\dotnet\dotnet.exe" "%ROOT%FullSpectrum.Observer.Host.Cli.dll" %*
exit /b %ERRORLEVEL%
