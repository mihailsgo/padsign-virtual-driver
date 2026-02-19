@echo off
setlocal enabledelayedexpansion
if not exist "out\client-package" (
  echo NO_CLIENT_PACKAGE_DIR
  exit /b 0
)
pushd "out\client-package"
set "keep="
for /f "delims=" %%i in ('dir /b /ad /o-n') do (
  if not defined keep (
    set "keep=%%i"
  ) else (
    rd /s /q "%%i"
  )
)
echo KEPT=!keep!
dir /b /ad
popd
endlocal
