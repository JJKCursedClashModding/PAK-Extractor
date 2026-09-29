@echo off
setlocal
title Build JJKCC-Unpacker

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\JJKCC-Unpacker.csproj"
set "OUT=%ROOT%dist"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [build] The .NET SDK ^(dotnet^) was not found on PATH.
    echo [build] Install the .NET SDK, or build the sources another way.
    exit /b 1
)

if exist "%OUT%" rmdir /s /q "%OUT%"

echo [build] Building %PROJ% ...
dotnet build "%PROJ%" -c Release -o "%OUT%" --nologo -v minimal
if errorlevel 1 (
    echo [build] FAILED
    exit /b 1
)

del /q "%OUT%\*.pdb" 2>nul
del /q "%OUT%\*.xml" 2>nul

if exist "%OUT%\JJKCC-Unpacker.exe" (
    for %%F in ("%OUT%\JJKCC-Unpacker.exe") do echo [build] OK -^> %%~fF  ^(%%~zF bytes^)
) else (
    echo [build] FAILED - exe not produced
    exit /b 1
)

endlocal
exit /b 0
