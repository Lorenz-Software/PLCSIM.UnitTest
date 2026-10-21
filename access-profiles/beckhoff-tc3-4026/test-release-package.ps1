[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$directory = [IO.Path]::GetFullPath($PackageDirectory)
$project = Join-Path $PSScriptRoot 'src/PLCSIM.UnitTest.AccessProfiles.Beckhoff'
$temporary = Join-Path $repo ('.tmp/beckhoff-package-tests/' + [Guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $directory 'package-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$component = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'distribution/bundle-component.json') -Raw | ConvertFrom-Json
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

# Test the builder guard itself without invoking a build or changing its payload.
$tokens = $null
$errors = $null
$builder = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repo 'tools/build-release-package.ps1'), [ref]$tokens, [ref]$errors)
Assert ($errors.Count -eq 0) 'Package builder has a PowerShell syntax error.'
$guard = $builder.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-AllowedReleaseFile'
}, $false).Body.GetScriptBlock()
foreach ($forbidden in @('Microsoft.VisualStudio.Interop.dll', 'nested/TCatSysManagerLib.dll', 'Siemens.Engineering.dll')) {
    $rejected = $false
    try { & $guard $forbidden 'profile' }
    catch {
        Assert ($_.Exception.Message -like '*must not ship vendor artifact*') "Unexpected guard failure: $_"
        $rejected = $true
    }
    Assert $rejected "Builder accepted installed vendor file: $forbidden"
}
& $guard 'TwinCAT.Ads.dll' 'profile'

Assert ($manifest.package.name -eq $component.package) 'Package identity does not match the bundle component.'
Assert ($manifest.package.version -eq $component.version) 'Package version does not match the bundle component.'
Assert ($manifest.package.kind -eq 'profile' -and $manifest.artifact.file -eq 'profile.zip') 'Expected an integrated profile package.'
Assert (@($manifest.requires).Count -eq 0) 'The integrated profile must not depend on binding packages.'
Assert ($component.id -eq 'profile.beckhoff-tc3-4026' -and
    $component.destination -eq 'profiles/beckhoff-tc3-4026' -and
    $component.selectedByDefault -eq $true -and
    $null -eq $component.PSObject.Properties['parent']) 'Unexpected installer component selection or hierarchy.'

# Exercise the same integrity validation used by local bundle resolution.
$manifestHash = Hash $manifestPath
& (Join-Path $repo 'tools/resolve-local-release.ps1') `
    -PackageName $component.package -Version $component.version -ManifestSha256 $manifestHash `
    -LocalPackageRoot (Split-Path (Split-Path $directory)) -DestinationRoot (Join-Path $temporary 'resolved')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $directory 'profile.zip'
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })
    foreach ($name in $names) {
        Assert ($name -notmatch '(^/|:|\\|(^|/)\.\.(/|$))') "Unsafe archive entry: $name"
        Assert ($name -notmatch '(?i)(^|/)(bin|obj|tests|testresults|logs|\.tmp|workers|bindings)(/|$)') "Non-production entry: $name"
        Assert ($name -notmatch '(?i)(\.pdb$|\.log$|\.trx$|\.cache$|\.user$|\.Tests?\.|\.Fake\.)') "Non-production file: $name"
        Assert ($name -notmatch '(?i)(^|/)(Microsoft.VisualStudio.Interop|TCatSysManagerLib|Siemens.Engineering|Siemens.Simatic.Simulation.Runtime.Api.x64)\.dll$') "Installed vendor binary redistributed: $name"
        Assert ($name -notmatch '(?i)(\.tlb$|(^|/)[^/]*TCatSysManager[^/]*\.dll$)') "Beckhoff interface metadata redistributed: $name"
    }
    foreach ($required in @('manifest.json', 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt',
        'PLCSIM.UnitTest.AccessProfiles.Beckhoff.dll', 'PLCSIM.UnitTest.AccessProfiles.Beckhoff.deps.json',
        'docs/configuration.md', 'docs/usage.md', 'docs/ci-execution.md',
        'docs/examples/managed-solution.json', 'docs/examples/managed-archive.json',
        'runtimes/win/lib/net8.0/System.Diagnostics.EventLog.dll',
        'runtimes/win/lib/net8.0/System.Diagnostics.EventLog.Messages.dll',
        'runtimes/win/lib/net8.0/System.ServiceProcess.ServiceController.dll')) {
        Assert ($names -ccontains $required) "Missing package file: $required"
    }
} finally { $archive.Dispose() }
$payload = Join-Path $temporary 'payload'
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $payload)
$runtime = Get-Content -LiteralPath (Join-Path $payload 'manifest.json') -Raw | ConvertFrom-Json
Assert ($runtime.profile.id -eq 'beckhoff-tc3-4026' -and
    $runtime.profile.version -eq $manifest.package.version -and
    $runtime.profile.contractVersion -eq '2.0.0') 'Runtime manifest compatibility mismatch.'
