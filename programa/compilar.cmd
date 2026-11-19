@echo off
rem Compila Forever Tracker para Windows con el compilador de C# que trae Windows (.NET Framework 4.8): no hace falta
rem instalar nada. Deja salida\ForeverTracker.exe. Uso: compilar.cmd (desde esta carpeta o desde cualquier otra).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist salida mkdir salida
"%CSC%" /nologo /target:winexe /optimize+ /utf8output /codepage:65001 /out:salida\ForeverTracker.exe ^
  /win32manifest:app.manifest /win32icon:icono.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Net.Http.dll /r:System.Security.dll ^
  /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\*.cs
if errorlevel 1 exit /b 1
echo Compilado: %~dp0salida\ForeverTracker.exe
