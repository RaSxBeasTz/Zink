@echo off
setlocal
set "ROOT=%~1"
set "CONFIG=%~2"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSINSTALL="
if "%ROOT%"=="" (
  echo Missing ROOT argument
  exit /b 2
)
if "%CONFIG%"=="" (
  echo Missing CONFIG argument
  exit /b 2
)
rem Try to locate vswhere if the default path doesn't exist
if not exist "%VSWHERE%" (
  for /f "usebackq tokens=*" %%v in (`where vswhere 2^>nul`) do (
    set "VSWHERE=%%v"
    goto :VSWHERE_FOUND
  )
  if exist "%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe" set "VSWHERE=%ProgramFiles%\Microsoft Visual Studio\Installer\vswhere.exe"
)
:VSWHERE_FOUND
if not exist "%VSWHERE%" (
  echo vswhere not found. Please install the Visual Studio Installer or add vswhere to PATH.
  exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do set "VSINSTALL=%%i"
if "%VSINSTALL%"=="" (
  echo Visual C++ Build Tools not found. Please install the "Desktop development with C++" workload.
  exit /b 1
)
call "%VSINSTALL%\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
if not exist "%ROOT%\x64\%CONFIG%" mkdir "%ROOT%\x64\%CONFIG%"
if not exist "%ROOT%\obj\Native\ZinkNvencEncoder\%CONFIG%" mkdir "%ROOT%\obj\Native\ZinkNvencEncoder\%CONFIG%"
where cl.exe >nul 2>&1
if errorlevel 1 (
  echo cl.exe not found in PATH after VsDevCmd.bat. Ensure Visual C++ tools are installed.
  exit /b 1
)
cl.exe /nologo /LD /MD /EHsc /O2 /std:c++17 ^
  /I"%ROOT%\Native\ZinkNvencEncoder" ^
  /Fo"%ROOT%\obj\Native\ZinkNvencEncoder\%CONFIG%\\" ^
  /Fe"%ROOT%\x64\%CONFIG%\ZinkNvencEncoder.dll" ^
  "%ROOT%\Native\ZinkNvencEncoder\ZinkNvencEncoder.cpp" ^
  d3d11.lib dxgi.lib
exit /b %errorlevel%
