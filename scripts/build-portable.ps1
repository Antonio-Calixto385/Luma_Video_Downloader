$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$setupProject = Join-Path $projectRoot "Setup\MP3LocalDownloader.Setup.csproj"
$buildId = [Guid]::NewGuid().ToString("N")
$setupOutput = Join-Path $projectRoot "artifacts\portable-build-setup-$buildId"
$portableOutput = Join-Path $projectRoot "artifacts\portable"
$portableBuildOutput = Join-Path $projectRoot "artifacts\portable-build-app-$buildId"
$toolsStage = Join-Path $projectRoot "artifacts\portable-build-tools-$buildId"
$toolsArchive = Join-Path $projectRoot "artifacts\portable-build-tools-$buildId.zip"
$prepareLog = Join-Path $projectRoot "artifacts\portable-build-log-$buildId.txt"
$setupExecutable = Join-Path $setupOutput "LumaVideoDownloader-Setup.exe"
$portableBuildExecutable = Join-Path $portableBuildOutput "LumaVideoDownloader.exe"
$portableExecutable = Join-Path $portableOutput "LumaVideoDownloader.exe"
$requiredTools = @("deno.exe", "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe")
$toolCache = Join-Path $env:LOCALAPPDATA "MP3LocalDownloader\portable"
$dotnetRoot = Join-Path $projectRoot "artifacts\dotnet10"
$previousDotnetRoot = $env:DOTNET_ROOT
$previousPath = $env:PATH
if (Test-Path -LiteralPath (Join-Path $dotnetRoot "dotnet.exe")) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}

try {
    $legacyExecutable = Join-Path $portableOutput "MP3LocalDownloader.exe"
    if (Test-Path -LiteralPath $legacyExecutable) {
        Remove-Item -LiteralPath $legacyExecutable -Force
    }

    $cachedTools = $null
    if (Test-Path -LiteralPath $toolCache) {
        foreach ($candidate in (Get-ChildItem -LiteralPath $toolCache -Directory | Sort-Object LastWriteTimeUtc -Descending)) {
            $candidateTools = Join-Path $candidate.FullName "tools"
            $containsAllTools = $true
            foreach ($toolName in $requiredTools) {
                if (-not (Test-Path -LiteralPath (Join-Path $candidateTools $toolName))) {
                    $containsAllTools = $false
                    break
                }
            }
            if ($containsAllTools) {
                $cachedTools = $candidateTools
                break
            }
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    if ($cachedTools) {
        $archiveStream = [System.IO.File]::Open($toolsArchive, [System.IO.FileMode]::CreateNew)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new(
                $archiveStream,
                [System.IO.Compression.ZipArchiveMode]::Create,
                $true)
            try {
                foreach ($toolName in $requiredTools) {
                    $sourcePath = Join-Path $cachedTools $toolName
                    $entry = $archive.CreateEntry($toolName, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                    $inputStream = [System.IO.File]::OpenRead($sourcePath)
                    $entryStream = $entry.Open()
                    try {
                        $inputStream.CopyTo($entryStream)
                    }
                    finally {
                        $entryStream.Dispose()
                        $inputStream.Dispose()
                    }
                }
            }
            finally {
                $archive.Dispose()
            }
        }
        finally {
            $archiveStream.Dispose()
        }
        Write-Output "Reutilizando ferramentas já verificadas em: $cachedTools"
    }
    else {
        dotnet publish $setupProject --configuration Release --runtime win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -o $setupOutput
        if ($LASTEXITCODE -ne 0) {
            throw "A publicação auxiliar do instalador falhou."
        }

        $setupCheck = Start-Process -FilePath $setupExecutable -ArgumentList "--check-package" `
            -Wait -PassThru -WindowStyle Hidden
        if ($setupCheck.ExitCode -ne 0) {
            throw "O pacote auxiliar não contém o executável LumaVideoDownloader esperado."
        }

        $prepareArguments = '--prepare-portable-bundle "{0}" "{1}"' -f $toolsStage, $prepareLog
        $prepareProcess = Start-Process -FilePath $setupExecutable -ArgumentList $prepareArguments `
            -Wait -PassThru -WindowStyle Hidden
        if ($prepareProcess.ExitCode -ne 0) {
            $detail = if (Test-Path -LiteralPath $prepareLog) { Get-Content -LiteralPath $prepareLog -Raw } else { "Sem detalhes adicionais." }
            throw "Não foi possível baixar e validar as ferramentas do pacote portátil. $detail"
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            (Join-Path $toolsStage "tools"),
            $toolsArchive,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)
    }

    dotnet publish (Join-Path $projectRoot "MP3LocalDownloader.csproj") `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
        "-p:PortableToolsArchive=$toolsArchive" -o $portableBuildOutput
    if ($LASTEXITCODE -ne 0) {
        throw "A publicação do executável portátil falhou."
    }

    $bundleCheck = Start-Process -FilePath $portableBuildExecutable -ArgumentList "--check-bundle" `
        -Wait -PassThru -WindowStyle Hidden
    if ($bundleCheck.ExitCode -ne 0) {
        throw "A validação do pacote portátil falhou."
    }

    New-Item -ItemType Directory -Force $portableOutput | Out-Null
    try {
        Copy-Item -LiteralPath $portableBuildExecutable -Destination $portableExecutable -Force
        Write-Output "Executável portátil criado e validado em: $portableExecutable"
    }
    catch {
        $fallbackOutput = Join-Path $projectRoot "artifacts\portable-update-$buildId"
        Move-Item -LiteralPath $portableBuildOutput -Destination $fallbackOutput
        Write-Warning "O executável portátil anterior está em uso. A nova versão foi salva em: $(Join-Path $fallbackOutput 'LumaVideoDownloader.exe'). Feche o aplicativo e execute novamente o script para atualizar o caminho padrão."
    }
}
finally {
    foreach ($path in @($setupOutput, $toolsStage, $portableBuildOutput)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $toolsArchive) {
        Remove-Item -LiteralPath $toolsArchive -Force
    }
    if (Test-Path -LiteralPath $prepareLog) {
        Remove-Item -LiteralPath $prepareLog -Force
    }
    $env:DOTNET_ROOT = $previousDotnetRoot
    $env:PATH = $previousPath
}
