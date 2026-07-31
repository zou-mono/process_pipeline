@echo off

set ACCORE=C:\Program Files\Autodesk\AutoCAD 2019\accoreconsole.exe
set ROOT=D:\Codes\Tools\process_pipeline

"%ACCORE%" /i "D:\temp\1.dwg" ^
/s "%ROOT%\tests\Cad.Plugin\load.scr"

pause