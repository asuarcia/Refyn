# Builds PromptsmithHost.exe with the .NET Framework compiler that is already
# on every Windows machine. No SDK, no NuGet, no toolchain to install.
#
#   powershell -ExecutionPolicy Bypass -File host\build.ps1
#
# Output: host\bin\PromptsmithHost.exe (a single self-contained ~50KB exe).

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$source  = Join-Path $here 'PromptsmithHost.cs'
$manifest= Join-Path $here 'app.manifest'
$outDir  = Join-Path $here 'bin'
$outExe  = Join-Path $outDir 'PromptsmithHost.exe'

# Prefer the 64-bit compiler, but a 32-bit-only Windows still has the other one.
$candidates = @(
  "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
  "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
  throw "No .NET Framework 4 compiler found. Looked in:`n  $($candidates -join "`n  ")"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# /target:winexe suppresses the console window that would otherwise flash on
# every launch of a background app.
$refs = @(
  'System.dll',
  'System.Core.dll',
  'System.Drawing.dll',
  'System.Windows.Forms.dll',
  'System.Net.Http.dll'
)

$compilerArgs = @(
  '/nologo',
  '/target:winexe',
  '/platform:anycpu',
  '/optimize+',
  '/warn:4',
  "/out:$outExe",
  "/win32manifest:$manifest"
) + ($refs | ForEach-Object { "/reference:$_" }) + @($source)

Write-Host "Compiling with $csc" -ForegroundColor DarkGray
& $csc @compilerArgs
if ($LASTEXITCODE -ne 0) {
  throw "Compilation failed with exit code $LASTEXITCODE"
}

$size = [math]::Round((Get-Item $outExe).Length / 1KB, 1)
Write-Host "Built $outExe ($size KB)" -ForegroundColor Green
