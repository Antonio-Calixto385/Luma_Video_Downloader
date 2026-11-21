$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$setupProject = Join-Path $projectRoot "Setup\MP3LocalDownloader.Setup.csproj"
$publishDirectory = Join-Path $projectRoot "artifacts\installer"
$dotnetRoot = Join-Path $projectRoot "artifacts\dotnet10"
$previousDotnetRoot = $env:DOTNET_ROOT
$previousPath = $env:PATH
if (Test-Path -LiteralPath (Join-Path $dotnetRoot "dotnet.exe")) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}

try {
    dotnet publish $setupProject --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

    if ($LASTEXITCODE -ne 0) {
        throw "A publicação do instalador falhou."
    }

    Write-Output "Instalador criado em: $publishDirectory\LumaVideoDownloader-Setup.exe"
}
finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:PATH = $previousPath
}
