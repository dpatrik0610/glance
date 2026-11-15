# Publishes a GitHub release of this source tree (desktop shortcut "Publish Glance release", or run by hand).
# Next patch version unless -Version is given. Builds and selftests before touching git, so a failure leaves no commit or tag.
# Notes are the commit subjects since the last release; edit them on GitHub afterwards if you like.
param([string]$Version)
$root = Split-Path $PSScriptRoot
$csproj = Join-Path $root 'src\Glance\Glance.csproj'
$public = 'dpatrik0610/glance-releases' # Services\Updater.cs reads from here
$Host.UI.RawUI.WindowTitle = 'Glance - publish release'
Set-Location $root

function Fail($msg) {
    Write-Host "`n$msg" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

$gh = (Get-Command gh -ErrorAction Ignore).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { Fail 'GitHub CLI (gh) is not installed.' }
$iscc = @((Get-Command ISCC -ErrorAction Ignore).Source, "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { Fail 'Inno Setup 6 is not installed (it builds the setup): winget install JRSoftware.InnoSetup' }

if (git status --porcelain) { Fail 'There are uncommitted changes. Commit them first: a release is built from a commit.' }
if ((git branch --show-current) -ne 'main') { Fail 'Not on main.' }
git fetch -q --tags origin
$last = git describe --tags --abbrev=0
$changes = @(git log --reverse --format=%s "$last..HEAD" | Where-Object { $_ -notmatch '^Version ' })
if (-not $changes) { Fail "Nothing new since $last." }

$text = [IO.File]::ReadAllText($csproj)
$current = [version][regex]::Match($text, '<Version>(.+?)</Version>').Groups[1].Value
if (-not $Version) { $Version = "$($current.Major).$($current.Minor).$($current.Build + 1)" }
$tag = "v$Version"
if (git tag --list $tag) { Fail "$tag already exists." }
Write-Host "Glance $Version ($($changes.Count) changes since $last)" -ForegroundColor Cyan

function Undo($msg) { git checkout -q -- $csproj; Fail $msg }
[IO.File]::WriteAllText($csproj, $text.Replace("<Version>$current</Version>", "<Version>$Version</Version>"))

Write-Host 'Building...' -ForegroundColor DarkGray
# Its own intermediate folder: bin\build is the deploy's, and a Glance running from there locks it.
# PublicBuild: the exe carries no path from this PC (and so no "Rebuild and restart").
$out = Join-Path $root "bin\release\Glance-$Version"
Remove-Item $out -Recurse -Force -ErrorAction Ignore
dotnet publish $csproj -c Release -o $out "-p:OutDir=$root\bin\release-build\" -p:PublicBuild=true --nologo -v q -clp:ErrorsOnly
if ($LASTEXITCODE) { Undo 'Build failed. Nothing was committed or published.' }
& "$PSScriptRoot\sign.ps1" -Path "$out\Glance.exe" | Out-Null

Write-Host 'Selftest...' -ForegroundColor DarkGray
$log = Join-Path $env:TEMP 'glance-selftest.txt'
$test = Start-Process "$out\Glance.exe" '--selftest' -Wait -PassThru -RedirectStandardError $log
Get-Content $log -ErrorAction SilentlyContinue | Write-Host -ForegroundColor $(if ($test.ExitCode) { 'Red' } else { 'Green' })
if ($test.ExitCode) { Undo 'Selftest failed. Nothing was committed or published.' }

$zip = Join-Path $root "bin\release\Glance-$Version-win-x64.zip"
Compress-Archive -Path "$out\*" -DestinationPath $zip -Force
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()

# The installer. Its name must not end in -win-x64.zip: Services\Updater.cs picks the zip by that.
Write-Host 'Setup...' -ForegroundColor DarkGray
& $iscc /Q "/DAppVersion=$Version" "/DBuildDir=$out" "/O$root\bin\release" "$root\installer\Glance.iss"
if ($LASTEXITCODE) { Undo 'Building the setup failed. Nothing was committed or published.' }
$setup = Join-Path $root "bin\release\Glance-$Version-setup.exe"
& "$PSScriptRoot\sign.ps1" -Path $setup | Out-Null

$notes = Join-Path $env:TEMP 'glance-release-notes.md'
$lines = $changes | ForEach-Object { if ($_ -match '^([^:]{1,40}): (.+)$') { "- **$($Matches[1]):** $($Matches[2])" } else { "- $_" } }
@"
$($lines -join "`n")

**Install:** run ``Glance-$Version-setup.exe`` (no admin needed), or unzip ``Glance-$Version-win-x64.zip`` anywhere and run ``Glance.exe``. Settings live in %APPDATA%\Glance and survive updates. Needs Windows 10/11 x64, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and WebView2.

The setup and exe aren't signed by a public certificate, so SmartScreen may warn the first time: More info, then Run anyway.

SHA-256 of the zip: ``$sha``
"@ | ForEach-Object { [IO.File]::WriteAllText($notes, $_) } # no BOM, in either PowerShell

Write-Host 'Publishing...' -ForegroundColor DarkGray
git commit -q -m "Version $Version" -- $csproj
if ($LASTEXITCODE) { Undo 'Committing the version failed. Nothing was published.' }
git tag $tag
$create = "gh release create $tag `"$zip`" `"$setup`" --title `"Glance $Version`" --notes-file `"$notes`""
git push -q origin main $tag
if ($LASTEXITCODE) { Fail "Push failed; the commit and tag $tag are only local. Finish by hand: git push origin main $tag, then $create" }
& $gh release create $tag $zip $setup --title "Glance $Version" --notes-file $notes
if ($LASTEXITCODE) { Fail "Creating the release failed ($tag is pushed). Retry: $create, and again with -R $public" }
# The source repo is private: installed copies update from this public one, which holds only the releases.
& $gh release create $tag $zip $setup -R $public --title "Glance $Version" --notes-file $notes
if ($LASTEXITCODE) { Fail "The private release is up, but the public one failed, so nobody gets the update yet. Retry: $create -R $public" }

Write-Host "Done: Glance $Version is released." -ForegroundColor Green
& $gh release view $tag --web
Start-Sleep -Milliseconds 800
