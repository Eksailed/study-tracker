param([switch]$Lan)

$ErrorActionPreference = 'Stop'
$localDotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
$dotnetCommand = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
& $dotnetCommand run --project (Join-Path $PSScriptRoot 'StudyTracker.csproj') --no-launch-profile -- --migrate
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Lan) {
    & $dotnetCommand run --project (Join-Path $PSScriptRoot 'StudyTracker.csproj') --no-launch-profile -- --urls 'http://0.0.0.0:5184' --AllowedHosts '*'
} else {
    & $dotnetCommand run --project (Join-Path $PSScriptRoot 'StudyTracker.csproj') --launch-profile StudyTracker
}
exit $LASTEXITCODE
