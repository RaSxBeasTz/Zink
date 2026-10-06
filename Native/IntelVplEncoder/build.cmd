@echo off
setlocal

set "ROOT=%~1"
set "CONFIG=%~2"
set "VPL=%~3"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSINSTALL="

if "%ROOT%"=="" exit /b 2
if "%CONFIG%"=="" exit /b 2
if "%VPL%"=="" exit /b 2

if exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSINSTALL=%%i"
)

if "%VSINSTALL%"=="" (
  for /d %%i in ("%ProgramFiles%\Microsoft Visual Studio\*") do (
    if exist "%%~i\Community\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Community"
    if exist "%%~i\Professional\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Professional"
    if exist "%%~i\Enterprise\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\Enterprise"
    if exist "%%~i\BuildTools\Common7\Tools\VsDevCmd.bat" set "VSINSTALL=%%~i\BuildTools"
  )
)

if "%VSINSTALL%"=="" (
  echo Visual Studio C++ build tools were not found.
  exit /b 1
)

call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%

where cl.exe >nul 2>nul
if not errorlevel 1 goto compiler_ready

rem Visual Studio updates can leave VsDevCmd available while vcvarsall.bat is
rem temporarily absent. Use the installed compiler and Windows SDK directly.
for /f "delims=" %%i in ('dir /b /ad /o-n "%VSINSTALL%\VC\Tools\MSVC" 2^>nul') do if not defined VCTOOLS set "VCTOOLS=%VSINSTALL%\VC\Tools\MSVC\%%i"
for /f "delims=" %%i in ('dir /b /ad /o-n "%ProgramFiles(x86)%\Windows Kits\10\Include" 2^>nul') do if not defined WINSDKVER set "WINSDKVER=%%i"

if not defined VCTOOLS goto compiler_missing
if not defined WINSDKVER goto compiler_missing
if not exist "%VCTOOLS%\bin\Hostx64\x64\cl.exe" goto compiler_missing

set "PATH=%VCTOOLS%\bin\Hostx64\x64;%PATH%"
set "INCLUDE=%VCTOOLS%\include;%ProgramFiles(x86)%\Windows Kits\10\Include\%WINSDKVER%\ucrt;%ProgramFiles(x86)%\Windows Kits\10\Include\%WINSDKVER%\shared;%ProgramFiles(x86)%\Windows Kits\10\Include\%WINSDKVER%\um;%ProgramFiles(x86)%\Windows Kits\10\Include\%WINSDKVER%\winrt;%ProgramFiles(x86)%\Windows Kits\10\Include\%WINSDKVER%\cppwinrt"
set "VCLIB=%VCTOOLS%\lib\x64"
if not exist "%VCLIB%\msvcrt.lib" set "VCLIB=%VCTOOLS%\lib\onecore\x64"
set "LIB=%VCLIB%;%ProgramFiles(x86)%\Windows Kits\10\Lib\%WINSDKVER%\ucrt\x64;%ProgramFiles(x86)%\Windows Kits\10\Lib\%WINSDKVER%\um\x64"

:compiler_ready

if not exist "%ROOT%\x64\%CONFIG%" mkdir "%ROOT%\x64\%CONFIG%"
if not exist "%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%" mkdir "%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%"

where cl.exe >nul 2>nul
if errorlevel 1 goto compiler_missing

cl.exe /nologo /LD /MD /EHsc /O2 /std:c++17 ^
  /I"%VPL%\lib\native\include" ^
  /Fo"%ROOT%\obj\Native\IntelVplEncoder\%CONFIG%\\" ^
  /Fe"%ROOT%\x64\%CONFIG%\ZinkIntelVplEncoder.dll" ^
  "%ROOT%\Native\IntelVplEncoder\ZinkIntelVplEncoder.cpp" ^
  /link /LIBPATH:"%VPL%\lib\native\win-x64" vpl.lib

exit /b %errorlevel%

:compiler_missing
echo cl.exe or its required build environment was not found.
exit /b 1
