param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [switch]$Test
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if ($Test) {
    dotnet run --project tests/DiskStudio.Tests -c Release -- (Join-Path $PSScriptRoot 'artifacts/validation')
    if ($LASTEXITCODE -ne 0) { throw 'Core validation failed.' }
}
dotnet publish src/DiskStudio/DiskStudio.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "artifacts/$Runtime"
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Host "Ready: artifacts/$Runtime/DiskStudio.exe"
