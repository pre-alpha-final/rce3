param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = ""
)

$ErrorActionPreference = "Stop"

if ($Runtime -notmatch '^[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*$') {
    throw "Runtime must be a runtime identifier without path separators."
}

if ($Configuration -notmatch '^[A-Za-z0-9]+(?:[.-][A-Za-z0-9]+)*$') {
    throw "Configuration must be a name without path separators."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$solution = Join-Path $repoRoot "src\FeedServer\FeedServer.slnx"
$project = Join-Path $repoRoot "src\FeedServer\FeedServer\FeedServer.csproj"

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$outputRootPathRoot = [IO.Path]::GetPathRoot($OutputRoot)
if (-not [string]::Equals($OutputRoot, $outputRootPathRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $OutputRoot = $OutputRoot.TrimEnd([char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar))
}

$publishDir = [IO.Path]::GetFullPath((Join-Path $OutputRoot "FeedServer-$Runtime"))
if (-not [string]::Equals(
    [IO.Path]::GetDirectoryName($publishDir),
    $OutputRoot,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish directory must be a direct child of the output root."
}

$zipPath = "$publishDir.zip"

dotnet test $solution -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $project -c $Configuration -r $Runtime --self-contained false -o $publishDir /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Published $publishDir"
Write-Host "Packaged $zipPath"
