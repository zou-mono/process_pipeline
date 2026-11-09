@echo off
setlocal enabledelayedexpansion

set "ACCORE=C:\Program Files\Autodesk\AutoCAD 2019\accoreconsole.exe"
set "ROOT=D:\Codes\Tools\process_pipeline"
set "SCR_FILE=%ROOT%\Tests\Cad.Plugin\load.scr"

set "DEFAULT_DWG="
for %%F in ("%ROOT%\Data\*.dwg") do (
    if not defined DEFAULT_DWG set "DEFAULT_DWG=%%~fF"
)

set "TARGET_CMD=%~1"
set "NO_PAUSE=0"
if /i "%~2"=="--no-pause" set "NO_PAUSE=1"
if /i "%~2"=="/nopause" set "NO_PAUSE=1"

if not "%TARGET_CMD%"=="" goto :RUN_CMD

echo ============================================================
echo   Cad.Plugin Headless Test Runner (accoreconsole)
echo ============================================================
echo   [1] test_match_pipe       (Test_Match_Pipe)
echo   [2] test_compare_segment  (Test_Compare_Segment)
echo   [3] test_merge_polylines  (Test_Merge_Polylines)
echo   [4] test_read_dwg         (Test_Read_Dwg)
echo   [5] test_dwg_stats        (Test_Dwg_Stats)
echo   [6] test_config           (Test_Config)
echo   [7] test_match_group      (Test_Match_Group)
echo   [8] test_check_plan       (Test_Check_Plan)
echo   [9] list                  (List all test commands)
echo ============================================================
set /p "USER_INPUT=Select [1-9] or enter command name (default 1): "

if "%USER_INPUT%"=="" set "USER_INPUT=1"
if "%USER_INPUT%"=="1" set "TARGET_CMD=test_match_pipe"
if "%USER_INPUT%"=="2" set "TARGET_CMD=test_compare_segment"
if "%USER_INPUT%"=="3" set "TARGET_CMD=test_merge_polylines"
if "%USER_INPUT%"=="4" set "TARGET_CMD=test_read_dwg"
if "%USER_INPUT%"=="5" set "TARGET_CMD=test_dwg_stats"
if "%USER_INPUT%"=="6" set "TARGET_CMD=test_config"
if "%USER_INPUT%"=="7" set "TARGET_CMD=test_match_group"
if "%USER_INPUT%"=="8" set "TARGET_CMD=test_check_plan"
if "%USER_INPUT%"=="9" set "TARGET_CMD=list"
if "%TARGET_CMD%"=="" set "TARGET_CMD=%USER_INPUT%"

:RUN_CMD
set "CAD_TEST_CMD=%TARGET_CMD%"
echo.
echo [Runner] CAD_TEST_CMD = %CAD_TEST_CMD%
echo [Runner] Host DWG     = %DEFAULT_DWG%
echo.

"%ACCORE%" /i "%DEFAULT_DWG%" /s "%SCR_FILE%"

if "%NO_PAUSE%"=="0" (
    echo.
    pause
)
endlocal
