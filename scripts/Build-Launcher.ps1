[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Output
)

# Compiles the native launcher with MSVC when it is available. Returns exit code 1
# so callers can fall back to the self-contained managed launcher.
$ErrorActionPreference = 'Stop'
try {
    $candidates = @(
        Get-ChildItem -Path 'C:\Program Files\Microsoft Visual Studio\2022\*\VC\Auxiliary\Build\vcvars64.bat' -ErrorAction SilentlyContinue
        Get-ChildItem -Path 'C:\Program Files (x86)\Microsoft Visual Studio\2022\*\VC\Auxiliary\Build\vcvars64.bat' -ErrorAction SilentlyContinue
    )
    $vcvars = $candidates | Select-Object -First 1 -ExpandProperty FullName
    if (-not $vcvars) { exit 1 }

    $outputDirectory = Split-Path -Parent $Output
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $objectOutput = [IO.Path]::ChangeExtension($Output, '.obj')
    $batch = [IO.Path]::GetTempFileName() + '.bat'
    @(
        '@echo off'
        ('call "{0}" >nul 2>&1' -f $vcvars)
        ('cl /nologo /O2 /EHsc /MT /W3 /Fe"{0}" /Fo"{1}" "{2}" /link /SUBSYSTEM:WINDOWS' -f $Output, $objectOutput, $Source)
    ) | Set-Content -LiteralPath $batch -Encoding ASCII
    cmd /c "`"$batch`""
    $exitCode = $LASTEXITCODE
    Remove-Item -LiteralPath $batch -Force -ErrorAction SilentlyContinue
    if ($exitCode -ne 0 -or -not (Test-Path -LiteralPath $Output)) { exit 1 }
    exit 0
}
catch { exit 1 }
