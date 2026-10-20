param([string]$Executable, [string]$OutputDirectory = 'artifacts/validation')
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$fixtureRoot = Join-Path $env:TEMP ('DiskStudio-UI-' + [Guid]::NewGuid().ToString('N'))
try {
    $categories = @(
        @{ Name = 'Videos'; Extension = '.mp4'; Bytes = 180000000 },
        @{ Name = 'Photos'; Extension = '.jpg'; Bytes = 34000000 },
        @{ Name = 'Projects'; Extension = '.zip'; Bytes = 97000000 },
        @{ Name = 'Documents'; Extension = '.pdf'; Bytes = 7000000 }
    )
    foreach ($category in $categories) {
        $folder = Join-Path $fixtureRoot $category.Name
        New-Item -ItemType Directory -Force $folder | Out-Null
        for ($i = 1; $i -le 12; $i++) {
            $stream = [IO.File]::Create((Join-Path $folder ('Sample-' + $i + $category.Extension)))
            try { $stream.SetLength([long]($category.Bytes / $i)) } finally { $stream.Dispose() }
        }
    }
    $argumentText = '--smoke-test "' + $fixtureRoot + '" "' + $OutputDirectory + '"'
    $stderrFile = Join-Path $OutputDirectory 'ui-stderr.txt'
    $process = Start-Process -FilePath $Executable -ArgumentList $argumentText -PassThru -RedirectStandardError $stderrFile
    if (-not $process.WaitForExit(90000)) { $process.Kill(); throw 'UI smoke test timed out.' }
    if ($process.ExitCode -ne 0) {
        if (Test-Path $stderrFile) { Get-Content $stderrFile }
        Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddMinutes(-3) } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error' } | Select-Object -ExpandProperty Message
        $errorFile = Join-Path $OutputDirectory 'ui-error.txt'
        if (Test-Path $errorFile) { Get-Content $errorFile }
        throw "UI smoke test failed: $($process.ExitCode)"
    }
    if (-not (Test-Path (Join-Path $OutputDirectory 'ui-smoke.txt'))) { throw 'UI smoke result missing.' }
    Get-Content (Join-Path $OutputDirectory 'ui-smoke.txt')
} finally {
    if (Test-Path $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
