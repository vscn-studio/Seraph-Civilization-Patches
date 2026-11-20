param(
    [string]$Configuration = 'Release',
    [string]$GamePath = 'E:/vintagestory/Vintagestory',
    [switch]$RunTests,
    [string]$DeployPath = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'
if (-not $GamePath) { $GamePath = 'E:/vintagestory/Vintagestory' }
dotnet build "$PSScriptRoot/SeraphCivilizationPatches.csproj" -c $Configuration "-p:VINTAGE_STORY=$GamePath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if ($RunTests) {
    dotnet run --project "$PSScriptRoot/tests/Checks.csproj" -c $Configuration "-p:GamePath=$GamePath" -- $PSScriptRoot $GamePath core
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
}
$metadata = Get-Content -LiteralPath "$PSScriptRoot/modinfo.json" -Raw | ConvertFrom-Json
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$archive = Join-Path $artifacts "$($metadata.modid)_$($metadata.version).zip"
$files = [ordered]@{
    'vsscpmod.dll' = Join-Path $PSScriptRoot "bin/$Configuration/net10.0/vsscpmod.dll"
    'modinfo.json' = Join-Path $PSScriptRoot 'modinfo.json'
    'LICENSE' = Join-Path $PSScriptRoot 'LICENSE'
}
$documentationExtensions = @('.md', '.markdown', '.mdx', '.rst', '.adoc', '.txt', '.pdf', '.doc', '.docx')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'assets') -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\', '/')
    if ($file.Extension.ToLowerInvariant() -in $documentationExtensions -or
        $file.BaseName -match '^(readme|changelog|changes|contributing)$' -or
        $relative -match '(^|/)(docs?|documentation)(/|$)') { continue }
    $files[$relative] = $file.FullName
}
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Create($archive)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $inputStream = [IO.File]::OpenRead($files[$name])
        $outputStream = $entry.Open()
        try { $inputStream.CopyTo($outputStream) }
        finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
} finally { $zip.Dispose(); $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($check.Entries.Count -ne $files.Count) { throw 'Package entry count mismatch.' }
    foreach ($name in $files.Keys) {
        $entry = $check.GetEntry($name)
        if (-not $entry -or $entry.FullName.Contains('\')) { throw "Invalid package entry: $name" }
        $entryStream = $entry.Open()
        $sourceStream = [IO.File]::OpenRead($files[$name])
        try {
            $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream))
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceStream))
            if ($actual -ne $expected) { throw "Package content mismatch: $name" }
        } finally { $entryStream.Dispose(); $sourceStream.Dispose() }
    }
} finally { $check.Dispose() }
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n")
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
if (-not $NoDeploy) {
    & "$PSScriptRoot/deploy.ps1" -Archive $archive -DeployPath $DeployPath
}
