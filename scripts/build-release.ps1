param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish\win-x64'
$installerOutput = Join-Path $artifacts 'installer'
$localDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$artifactsFull = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\'
$publishFull = [IO.Path]::GetFullPath($publish)
if (-not $publishFull.StartsWith($artifactsFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a publish path outside $artifactsFull"
}
if (Test-Path -LiteralPath $publishFull) {
    Remove-Item -LiteralPath $publishFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish, $installerOutput | Out-Null

& $dotnet publish (Join-Path $root 'EdgeShelf\EdgeShelf.csproj') `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publish `
    --configfile (Join-Path $root 'NuGet.Config') `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$portable = Join-Path $artifacts "EdgeShelf-Portable-$Version.zip"
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Force }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $portable -CompressionLevel Optimal

if (-not $SkipInstaller) {
    $compiler = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $compiler) {
        throw 'Inno Setup 6 was not found. Install JRSoftware.InnoSetup or use -SkipInstaller.'
    }
    & $compiler "/DMyAppVersion=$Version" (Join-Path $root 'installer\EdgeShelf.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
}

$releaseFiles = @($portable)
if (-not $SkipInstaller) {
    $releaseFiles += Join-Path $installerOutput "EdgeShelf-Setup-$Version.exe"
}
$checksums = Join-Path $artifacts 'SHA256SUMS.txt'
$lines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($file))"
}
Set-Content -LiteralPath $checksums -Value $lines -Encoding ascii

Write-Host 'Release artifacts:'
$releaseFiles + $checksums | ForEach-Object { Write-Host "  $_" }
