param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $repoRoot "src\FeedServer\FeedServer.slnx"
$project = Join-Path $repoRoot "src\FeedServer\FeedServer\FeedServer.csproj"

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

$publishDir = Join-Path $OutputRoot "FeedServer-$Runtime"
$zipPath = "$publishDir.zip"

dotnet test $solution -c $Configuration

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $project -c $Configuration -r $Runtime --self-contained false -o $publishDir /p:PublishSingleFile=true

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Published $publishDir"
Write-Host "Packaged $zipPath"
