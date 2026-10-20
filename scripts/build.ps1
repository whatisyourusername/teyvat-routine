param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src\TeyvatRoutine\TeyvatRoutine.csproj'
$artifacts = Join-Path $repository 'artifacts'
$output = Join-Path $artifacts 'TeyvatRoutine'

if ($env:OS -ne 'Windows_NT') { throw 'This WPF application must be built on Windows.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null

& dotnet publish $project -c Release --self-contained false `
    -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if (-not $SkipTests) {
    $resultPath = Join-Path $artifacts 'self-test.txt'
    if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
    $runner = Join-Path $output 'TeyvatRoutine.dll'
    & dotnet $runner --self-test $resultPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw 'Self-tests failed or did not produce a result.'
    }
    $resultText = Get-Content -LiteralPath $resultPath -Raw
    $summary = [regex]::Match($resultText, '(?m)^(\d+) / (\d+) tests passed')
    if (-not $summary.Success -or $summary.Groups[1].Value -ne $summary.Groups[2].Value) {
        throw 'Self-tests did not all pass. See artifacts/self-test.txt.'
    }
    Write-Output $summary.Value
}

# Preserve an existing build's user recordings; only seed missing profile files.
$seedRoot = Join-Path $repository 'portable\data'
foreach ($relativeProfile in @('profile.json', 'profiles\1920x1080.json', 'profiles\3840x2160.json')) {
    $destination = Join-Path (Join-Path $output 'data') $relativeProfile
    if (-not (Test-Path -LiteralPath $destination)) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath (Join-Path $seedRoot $relativeProfile) -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $repository 'docs\사용법.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $repository 'portable\app-icon.png') -Destination $output -Force
Write-Output "Build ready: $output"
