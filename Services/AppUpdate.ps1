param([Parameter(Mandatory = $true)][string]$Plan, [switch]$TestMode)
$ErrorActionPreference = 'Stop'
$changed = [System.Collections.Generic.List[object]]::new()
$temporaryFiles = [System.Collections.Generic.List[string]]::new()
$state = $null
$waiting = $true

function Full-Directory([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
}

function Safe-File([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw 'Invalid update path.' }
    $resolved = [IO.Path]::GetFullPath([IO.Path]::Combine($Root, $Relative))
    if (-not $resolved.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escapes its directory.' }
    $cursor = $resolved
    while ($cursor -and $cursor.Length -ge $Root.TrimEnd('\').Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Update cannot traverse a link or junction.' }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $resolved
}

function Replace-File([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $temporary = $Destination + '.nimcap-screenshot-new-' + [Guid]::NewGuid().ToString('N')
    $temporaryFiles.Add($temporary)
    [IO.File]::Copy($Source, $temporary, $false)
    if ([IO.File]::Exists($Destination)) { [IO.File]::Replace($temporary, $Destination, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $Destination) }
}

try {
    $state = Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
    $target = Full-Directory $state.Target
    $stage = Full-Directory $state.Stage
    $backup = Full-Directory $state.Backup
    $restart = if ($state.Restart) { $state.Restart } else { [IO.Path]::Combine($target, 'NimCap.Screenshot.exe') }
    if ($target -eq $stage -or $target.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase) -or
        $stage.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) -or
        $backup.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update directory layout.' }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $state.Files) {
        if (-not $seen.Add($file.Relative)) { throw 'Duplicate update path.' }
        $source = Safe-File $stage $file.Relative
        $null = Safe-File $target $file.Relative
        $null = Safe-File $backup $file.Relative
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'Staged update checksum mismatch.' }
    }
    if (-not $seen.Contains('NimCap.Screenshot.exe')) { throw 'Missing application executable.' }
    [IO.File]::WriteAllText($state.Ready, 'ready')
    if (-not $TestMode) {
        try { $parent = [Diagnostics.Process]::GetProcessById([int]$state.ParentId) }
        catch [ArgumentException] { $parent = $null }
        if ($null -ne $parent) {
            if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$state.ParentStartTicks) { throw 'Application process identity changed.' }
            if (-not $parent.WaitForExit(120000)) { throw 'NimCap Screenshot did not exit. Update cancelled without changing files.' }
        }
    }
    if ($state.Abort -and [IO.File]::Exists($state.Abort)) { throw 'Update was cancelled.' }
    $waiting = $false
    # Snapshot every file before changing any destination file.
    foreach ($file in $state.Files) {
        $destination = Safe-File $target $file.Relative
        $original = Safe-File $backup $file.Relative
        $file | Add-Member -NotePropertyName Existed -NotePropertyValue ([IO.File]::Exists($destination))
        if ($file.Existed) {
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($original)) | Out-Null
            [IO.File]::Copy($destination, $original, $false)
        }
    }
    foreach ($file in $state.Files) {
        $source = Safe-File $stage $file.Relative
        $destination = Safe-File $target $file.Relative
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.Sha256) { throw 'Staged file changed during installation.' }
        Replace-File $source $destination
        $changed.Add($file)
    }
    @{ Status = 'installed'; Backup = $backup } | ConvertTo-Json | Set-Content -LiteralPath $state.Result -Encoding UTF8
    if (-not $TestMode) {
        Start-Process -FilePath $restart -WorkingDirectory $target -WindowStyle Hidden -ErrorAction Stop | Out-Null
    }
    exit 0
}
catch {
    $failure = $_.Exception.Message
    $rollbackFailures = [System.Collections.Generic.List[string]]::new()
    for ($index = $changed.Count - 1; $index -ge 0; $index--) {
        $file = $changed[$index]
        try {
            $destination = Safe-File $target $file.Relative
            if ($file.Existed) { Replace-File (Safe-File $backup $file.Relative) $destination }
            else { [IO.File]::Delete($destination) }
        }
        catch { $rollbackFailures.Add($_.Exception.Message) }
    }
    if ($null -ne $state) {
        @{ Status = 'failed'; Error = $failure; RollbackErrors = @($rollbackFailures.ToArray()); Backup = $state.Backup } |
            ConvertTo-Json | Set-Content -LiteralPath $state.Result -Encoding UTF8
    }
    if (-not $TestMode -and -not $waiting) {
        Add-Type -AssemblyName PresentationFramework
        $message = "NimCap Screenshot update failed: $failure`n"
        if ($rollbackFailures.Count -eq 0) { $message += "The previous program files have been preserved/restored.`n" }
        else { $message += "Some files could not be restored. Recover them from: $($state.Backup)`n" }
        $message += "Details: $($state.Result)"
        [System.Windows.MessageBox]::Show($message, 'NimCap Screenshot update', 'OK', 'Error') | Out-Null
        if (-not $waiting -and $rollbackFailures.Count -eq 0) {
            Start-Process -FilePath $restart -WorkingDirectory $target -WindowStyle Hidden
        }
    }
    exit 1
}
finally {
    foreach ($temporary in $temporaryFiles) {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}
