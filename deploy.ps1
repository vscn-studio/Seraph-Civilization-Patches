param(
    [Parameter(Mandatory)][string]$Archive,
    [string]$DeployPath = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods'
)
$ErrorActionPreference = 'Stop'
# A local server and its client share the assembly loader. Replacing a ZIP
# between their startup stages loads two assembly versions in one process.
$running = Get-Process -Name 'Vintagestory*' -ErrorAction SilentlyContinue
if ($running) { throw 'Exit Vintage Story and its server completely before deploying. Build with -NoDeploy while the game is running.' }
Add-Type -AssemblyName System.IO.Compression
function Get-PackageId([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifest = $zip.GetEntry('modinfo.json')
        if (-not $manifest) { return $null }
        $reader = [IO.StreamReader]::new($manifest.Open())
        try { return ($reader.ReadToEnd() | ConvertFrom-Json).modid }
        finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
}
$source = (Resolve-Path -LiteralPath $Archive).Path
if ((Get-PackageId $source) -ne 'vsscpmod') { throw 'Only vsscpmod packages may be deployed by this script.' }
$targetDirectory = [IO.Path]::GetFullPath($DeployPath)
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
$target = Join-Path $targetDirectory ([IO.Path]::GetFileName($source))
if ($source.Equals($target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source archive must be outside the deployment directory.' }
$oldPackages = @(Get-ChildItem -LiteralPath $targetDirectory -Filter '*.zip' -File | Where-Object {
    (Get-PackageId $_.FullName) -eq 'vsscpmod'
})
if ((Test-Path -LiteralPath $target) -and $target -notin $oldPackages.FullName) { throw 'Target filename belongs to another mod.' }
$backup = Join-Path $PSScriptRoot ('artifacts/deploy-backups/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-ffff'))
foreach ($old in $oldPackages) {
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $saved = Join-Path $backup $old.Name
    Copy-Item -LiteralPath $old.FullName -Destination $saved
    if ((Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $old.FullName).Hash) { throw 'Backup hash mismatch.' }
}
# Verify a staged copy before changing any installed package.
$staged = Join-Path $targetDirectory ('.vsscpmod-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Copy-Item -LiteralPath $source -Destination $staged
    $expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $expected) { throw 'Staged package hash mismatch.' }
    Move-Item -LiteralPath $staged -Destination $target -Force
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw 'Deployed package hash mismatch.' }
    foreach ($old in $oldPackages) {
        if (-not $old.FullName.Equals($target, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $old.FullName }
    }
} finally {
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged }
}
Write-Output "Deployed and verified: $target"
Write-Output "SHA256: $expected"
if ($oldPackages.Count) { Write-Output "Backup: $backup" }
