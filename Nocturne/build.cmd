@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /out:Nocturne.scr /r:System.Windows.Forms.dll /r:System.Drawing.dll /resource:moon_albedo.jpg,moon_albedo.jpg /resource:moon_slope.png,moon_slope.png Nocturne.cs
