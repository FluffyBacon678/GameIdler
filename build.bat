@echo off
:: ============================================================
::  build.bat  –  Steam Card Idler build script
::  Requirements: .NET Framework 4.x (any version >= 4.0)
::  Pass  -ci  to suppress the interactive pause at the end.
:: ============================================================

echo Building Steam Card Idler...
echo.

:: Locate the C# 5 compiler shipped with .NET Framework 4.x
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

if not exist "%CSC%" (
    echo ERROR: C# compiler not found at:
    echo   %CSC%
    echo.
    echo Install .NET Framework 4.x Developer Pack and try again:
    echo   https://dotnet.microsoft.com/download/dotnet-framework
    echo.
    if "%1"=="-ci" ( exit /b 1 ) else ( pause & exit /b 1 )
)

"%CSC%" ^
    /target:winexe ^
    /out:SteamIdler.exe ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    /reference:System.dll ^
    /reference:System.Net.dll ^
    /reference:Newtonsoft.Json.dll ^
    /reference:HtmlAgilityPack.dll ^
    /optimize+ ^
    SteamIdler.cs

set BUILD_RESULT=%ERRORLEVEL%

echo.
if %BUILD_RESULT% == 0 (
    echo Build succeeded: SteamIdler.exe
) else (
    echo Build FAILED. See errors above.
)

if not "%1"=="-ci" pause
exit /b %BUILD_RESULT%
