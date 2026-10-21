$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $repo ('.tmp/local-release-tests/' + [Guid]::NewGuid().ToString('N'))
$store = Join-Path $root 'store'
$resolver = Join-Path $repo 'tools/resolve-local-release.ps1'
$utf8 = [Text.UTF8Encoding]::new($false)
$script:checks = 0
function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
}
function Write-Json($path, $value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 15) + "`n", $utf8)
}
function Hash($path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
function New-Package($name, $kind, $requires, $content) {
    $directory = Join-Path $store "$name/1.0.0"
    $payload = Join-Path $root "payloads/$name"
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'LICENSE.txt'), 'Fixture license', $utf8)
    [IO.File]::WriteAllText((Join-Path $payload 'THIRD-PARTY-NOTICES.txt'), 'Fixture notices', $utf8)
    foreach ($entry in $content.GetEnumerator()) {
        $path = Join-Path $payload $entry.Key
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, $entry.Value, $utf8)
    }
    $artifact = @{ application='application.zip'; profile='profile.zip'; plugin='plugin.zip' }[$kind]
    $zip = Join-Path $directory $artifact
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
    $manifest = @{
        schemaVersion=1
        package=@{name=$name; version='1.0.0'; kind=$kind; displayName=$name}
        artifact=@{file=$artifact; sha256=(Hash $zip); size=(Get-Item $zip).Length}
        source=@{revision=('a' * 40)}
        requires=@($requires); runtimeRequirements=@()
    }
    Write-Json (Join-Path $directory 'package-manifest.json') $manifest
    return [ordered]@{package=$name; version='1.0.0'; manifestSha256=(Hash (Join-Path $directory 'package-manifest.json')); destination='.'}
}
function Expect-Failure($label, $pattern, [scriptblock]$action) {
    try { & $action; throw "Unexpected success: $label" }
    catch {
        Assert ($_.Exception.Message -like $pattern) "${label}: unexpected error: $_"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$application = New-Package 'example.application' 'application' @() @{ 'app.txt'='Application fixture' }
$profile = New-Package 'example.profile' 'profile' @() @{ 'manifest.json'='{"profile":{"version":"1.0.0"}}' }
$profile.destination = 'profiles/example'
$profile.id = 'profile.example'
$profile.selectedByDefault = $true
$plugin = New-Package 'example.plugin' 'plugin' @('example.profile') @{ 'manifest.json'='{"info":{"bindingVersion":"1.0.0"}}' }
$plugin.destination = 'profiles/example/lib/plugin'
$plugin.id = 'plugin.example'
$plugin.parent = 'profile.example'
$plugin.selectedByDefault = $true
$bundle = @{schemaVersion=1; distribution=@{name='example.windows';version='1.0.0'}; application=$application; components=@($profile,$plugin)}
$bundlePath = Join-Path $root 'bundle.json'
Write-Json $bundlePath $bundle
$resolved = Join-Path $root 'resolved'
& $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot $resolved
Assert ((Hash $bundlePath) -eq (Hash (Join-Path $resolved 'bundle-manifest.json'))) 'Bundle bytes changed'
Assert (@(Get-ChildItem $resolved -File -Recurse).Count -eq 7) 'Unexpected resolved file set'
$single = Join-Path $root 'single'
& $resolver -PackageName $profile.package -Version '1.0.0' -ManifestSha256 $profile.manifestSha256 -LocalPackageRoot $store -DestinationRoot $single
Assert (Test-Path (Join-Path $single 'example.profile/1.0.0/profile.zip')) 'Standalone package not resolved'
Expect-Failure 'immutable destination' '*Destination already exists*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot $resolved
}
Expect-Failure 'source overlap' '*must not overlap*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot (Join-Path $store 'output')
}
Expect-Failure 'wrong manifest pin' '*manifest hash mismatch*' {
    & $resolver -PackageName $profile.package -Version '1.0.0' -ManifestSha256 ('0'*64) -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-pin')
}
Assert (-not (Test-Path (Join-Path $root 'bad-pin'))) 'Failure exposed destination'
$profileManifestPath = Join-Path $store 'example.profile/1.0.0/package-manifest.json'
$originalManifest = [IO.File]::ReadAllBytes($profileManifestPath)
$changedManifest = Get-Content $profileManifestPath -Raw | ConvertFrom-Json
$changedManifest.package.name = 'wrong.name'
Write-Json $profileManifestPath $changedManifest
Expect-Failure 'package identity' '*identity does not match*' {
    & $resolver -PackageName $profile.package -Version '1.0.0' -ManifestSha256 (Hash $profileManifestPath) -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-identity')
}
[IO.File]::WriteAllBytes($profileManifestPath,$originalManifest)
$changedManifest = Get-Content $profileManifestPath -Raw | ConvertFrom-Json
$changedManifest.schemaVersion = 99
Write-Json $profileManifestPath $changedManifest
Expect-Failure 'unsupported schema' '*identity does not match*' {
    & $resolver -PackageName $profile.package -Version '1.0.0' -ManifestSha256 (Hash $profileManifestPath) -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-schema')
}
[IO.File]::WriteAllBytes($profileManifestPath,$originalManifest)
Expect-Failure 'missing package' '*missing package-manifest.json*' {
    & $resolver -PackageName 'example.missing' -Version '1.0.0' -ManifestSha256 ('0'*64) -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'missing-package')
}
$zipPath = Join-Path $store 'example.profile/1.0.0/profile.zip'
$originalZip = [IO.File]::ReadAllBytes($zipPath)
$changedZip = [byte[]]$originalZip.Clone()
$changedZip[0] = $changedZip[0] -bxor 1
[IO.File]::WriteAllBytes($zipPath,$changedZip)
Expect-Failure 'same-size corrupt payload' '*payload integrity check failed*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-payload')
}
Assert (-not (Test-Path (Join-Path $root 'bad-payload'))) 'Corrupt bundle exposed destination'
[IO.File]::WriteAllBytes($zipPath,$originalZip)
$plugin.parent = 'profile.missing'
Write-Json $bundlePath $bundle
Expect-Failure 'missing parent' '*references missing parent*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-parent')
}
$plugin.parent = 'profile.example'
$profile.destination = '../escape'
Write-Json $bundlePath $bundle
Expect-Failure 'destination traversal' '*invalid path segment*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-traversal')
}
$profile.destination = 'profiles/example'
$plugin.package = 'example.profile'
$savedPluginHash = $plugin.manifestSha256
$plugin.manifestSha256 = $profile.manifestSha256
Write-Json $bundlePath $bundle
Expect-Failure 'duplicate package identity' '*each package identity only once*' {
    & $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot (Join-Path $root 'bad-duplicate')
}
$plugin.package = 'example.plugin'
$plugin.manifestSha256 = $savedPluginHash
Write-Json $bundlePath $bundle

