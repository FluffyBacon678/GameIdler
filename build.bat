@echo off
echo Building Steam Card Idler...

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe

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

if %ERRORLEVEL% == 0 (
    echo.
    echo Build succeeded: SteamIdler.exe
) else (
    echo.
    echo Build FAILED. See errors above.
)
pause
