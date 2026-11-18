@echo off
rem Monta el paquete de la Store (MSIX) sin firmar: salida\ForeverTracker.msix (la Store lo firma al publicarlo) y la
rem carpeta salida\paquete (para probarlo con el modo de desarrollador: Add-AppxPackage -Register salida\paquete\AppxManifest.xml).
rem Usa makeappx del SDK de Windows 10.0.26100. Antes compila, salvo con "empaquetar.cmd sin-compilar": asi el paquete lleva
rem exactamente el mismo .exe que ya metio en la web el guion de Python que la empaqueta (cada compilacion sale distinta y las
rem dos formas de distribuirlo tienen que ser una; desde la 1.1.0, 2026-11-18).
setlocal
cd /d "%~dp0"
if /i "%~1"=="sin-compilar" (
  if not exist salida\ForeverTracker.exe echo Falta salida\ForeverTracker.exe: compila primero. && exit /b 1
) else (
  call "%~dp0compilar.cmd" || exit /b 1
)
set MAKEAPPX=%ProgramFiles(x86)%\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe
if exist salida\paquete rmdir /s /q salida\paquete
mkdir salida\paquete\Assets
copy /y paquete\AppxManifest.xml salida\paquete\ >nul
copy /y paquete\Assets\*.png salida\paquete\Assets\ >nul
copy /y salida\ForeverTracker.exe salida\paquete\ >nul
rem El mismo programa con otro nombre: el que arranca con Windows (ver AppxManifest.xml).
copy /y salida\ForeverTracker.exe salida\paquete\ForeverTrackerFondo.exe >nul
"%MAKEAPPX%" pack /o /d salida\paquete /p salida\ForeverTracker.msix
if errorlevel 1 exit /b 1
echo Paquete: %~dp0salida\ForeverTracker.msix
