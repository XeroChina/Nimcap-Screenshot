[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output
)

# Compiles the native launcher with MSVC when it is available. Returns exit code 1
# so callers can fall back to the self-contained managed launcher.
$ErrorActionPreference = 'Stop'
try {
    $vcvars = $null
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($installPath) {
            $candidate = Join-Path $installPath 'VC\Auxiliary\Build\vcvars64.bat'
            if (Test-Path -LiteralPath $candidate) { $vcvars = $candidate }
        }
    }
    if (-not $vcvars) {
        $found = foreach ($root in @('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio', 'C:\BuildTools')) {
            if (Test-Path -LiteralPath $root) {
                Get-ChildItem -Path (Join-Path $root '*\*\VC\Auxiliary\Build\vcvars64.bat') -ErrorAction SilentlyContinue
                Get-ChildItem -Path (Join-Path $root '*\VC\Auxiliary\Build\vcvars64.bat') -ErrorAction SilentlyContinue
            }
        }
        $vcvars = $found | Select-Object -First 1 -ExpandProperty FullName
    }
    if ($vcvars) { Write-Host "Native launcher: vcvars64 = $vcvars" }
    else { Write-Host 'Native launcher: vcvars64 not found, using managed fallback.' }
    if (-not $vcvars) { exit 1 }

    $outputDirectory = Split-Path -Parent $Output
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $objectOutput = [IO.Path]::ChangeExtension($Output, '.obj')
    $batch = [IO.Path]::GetTempFileName() + '.bat'
    @(
        '@echo off'
        ('call "{0}" >nul 2>&1' -f $vcvars)
        ('cl /nologo /std:c++17 /O2 /EHsc /MT /W3 /Fe"{0}" /Fo"{1}" "{2}" /link /SUBSYSTEM:WINDOWS' -f $Output, $objectOutput, $Source)
    ) | Set-Content -LiteralPath $batch -Encoding ASCII
    cmd /c "`"$batch`""
    $exitCode = $LASTEXITCODE
    Remove-Item -LiteralPath $batch -Force -ErrorAction SilentlyContinue
    Write-Host "Native launcher: cl exit code $exitCode; output exists = $(Test-Path -LiteralPath $Output)"
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $Output)) { exit 1 }
    exit 0
}
catch { Write-Host "Native launcher: $($_.Exception.Message)"; exit 1 }
