param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'TripcodeCudaGui\publish')
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$nvcc = (Get-Command nvcc -ErrorAction SilentlyContinue).Source
if (-not $nvcc -and $env:CUDA_PATH) {
    $candidate = Join-Path $env:CUDA_PATH 'bin\nvcc.exe'
    if (Test-Path $candidate) { $nvcc = $candidate }
}
if (-not $nvcc) {
    $candidate = 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4\bin\nvcc.exe'
    if (Test-Path $candidate) { $nvcc = $candidate }
}
if (-not $nvcc) {
    throw 'CUDA nvcc was not found. Install the NVIDIA CUDA Toolkit, then run this build script again.'
}

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsPath = if (Test-Path $vswhere) {
    & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}
$vcvars = if ($vsPath) { Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat' }
if (-not $vcvars -or -not (Test-Path $vcvars)) {
    throw 'Visual Studio 2022 C++ Build Tools (x64) were not found.'
}

Write-Host 'Building the CUDA engine for the GPU...'
$outputExe = Join-Path $OutputDirectory 'TripcodeCuda.exe'
$build = 'call "' + $vcvars + '" >nul && "' + $nvcc + '" -O3 -std=c++17 -gencode arch=compute_86,code=sm_86 -gencode arch=compute_89,code=sm_89 -gencode arch=compute_120,code=sm_120 -gencode arch=compute_120,code=compute_120 -Xcompiler /O2 -o "' + $outputExe + '" TripcodeCuda.cu'
& $env:ComSpec /d /s /c $build
if ($LASTEXITCODE -ne 0) {
    throw "CUDA build failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host 'Publishing the self-contained Windows desktop app...'
$guiProject = Join-Path $PSScriptRoot 'TripcodeCudaGui\TripcodeCudaGui.csproj'
& dotnet publish $guiProject --configuration Release --runtime win-x64 --self-contained true `
    --output $OutputDirectory -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Desktop app publish failed with exit code $LASTEXITCODE."
}

$guiExe = Join-Path $OutputDirectory 'Tripcode Generator.exe'
if (-not (Test-Path -LiteralPath $guiExe) -or -not (Test-Path -LiteralPath $outputExe)) {
    throw 'The build completed without producing the desktop app and CUDA engine.'
}

Write-Host ''
Write-Host "Build complete. Open '$guiExe' to run the app."
