# Builds a signed x64 MSIX of WindowsCalendar and writes the .msix, an .appinstaller
# and the public .cer to the release folder, then commits and pushes the version bump
# and creates the GitHub release v<version>. Each run bumps the third version part
# in Package.appxmanifest. See README "Publish a new build" and docs/prd/app-updates.md.
param([string]$ReleaseDir = (Join-Path $env:USERPROFILE 'OneDrive\WindowsCalendar Releases'))

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (Get-Process WindowsCalendar -ErrorAction SilentlyContinue) {
    Write-Host 'Quit WindowsCalendar from the tray first.'
    exit 1
}

# The GitHub release step needs the GitHub CLI signed in; checked before anything changes.
# Windows PowerShell turns redirected native stderr into an error under 'Stop', so relax it here.
& { $ErrorActionPreference = 'Continue'; gh auth status *> $null }
if ($LASTEXITCODE -ne 0) {
    Write-Host 'GitHub CLI is not signed in. Run "gh auth login" first.'
    exit 1
}

$manifestPath = Join-Path $root 'WindowsCalendar\Package.appxmanifest'
$original = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8)
$identity = ([xml]$original).Package.Identity
$name = $identity.Name
$publisher = $identity.Publisher

# Anchored on <Identity so the MinVersion attributes never match.
$rx = '(<Identity\b[^>]*?\bVersion=")(\d+)\.(\d+)\.(\d+)\.(\d+)(")'
$m = [regex]::Match($original, $rx)
if (-not $m.Success) { throw 'Identity Version not found in Package.appxmanifest' }
$newVersion = '{0}.{1}.{2}.0' -f $m.Groups[2].Value, $m.Groups[3].Value, ([int]$m.Groups[4].Value + 1)
$bumped = $original.Substring(0, $m.Index) + $m.Groups[1].Value + $newVersion + $m.Groups[6].Value + $original.Substring($m.Index + $m.Length)

New-Item -ItemType Directory -Force $ReleaseDir | Out-Null
$msixOut = Join-Path $ReleaseDir "WindowsCalendar_${newVersion}_x64.msix"
if (Test-Path $msixOut) {
    Write-Host "$msixOut already exists. Nothing changed."
    exit 1
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'WindowsCalendar signing' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5) -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Warning 'A new certificate was created and must be trusted again on every PC (README Install step 2).'
}
Write-Host "Certificate thumbprint: $($cert.Thumbprint)"
$cerOut = Join-Path $ReleaseDir 'WindowsCalendar.cer'
Export-Certificate -Cert $cert -FilePath $cerOut -Type CERT -Force | Out-Null

function Write-AppInstaller([string]$path, [string]$selfUri, [string]$msixUri) {
    $esc = { param($s) [Security.SecurityElement]::Escape($s) }
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$(& $esc $newVersion)" Uri="$(& $esc $selfUri)">
  <MainPackage Name="$(& $esc $name)" Publisher="$(& $esc $publisher)" Version="$(& $esc $newVersion)" ProcessorArchitecture="x64" Uri="$(& $esc $msixUri)" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="24" ShowPrompt="true" UpdateBlocksActivation="false" />
  </UpdateSettings>
</AppInstaller>
"@
    [IO.File]::WriteAllText($path, $xml, (New-Object Text.UTF8Encoding $false))
}

$utf8Bom = New-Object Text.UTF8Encoding $true
[IO.File]::WriteAllText($manifestPath, $bumped, $utf8Bom)
$pkgDir = Join-Path $env:TEMP "WindowsCalendar-msix-$newVersion"
try {
    if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
    dotnet build (Join-Path $root 'WindowsCalendar\WindowsCalendar.csproj') -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxBundle=Never -p:AppxPackageSigningEnabled=true "-p:PackageCertificateThumbprint=$($cert.Thumbprint)" "-p:AppxPackageDir=$pkgDir\"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }

    $msix = Get-ChildItem $pkgDir -Recurse -Filter *.msix | Select-Object -First 1
    if (-not $msix) { throw "No .msix found under $pkgDir" }
    Copy-Item $msix.FullName $msixOut
    Write-Host "Signature status: $((Get-AuthenticodeSignature $msixOut).Status)"

    $appInstaller = Join-Path $ReleaseDir 'WindowsCalendar.appinstaller'
    Write-AppInstaller $appInstaller ([Uri]$appInstaller).AbsoluteUri ([Uri]$msixOut).AbsoluteUri

    Remove-Item $pkgDir -Recurse -Force
}
catch {
    [IO.File]::WriteAllText($manifestPath, $original, $utf8Bom)
    Write-Host "Failed: $($_.Exception.Message)"
    exit 1
}

Write-Host "Published version $newVersion"
Write-Host "  $msixOut"
Write-Host "  $appInstaller"
Write-Host "  $cerOut"
Write-Host 'Double-click WindowsCalendar.appinstaller to install or update.'

# GitHub release: commit and push the version bump, then release v<version> with the .msix, the .cer and an
# .appinstaller that points at GitHub. Installs from it check the latest release for updates.
$repo = gh repo view --json nameWithOwner -q .nameWithOwner
$base = "https://github.com/$repo/releases"
$ghDir = Join-Path $env:TEMP "WindowsCalendar-github-$newVersion"
New-Item -ItemType Directory -Force $ghDir | Out-Null
$ghAppInstaller = Join-Path $ghDir 'WindowsCalendar.appinstaller'
Write-AppInstaller $ghAppInstaller "$base/latest/download/WindowsCalendar.appinstaller" "$base/download/v$newVersion/WindowsCalendar_${newVersion}_x64.msix"
$notes = Join-Path $ghDir 'notes.md'
[IO.File]::WriteAllText($notes, ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release-notes.md')) -replace '\{VERSION\}', $newVersion), (New-Object Text.UTF8Encoding $false))

git -C $root commit -m "Package $newVersion" -- WindowsCalendar/Package.appxmanifest
if ($LASTEXITCODE -eq 0) { git -C $root push }
if ($LASTEXITCODE -eq 0) {
    gh release create "v$newVersion" --repo $repo --target (git -C $root rev-parse HEAD) --title "WindowsCalendar $newVersion" --notes-file $notes $msixOut $cerOut $ghAppInstaller
}
$ghResult = $LASTEXITCODE
Remove-Item $ghDir -Recurse -Force
if ($ghResult -ne 0) {
    Write-Host 'GitHub release failed. The local release above is complete; commit, push and release by hand.'
    exit 1
}
Write-Host "GitHub release: $base/tag/v$newVersion"
