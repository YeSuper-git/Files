param(
	[Parameter(Mandatory = $true)][string]$PublishDirectory,
	[Parameter(Mandatory = $true)][string]$PackageName,
	[Parameter(Mandatory = $true)][version]$PackageVersion,
	[Parameter(Mandatory = $true)][string]$DisplayName,
	[Parameter(Mandatory = $true)][string]$SigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
$executable = Join-Path $payload 'Files.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Publish Files.App before creating its preview identity.' }
if ($PackageName -eq 'FilesDev' -or $PackageName -notmatch '^FilesDev\.[A-Za-z0-9]+Preview$') {
	throw 'Use a dedicated FilesDev.*Preview identity, never the official identity.'
}
& (Join-Path $workspace 'src\Files.App\Assets\AppTiles\Taskbar\Verify-TaskbarIcons.ps1')

$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makepri.exe' |
	Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Windows SDK tools were not found.' }
$sdkDirectory = $sdk.DirectoryName
$identityDirectory = Join-Path ([IO.Path]::GetTempPath()) ("$PackageName-identity-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $identityDirectory | Out-Null
$manifestPath = Join-Path $identityDirectory 'AppxManifest.xml'
[xml]$manifest = Get-Content -Raw -LiteralPath (Join-Path $workspace '.github\installer\ExternalLocation.AppxManifest')
$manifest.Package.Identity.Name = $PackageName
$manifest.Package.Identity.Version = $PackageVersion.ToString(4)
$manifest.Package.Properties.DisplayName = $DisplayName
$visual = $manifest.SelectSingleNode("//*[local-name()='VisualElements']")
$visual.SetAttribute('DisplayName', $DisplayName)
$visual.SetAttribute('AppListEntry', 'none')
if ($visual.GetAttribute('Square44x44Logo') -ne 'Assets\AppTiles\Dev\Square44x44Logo.png' -or
	$visual.GetAttribute('BackgroundColor') -ne 'transparent') {
	throw 'The preview manifest must use the unqualified taskbar logo and transparent background.'
}
$manifest.Save($manifestPath)

$assetDirectory = Join-Path $identityDirectory 'Assets\AppTiles\Dev'
New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
Copy-Item -Path (Join-Path $workspace 'src\Files.App\Assets\AppTiles\Dev\*') -Destination $assetDirectory
$archiveDirectory = Join-Path $identityDirectory 'Assets\archives'
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspace 'src\Files.App\Assets\archives\ExtensionIcon.png') -Destination $archiveDirectory
$priConfiguration = Join-Path $identityDirectory 'priconfig.xml'
& (Join-Path $sdkDirectory 'makepri.exe') createconfig /cf $priConfiguration /dq en-US
if ($LASTEXITCODE -ne 0) { throw 'PRI configuration generation failed.' }
& (Join-Path $sdkDirectory 'makepri.exe') new /pr $identityDirectory /cf $priConfiguration /mn $manifestPath /of (Join-Path $identityDirectory 'resources.pri') /o
if ($LASTEXITCODE -ne 0) { throw 'Identity PRI generation failed.' }

$indexedResources = ''
foreach ($pri in Get-ChildItem -LiteralPath $identityDirectory -Filter 'resources*.pri') {
	$dumpPath = Join-Path $identityDirectory ("dump-$($pri.BaseName).xml")
	& (Join-Path $sdkDirectory 'makepri.exe') dump /if $pri.FullName /of $dumpPath
	if ($LASTEXITCODE -ne 0) { throw 'Identity PRI inspection failed.' }
	$indexedResources += [IO.File]::ReadAllText($dumpPath)
	Remove-Item -LiteralPath $dumpPath
}
$variants = @(Get-ChildItem -LiteralPath $assetDirectory -Filter 'Square44x44Logo.targetsize-*_altform-*.png')
if ($variants.Count -eq 0) { throw 'Unplated taskbar variants are missing.' }
foreach ($variant in $variants) {
	if (-not $indexedResources.Contains($variant.Name)) { throw "Unindexed taskbar variant: $($variant.Name)" }
}

$packagePath = Join-Path $payload 'PreviewIdentity.msix'
# External-location packages deliberately keep Files.exe outside the identity MSIX.
& (Join-Path $sdkDirectory 'makeappx.exe') pack /d $identityDirectory /p $packagePath /o /nv
if ($LASTEXITCODE -ne 0) { throw 'Preview identity packaging failed.' }
& (Join-Path $sdkDirectory 'signtool.exe') sign /sha1 $SigningCertificateThumbprint /fd SHA256 $packagePath
if ($LASTEXITCODE -ne 0) { throw 'Preview identity signing failed.' }

$executableManifest = Join-Path $identityDirectory 'executable.manifest'
& (Join-Path $sdkDirectory 'mt.exe') "-inputresource:$executable;#1" "-out:$executableManifest"
if ($LASTEXITCODE -ne 0) { throw 'Executable manifest extraction failed.' }
[xml]$assembly = [IO.File]::ReadAllText($executableManifest)
$identity = $assembly.SelectSingleNode("//*[local-name()='msix']")
if (-not $identity) { throw 'The published executable lacks external-location identity metadata.' }
$identity.SetAttribute('packageName', $PackageName)
$assembly.Save($executableManifest)
& (Join-Path $sdkDirectory 'mt.exe') -manifest $executableManifest "-outputresource:$executable;#1"
if ($LASTEXITCODE -ne 0) { throw 'Executable identity update failed; close this preview before retrying.' }

$record = [ordered]@{
	SourceCommit = (& git -C $workspace rev-parse HEAD)
	SourceChanges = @(& git -C $workspace status --short)
	ExecutableProductVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
	PackageName = $PackageName
	PackageVersion = $PackageVersion.ToString(4)
	ManifestSource = '.github/installer/ExternalLocation.AppxManifest'
	IdentityDirectory = $identityDirectory
	LogoReference = $visual.GetAttribute('Square44x44Logo')
	BackgroundColor = $visual.GetAttribute('BackgroundColor')
	IndexedUnplatedVariants = $variants.Count
	ExecutableSHA256 = (Get-FileHash -LiteralPath $executable).Hash
	IdentitySHA256 = (Get-FileHash -LiteralPath $packagePath).Hash
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'preview-provenance.json') -Encoding utf8
Write-Output "Verified preview identity: $packagePath"
