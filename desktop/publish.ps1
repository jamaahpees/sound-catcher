param(
    [switch]$Standalone
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'SoundCatcher\SoundCatcher.csproj'
$output = Join-Path $PSScriptRoot 'dist'

if (-not (Test-Path $project)) {
    throw "Proyek tidak ditemukan: $project"
}

if ($Standalone) {
    Write-Host 'Membangun versi mandiri (tanpa .NET terpasang)...'
    dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $output
}
else {
    Write-Host 'Membangun versi framework-dependent (butuh .NET 8 Desktop Runtime)...'
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $output
}

if ($LASTEXITCODE -ne 0) {
    throw "Build gagal dengan kode $LASTEXITCODE"
}

Write-Host "Selesai: $output\SoundCatcher.exe"
