$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$generator = Join-Path $repo 'tools/new-third-party-notices.ps1'
$root = Join-Path $repo ('.tmp/third-party-notice-tests/' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $root 'packages/example/1.0.0'
New-Item -ItemType Directory -Path $package -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-Text($path, $text) { [IO.File]::WriteAllText($path, $text, $utf8) }
function Assert($condition, $message) { if (-not $condition) { throw $message } }
Write-Text (Join-Path $root 'license.txt') 'REQUIRED LICENSE TEXT'
Write-Text (Join-Path $root 'selected.txt') 'EXPLICIT ADDITIONAL TEXT'
Write-Text (Join-Path $package 'NOTICE.txt') 'UNSELECTED NOTICE MUST NOT BE COPIED'
Write-Text (Join-Path $package 'example.nuspec') '<package><metadata><copyright>Copyright Example Owner</copyright></metadata></package>'
$inventory = @{
 schemaVersion = 1
 licenses = @(@{ id='Example'; displayName='Example'; copyright=''; textFile='license.txt'; includeText=$true; reason='Test obligation' })
 packages = @(@{ id='Example'; versions=@('1.0.0'); license='Example'; projectUrl='https://example.org'; additionalTextFiles=@('selected.txt') })
 installerComponents = @()
}
$assets = @{
 libraries = @{ 'Example/1.0.0' = @{ type='package'; path='example/1.0.0'; files=@() } }
 packageFolders = @{ (Join-Path $root 'packages') = @{} }
}
$inventoryPath = Join-Path $root 'inventory.json'
$assetsPath = Join-Path $root 'assets.json'
$output = Join-Path $root 'notices.txt'
Write-Text $assetsPath ($assets | ConvertTo-Json -Depth 10)
function Generate {
 Write-Text $inventoryPath ($inventory | ConvertTo-Json -Depth 10)
 & $generator -AssetFiles @($assetsPath) -InventoryPath $inventoryPath -OutputPath $output
 return [IO.File]::ReadAllText($output)
}
$first = Generate
Assert ($first.Contains('REQUIRED LICENSE TEXT')) 'Required license missing'
Assert ($first.Contains('EXPLICIT ADDITIONAL TEXT')) 'Explicit additional text missing'
Assert ($first.Contains('Copyright Example Owner')) 'Component copyright missing'
Assert (-not $first.Contains('UNSELECTED NOTICE')) 'Unselected notice copied'
Assert ($first -ceq (Generate)) 'Output not deterministic'
$inventory.licenses[0].includeText = $false
$inventory.packages[0].additionalTextFiles = @()
$second = Generate
Assert (-not $second.Contains('REQUIRED LICENSE TEXT')) 'Excluded license copied'
Assert (-not $second.Contains('EXPLICIT ADDITIONAL TEXT')) 'Excluded additional text copied'
function Expect-Failure($pattern) {
 try { Generate | Out-Null } catch {
  Assert ($_.Exception.Message -like $pattern) "Unexpected failure: $_"
  return
 }
 throw "Expected failure: $pattern"
}
$inventory.packages[0].additionalTextFiles = @('missing.txt')
Expect-Failure '*Required additional text*missing*'
$inventory.packages[0].Remove('additionalTextFiles')
Expect-Failure '*no explicit additional-text decision*'
$inventory.packages[0].additionalTextFiles = @()
$inventory.packages[0].versions = @('2.0.0')
Expect-Failure '*not classified*'
$inventory.packages[0].versions = @('1.0.0')
$inventory.licenses[0].Remove('includeText')
try { Generate | Out-Null; throw 'Missing decision was accepted' } catch {
 Assert ($_.Exception.Message -notlike '*Missing decision was accepted*') 'Missing decision accepted'
}
$inventory.licenses[0].includeText = $true
$inventory.packages[0].additionalTextFiles = @()
Write-Text (Join-Path $package 'example.dll') 'Fixture binary content'
$assets.libraries['Example/1.0.0'].files = @('example.dll')
Write-Text $assetsPath ($assets | ConvertTo-Json -Depth 10)
Write-Text $inventoryPath ($inventory | ConvertTo-Json -Depth 10)
$payload = Join-Path $root 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
Copy-Item (Join-Path $package 'example.dll') $payload
& $generator -AssetFiles @($assetsPath) -InventoryPath $inventoryPath -PayloadRoot $payload -OutputPath $output
Assert ([IO.File]::ReadAllText($output).Contains('Example 1.0.0')) 'Shipped dependency missing'
Write-Text (Join-Path $payload 'unclassified.dll') 'Different binary content'
try {
 & $generator -AssetFiles @($assetsPath) -InventoryPath $inventoryPath -PayloadRoot $payload -OutputPath $output
 throw 'Unknown binary was accepted'
} catch { Assert ($_.Exception.Message -like '*does not match a reviewed NuGet dependency*') "Unexpected binary-validation failure: $_" }

$bundleRoot = Join-Path $root 'bundle'
New-Item -ItemType Directory -Path (Join-Path $bundleRoot 'distribution') -Force | Out-Null
Write-Text (Join-Path $bundleRoot 'LICENSE.txt') 'Package own license'
Write-Text (Join-Path $bundleRoot 'THIRD-PARTY-NOTICES.txt') 'INDEPENDENT PACKAGE NOTICE'
$bundle = @{ application=@{package='independent'; version='1.2.3'; destination='.'}; components=@() }
Write-Text (Join-Path $bundleRoot 'distribution/bundle-manifest.json') ($bundle | ConvertTo-Json -Depth 8)
$bundleGenerator = Join-Path $repo 'tools/new-bundle-third-party-notices.ps1'
& $bundleGenerator -DistributionRoot $bundleRoot -OutputPath $output
Assert ([IO.File]::ReadAllText($output).Contains('INDEPENDENT PACKAGE NOTICE')) 'Independent package notice lost'
Assert (-not [IO.File]::ReadAllText($output).Contains('Except where otherwise noted')) 'Unrequired Inno license copied'
Write-Text (Join-Path $bundleRoot 'THIRD-PARTY-NOTICES.txt') ''
try {
 & $bundleGenerator -DistributionRoot $bundleRoot -OutputPath $output
 throw 'Empty notice accepted'
} catch { Assert ($_.Exception.Message -like '*missing a nonempty*') "Unexpected empty-notice failure: $_" }

Write-Host 'PASS: explicit inclusion/exclusion, copyright, no wildcard copying, deterministic output, missing text/decision and unknown version.'
