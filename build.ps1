# Build, verify, launch — SlitherIn (new design)
# Usage: .\build.ps1 (from the repo root)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 1) Stop a running instance so the EXE isn't locked.
Get-Process slitherin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# 2) Build the full solution (app + tests).
dotnet build -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }

# 3) REV-verify: the EXE must literally contain the REV constant from Program.cs
#    (proves the build that just ran actually embedded the current revision).
$srcProgram = Join-Path $root 'src\Program.cs'
$match = Select-String -Path $srcProgram -Pattern 'REV\s*=\s*"(rev-[0-9]{8}-[0-9]+)"'
if (-not $match) { throw "Could not read REV from src\Program.cs" }
$expectedRev = $match.Matches[0].Groups[1].Value

$exe = Join-Path $root 'src\bin\Release\net48\slitherin.exe'
if (-not (Test-Path $exe)) { throw "Built EXE not found at $exe" }
$bytes = [IO.File]::ReadAllBytes($exe)
# Alignment-agnostic REV check: the literal's UTF-16 bytes must appear in the
# binary somewhere (the #US heap can place it at an odd offset, so decoding
# the whole file as UTF-16 and using .Contains can miss it). Compare hex.
$haystack = [BitConverter]::ToString($bytes).Replace('-', '')
$needle   = [BitConverter]::ToString([Text.Encoding]::Unicode.GetBytes($expectedRev)).Replace('-', '')
if (-not $haystack.Contains($needle)) { throw "REV '$expectedRev' not found in built EXE." }

Write-Host "REV verified: $expectedRev"

# 4) Relaunch into the tray.
Start-Process $exe
Write-Host "slitherin launched."