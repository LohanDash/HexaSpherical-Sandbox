@echo off
set "DOTNET_ROOT=C:\Program Files\dotnet"
set "PATH=C:\Program Files\dotnet;%PATH%"
set "GODOT_EXE=C:\Users\lohan\AppData\Local\Microsoft\WinGet\Packages\GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64.exe"

if not exist "%GODOT_EXE%" (
    echo Godot 4.7.1 .NET est introuvable.
    pause
    exit /b 1
)

start "HexaSpherical Sandbox" "%GODOT_EXE%" --editor --path "%~dp0."
