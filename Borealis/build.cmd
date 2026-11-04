@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /unsafe /target:winexe /out:Borealis.scr /r:System.Windows.Forms.dll /r:System.Drawing.dll Borealis.cs
