[CmdletBinding()]
param(
    [AllowEmptyString()]
    [string]$ExpectedVersion = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryRoot
try {
    [xml]$project = Get-Content -LiteralPath 'NimCap.Screenshot.csproj' -Raw
    $projectVersion = [string]$project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($projectVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'The project Version must use major.minor.patch format.'
    }
    if ($ExpectedVersion -and $ExpectedVersion -cne $projectVersion) {
        throw "Expected version $ExpectedVersion does not match project version $projectVersion."
    }

    # An isolated output directory prevents local models/settings or old binaries
    # from leaking into the portable distribution. No existing directory is deleted.
    $buildRoot = Join-Path $repositoryRoot ".artifacts/desktop-build/$([Guid]::NewGuid().ToString('N'))"
    $publishDirectory = Join-Path $buildRoot 'publish'
    $launcherDirectory = Join-Path $buildRoot 'launcher'
    $packageDirectory = Join-Path $buildRoot 'dist'
    $stagePackage = Join-Path $buildRoot 'package'
    New-Item -ItemType Directory -Path $publishDirectory, $launcherDirectory, $packageDirectory -Force | Out-Null

    Invoke-DotNet @('restore', 'NimCap.Screenshot.slnx')
    Invoke-DotNet @('build', 'NimCap.Screenshot.slnx', '-c', 'Release', '--no-restore', '-m:1',
        '-p:UseSharedCompilation=false', "-bl:$buildRoot/build.binlog")
    Invoke-DotNet @('run', '--project', 'tools/UpdateChecks/UpdateChecks.csproj', '-c', 'Release')
    Invoke-DotNet @('run', '--project', 'tools/WindowChecks/WindowChecks.csproj', '-c', 'Release',
        '--no-build', '--', '--dpi-only')
    Invoke-DotNet @('publish', 'NimCap.Screenshot.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', $publishDirectory, '-m:1',
        '-p:UseSharedCompilation=false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        "-bl:$buildRoot/publish.binlog")
    # Prefer the tiny native launcher; fall back to the self-contained managed launcher without MSVC.
    New-Item -ItemType Directory -Path $stagePackage -Force | Out-Null
    $nativeLauncher = Join-Path $buildRoot 'launcher-native\NimCap.Screenshot.exe'
    & (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -Source (Join-Path $repositoryRoot 'tools/Launcher/launcher.cpp') -Output $nativeLauncher | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Copy-Item -LiteralPath $nativeLauncher -Destination (Join-Path $stagePackage 'NimCap.Screenshot.exe')
    }
    else {
        Invoke-DotNet @('publish', 'tools/Launcher/Launcher.csproj', '-c', 'Release', '-r', 'win-x64',
            '-o', $launcherDirectory, '-m:1', '-p:UseSharedCompilation=false',
            "-bl:$buildRoot/launcher.binlog")
        Copy-Item -LiteralPath (Join-Path $launcherDirectory 'NimCap.Screenshot.exe') -Destination (Join-Path $stagePackage 'NimCap.Screenshot.exe')
    }

    # Assemble the portable layout: launcher + portable marker at the root, runtime under Program/, user data under Data/.
    Move-Item -LiteralPath $publishDirectory -Destination (Join-Path $stagePackage 'Program')
    $programDirectory = Join-Path $stagePackage 'Program'
    New-Item -ItemType File -Path (Join-Path $stagePackage 'portable') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $stagePackage 'Data') -Force | Out-Null
    New-Item -ItemType File -Path (Join-Path $stagePackage 'Data\.gitkeep') -Force | Out-Null
    foreach ($name in @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $stagePackage $name)
    }

    $requiredFiles = @(
        'Program/NimCap.Screenshot.exe', 'Program/NimCap.Screenshot.dll', 'Program/NimCap.Screenshot.deps.json',
        'Program/NimCap.Screenshot.runtimeconfig.json', 'Program/coreclr.dll', 'Program/hostfxr.dll',
        'NimCap.Screenshot.exe', 'portable', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md',
        'Program/ThirdPartyLicenses/PACKAGE-NOTICES.md', 'Program/ThirdPartyLicenses/DirectML-LICENSE.txt'
    )
    foreach ($name in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagePackage $name) -PathType Leaf)) {
            throw "The portable distribution is missing $name."
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $stagePackage 'Data') -PathType Container)) {
        throw 'The portable distribution is missing the Data folder.'
    }
    $executable = Join-Path $programDirectory 'NimCap.Screenshot.exe'
    $fileVersion = [Version][Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
    if ($fileVersion -ne [Version]"$projectVersion.0") {
        throw "Executable version $fileVersion does not match $projectVersion."
    }
    if ((Get-Item -LiteralPath (Join-Path $stagePackage 'NimCap.Screenshot.exe')).Length -le 0) {
        throw 'The portable launcher is empty.'
    }
    $forbiddenExtensions = @('.onnx', '.gguf', '.safetensors', '.traineddata', '.pt', '.pth', '.pfx', '.p12', '.key')
    $forbiddenNames = @('settings.json', 'data-location.json', '.env', 'secrets.json')
    foreach ($file in Get-ChildItem -LiteralPath $programDirectory -Recurse -Force -File) {
        if ($file.Extension -in $forbiddenExtensions -or $file.Name -in $forbiddenNames) {
            throw "Unexpected private data or model file in the distribution: $($file.Name)."
        }
    }
    foreach ($name in @('models', 'History', 'Diagnostics', 'downloads', '.git', '.codex', '.agents')) {
        if (Test-Path -LiteralPath (Join-Path $programDirectory $name)) {
            throw "Unexpected local directory in the distribution: $name."
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $stagePackage 'Data') -Recurse -Force -File) {
        if ($file.Name -ne '.gitkeep') {
            throw "Unexpected user file shipped in the portable Data folder: $($file.Name)."
        }
    }

    $packageName = "nimcap-screenshot-v$projectVersion-win-x64.zip"
    $packagePath = Join-Path $packageDirectory $packageName
    # Archive children at ZIP root, exactly as expected by the in-app updater.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stagePackage, $packagePath,
        [IO.Compression.CompressionLevel]::Optimal, $false)
    $digest = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = Join-Path $packageDirectory 'SHA256SUMS.txt'
    [IO.File]::WriteAllText($checksumPath, "$digest  $packageName`n", [Text.UTF8Encoding]::new($false))
    if ($env:GITHUB_OUTPUT) {
        @("version=$projectVersion", "package_path=$packagePath", "checksum_path=$checksumPath") |
            Out-File -LiteralPath $env:GITHUB_OUTPUT -Encoding utf8 -Append
    }
    if ($env:GITHUB_STEP_SUMMARY) {
        @('## Windows x64 build', "Version: $projectVersion", "Package: $packageName", "SHA256: $digest",
            '', 'Checks: solution build, offline updater regression, mixed-DPI coordinate fixtures.',
            'Real capture, GPU models, microphone, and camera require separate hardware validation.') |
            Out-File -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
    }
    Write-Host "Package: $packagePath"
    Write-Host "Checksum: $checksumPath"
}
finally {
    Pop-Location
}
