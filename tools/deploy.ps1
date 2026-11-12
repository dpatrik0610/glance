# Rebuilds Glance from this source tree and restarts it (tray menu / Ctrl+K "Rebuild and restart", or run by hand).
# Builds into a staging folder first, so a broken build or failing selftest leaves the running Glance alone.
$root = Split-Path $PSScriptRoot
$stage = Join-Path $root 'bin\staging'
$app = Join-Path $root 'bin\Release\net10.0-windows\win-x64'
$Host.UI.RawUI.WindowTitle = 'Glance - rebuild and restart'

function Fail($msg) {
    Write-Host "`n$msg" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host 'Building...' -ForegroundColor DarkGray
# publish, not build: only publish precompiles (ReadyToRun), so Glance starts without compiling itself first.
# Its intermediate build goes to bin\build: the default output folder is the one the running Glance is in.
dotnet publish "$root\Glance.csproj" -c Release -o $stage "-p:OutDir=$root\bin\build\" --nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE) { Fail 'Build failed. The running Glance was left alone.' }
# The build signs its own copy of the exe; the published one is a fresh copy.
& "$PSScriptRoot\sign.ps1" -Path "$stage\Glance.exe" | Out-Null

# Glance is a GUI exe: Start-Process -Wait is what makes PowerShell wait for it and see its exit code.
Write-Host 'Selftest...' -ForegroundColor DarkGray
$test = Start-Process "$stage\Glance.exe" '--selftest' -Wait -PassThru
if ($test.ExitCode) { Fail 'Selftest failed. The running Glance was left alone.' }

$running = @(Get-Process Glance -ErrorAction SilentlyContinue)
if ($running) {
    Write-Host 'Closing Glance...' -ForegroundColor DarkGray
    # A second Glance hands --exit to the running one, which saves and quits without asking.
    Start-Process "$stage\Glance.exe" '--exit' -Wait
    $running | Wait-Process -Timeout 15 -ErrorAction SilentlyContinue
    $running | Where-Object { -not $_.HasExited } | Stop-Process -Force # an old build that doesn't know --exit
}

robocopy $stage $app /E /R:5 /W:1 /NJH /NJS /NDL /NFL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "Copying the new build failed (robocopy code $LASTEXITCODE)." }

# Through Explorer, so Glance (and every terminal it opens) gets a normal environment, not this shell's.
explorer.exe (Join-Path $app 'Glance.exe')
Write-Host 'Done.' -ForegroundColor Green
Start-Sleep -Milliseconds 800