foreach ($assembly in @('PLCSIM.UnitTest.Common.dll', 'PLCSIM.UnitTest.Common.Application.dll')) {
    Assert ((Hash (Join-Path $payload $assembly)) -eq (Hash (Join-Path $repo "lib/contracts/$assembly"))) "Shared contract mismatch: $assembly"
}

$lock = Get-Content -LiteralPath (Join-Path $project 'packages.lock.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath (Join-Path $project 'obj/project.assets.json') -Raw | ConvertFrom-Json
$libraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
$locked = @($lock.dependencies.'net8.0'.PSObject.Properties)
Assert ($libraries.Count -eq 13 -and $locked.Count -eq 13) 'Dependency closure changed; repeat the redistribution review.'
foreach ($entry in $locked) {
    $identity = $entry.Name + '/' + $entry.Value.resolved
    $library = $libraries | Where-Object { $_.Name -eq $identity }
    Assert ($null -ne $library -and $library.Value.sha512 -eq $entry.Value.contentHash) "Resolved dependency differs from lock: $identity"
}

$noticePath = Join-Path $payload 'THIRD-PARTY-NOTICES.txt'
$notices = Get-Content -LiteralPath $noticePath -Raw
foreach ($entry in $locked) {
    Assert ($notices.Contains($entry.Name + ' ' + $entry.Value.resolved)) "Missing dependency attribution: $($entry.Name)"
}
foreach ($license in @('beckhoff-mit.txt', 'dotnet-mit.txt')) {
    $text = Get-Content -LiteralPath (Join-Path $repo "distribution/licenses/texts/$license") -Raw
    Assert ($notices.Replace("`r`n", "`n").Contains($text.Replace("`r`n", "`n").Trim())) "Required license text missing: $license"
}
Assert (-not $notices.Contains('Apache License')) 'Unrelated license copied into the profile notice.'
& (Join-Path $repo 'tools/new-third-party-notices.ps1') `
    -AssetFiles @((Join-Path $project 'obj/project.assets.json')) -PayloadRoot $payload `
    -OutputPath (Join-Path $temporary 'regenerated-notices.txt')
Assert ((Hash $noticePath) -eq (Hash (Join-Path $temporary 'regenerated-notices.txt'))) 'Package notices do not match the reviewed payload.'
Assert ((Hash (Join-Path $payload 'LICENSE.txt')) -eq (Hash (Join-Path $repo 'LICENSE'))) 'Project license missing or altered.'

# A component fragment is not a release pin until the actual manifest hash is added.
$component | Add-Member -NotePropertyName manifestSha256 -NotePropertyValue $manifestHash
[IO.File]::WriteAllText((Join-Path $temporary 'bundle-component.resolved.json'),
    ($component | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "PASS: integrity, manifests, contract hashes, $($names.Count) payload entries, 13 locked/reviewed dependencies, Windows assets, notices and bundle component."
Write-Output "Evidence: $temporary"
