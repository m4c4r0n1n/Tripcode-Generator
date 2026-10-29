@echo off
setlocal
set "APP=%~dp0TripcodeCudaGui\publish\Tripcode Generator.exe"
if not exist "%APP%" (
  echo Tripcode Generator has not been built yet.
  echo Run Build-CUDA.ps1 once, then open Tripcode Generator.exe directly.
  pause
  exit /b 1
)
start "" "%APP%"