# Exercise the existing staging/installer composition against the resolved layout.
& (Join-Path $repo 'tools/build-distribution-staging.ps1') -ResolvedBundleRoot $resolved -OutputRoot (Join-Path $root 'staging')
$distribution = Join-Path $root 'staging/example.windows/1.0.0'
Assert (Test-Path (Join-Path $distribution 'profiles/example/lib/plugin/manifest.json')) 'Plugin not staged'
Assert (Test-Path (Join-Path $distribution 'distribution/THIRD-PARTY-NOTICES.txt')) 'Aggregate notices missing'
& (Join-Path $repo 'tools/new-windows-installer-composition.ps1') -DistributionRoot $distribution -ComponentsFile (Join-Path $root 'components.iss') -FilesFile (Join-Path $root 'files.iss')
Assert ((Get-Content (Join-Path $root 'components.iss') -Raw).Contains('component_profile_example\component_plugin_example')) 'Installer hierarchy lost'
Assert ((Get-Content (Join-Path $root 'files.iss') -Raw).Contains('Source: "{#SourceRoot}\*"; DestDir: "{app}"')) 'Installer application mapping missing'

# A correctly hashed ZIP must still be rejected if its entries escape staging.
$pluginZip = Join-Path $store 'example.plugin/1.0.0/plugin.zip'
$archive = [IO.Compression.ZipFile]::Open($pluginZip,[IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $archive.CreateEntry('../escape.txt')
    $writer = [IO.StreamWriter]::new($entry.Open())
    try { $writer.Write('Must not escape staging') } finally { $writer.Dispose() }
}
finally { $archive.Dispose() }
$pluginManifestPath = Join-Path $store 'example.plugin/1.0.0/package-manifest.json'
$pluginManifest = Get-Content $pluginManifestPath -Raw | ConvertFrom-Json
$pluginManifest.artifact.sha256 = Hash $pluginZip
$pluginManifest.artifact.size = (Get-Item $pluginZip).Length
Write-Json $pluginManifestPath $pluginManifest
$plugin.manifestSha256 = Hash $pluginManifestPath
Write-Json $bundlePath $bundle
$unsafeResolved = Join-Path $root 'unsafe-resolved'
& $resolver -BundleManifest $bundlePath -LocalPackageRoot $store -DestinationRoot $unsafeResolved
Expect-Failure 'ZIP traversal' '*unsafe ZIP entry*' {
    & (Join-Path $repo 'tools/build-distribution-staging.ps1') -ResolvedBundleRoot $unsafeResolved -OutputRoot (Join-Path $root 'unsafe-staging')
}
Assert (-not (Test-Path (Join-Path $root 'unsafe-staging/example.windows/1.0.0'))) 'Unsafe archive exposed distribution'
Assert (@(Get-ChildItem (Join-Path $root 'unsafe-staging') -Directory -Force -Filter '.staging-*').Count -eq 0) 'Failed distribution staging leaked'
Assert (@(Get-ChildItem $root -Directory -Force -Filter '.local-release-*').Count -eq 0) 'Resolver staging leaked'
Write-Output "PASS: $script:checks local release checks, including distribution and installer composition."
Write-Output "Fixtures: $root"
