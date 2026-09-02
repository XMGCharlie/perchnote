$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $projectRoot 'dist'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path $compiler)) {
    $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $compiler)) {
    throw 'Windows C# compiler was not found. Enable .NET Framework 4.x.'
}

$frameworkRoot = 'C:\Windows\Microsoft.NET\assembly'
function Find-FrameworkAssembly([string]$name) {
    $match = Get-ChildItem $frameworkRoot -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'v4\.0_' } |
        Select-Object -First 1
    if (-not $match) { throw "Framework assembly was not found: $name" }
    return $match.FullName
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$references = @(
    (Find-FrameworkAssembly 'PresentationFramework.dll'),
    (Find-FrameworkAssembly 'PresentationCore.dll'),
    (Find-FrameworkAssembly 'WindowsBase.dll'),
    (Find-FrameworkAssembly 'System.Xaml.dll')
)

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    "/out:$outputDir\PerchNote.exe"
)
$arguments += $references | ForEach-Object { "/reference:$_" }
$arguments += (Join-Path $projectRoot 'src\PerchNote.cs')

& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host "Build succeeded: $outputDir\PerchNote.exe" -ForegroundColor Green
