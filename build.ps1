[CmdletBinding()]
param([switch]$Package, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$suiteVersion = (Get-Content -LiteralPath .\VERSION -Raw).Trim()
dotnet format .\Kill.slnx --verify-no-changes --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Format verification failed.' }
dotnet build .\Kill.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (-not $SkipTests) {
    dotnet run --project .\Kill.SelfTest\Kill.SelfTest.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Management self-test failed.' }
    dotnet run --project .\BehaviorMonitor.SelfTest\ProcessSentinel.SelfTest.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Behavior self-test failed.' }
    New-Item -ItemType Directory -Path .\artifacts -Force | Out-Null
    $suiteReport = Join-Path $PSScriptRoot 'artifacts\suite-ui-results.txt'
    $suiteProcess = Start-Process -FilePath .\Releases\Kill.exe -ArgumentList '--verify-ui',('"' + $suiteReport + '"') -WindowStyle Hidden -PassThru
    if (-not $suiteProcess.WaitForExit(60000)) { throw 'UI verification timed out.' }
    Get-Content -LiteralPath $suiteReport
    if ($suiteProcess.ExitCode -ne 0) { throw 'UI verification failed.' }
}
if (-not $Package) { Write-Output "Built: $PSScriptRoot\Releases\Kill.exe"; return }
$suitePublishDirectory = Join-Path $PSScriptRoot "Releases\Kill-portable-win-x64-v$suiteVersion"
$suitePublishOptions = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $suitePublishDirectory)
dotnet publish .\Kill\Kill.csproj @suitePublishOptions
if ($LASTEXITCODE -ne 0) { throw 'Main program publish failed.' }
dotnet publish .\BehaviorMonitor\Collector\ProcessSentinel.Collector.csproj @suitePublishOptions
if ($LASTEXITCODE -ne 0) { throw 'Collector publish failed.' }
Copy-Item -LiteralPath .\README.md -Destination $suitePublishDirectory -Force
New-Item -ItemType Directory -Path (Join-Path $suitePublishDirectory 'docs') -Force | Out-Null
Copy-Item -LiteralPath .\docs\BEHAVIOR_MONITOR.md -Destination (Join-Path $suitePublishDirectory 'docs') -Force
foreach ($suiteRequiredFile in @('Kill.exe', "Kill-v$suiteVersion.exe", 'ProcessSentinel.Collector.exe', 'VERSION', 'LICENSE', 'THIRD_PARTY.md', 'third-party\dotnet-runtime-10.0.11-THIRD-PARTY-NOTICES.txt', 'third-party\wpf-10.0.11-THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $suitePublishDirectory $suiteRequiredFile))) { throw "Missing package file: $suiteRequiredFile" }
}
if ((Get-FileHash -LiteralPath (Join-Path $suitePublishDirectory 'Kill.exe')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $suitePublishDirectory "Kill-v$suiteVersion.exe")).Hash) { throw 'Versioned entry does not match Kill.exe.' }
$suiteZipPath = Join-Path $PSScriptRoot "Releases\Kill-portable-win-x64-v$suiteVersion.zip"
Compress-Archive -LiteralPath $suitePublishDirectory -DestinationPath $suiteZipPath -Force
Write-Output "Portable app: $suitePublishDirectory\Kill.exe"
Write-Output "Archive: $suiteZipPath"
