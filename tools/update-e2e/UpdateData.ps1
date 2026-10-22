# Shared bounded fixed-data handling for the owned update/restart acceptance runners.
# Not a filesystem sandbox: callers must own the fixture and keep it unchanged during the run.
function ReadUpdateReceipt([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        try { return ConvertFrom-Json -InputObject $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    } catch [IO.FileNotFoundException] { return $null } # Atomic rename can race opening the old path.
}
function ReadDataFixtureFiles([string]$directory) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { throw 'Data fixture directory does not exist.' }
    if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Data fixtures must not contain link/reparse paths.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($directory)
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $examined = 0
    $bytes = [long]0
    while ($pending.Count -gt 0) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            if (++$examined -gt 128) { throw 'Data fixture exceeds the fixed 128-entry / 16 MiB limit.' }
            $attributes = [IO.File]::GetAttributes($path)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Data fixtures must not contain link/reparse paths.'
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($path); continue }
            $file = [IO.FileInfo]::new($path)
            $bytes += $file.Length
            if ($bytes -gt 16MB) { throw 'Data fixture exceeds the fixed 128-entry / 16 MiB limit.' }
            $files.Add($file)
        }
    }
    return $files.ToArray()
}
function DataHashes([string]$directory) {
    $data = Join-Path $directory 'data'
    if (-not (Test-Path -LiteralPath $data)) { return '[]' }
    $rows = @(Get-ChildItem -LiteralPath $data -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ name = $_.FullName.Substring($data.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    return ConvertTo-Json -InputObject $rows -Compress
}
function AddDataFixture([string]$directory, [string]$fixtureDirectory, [IO.FileInfo[]]$files) {
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($fixtureDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1)
        $destination = Join-Path (Join-Path $directory 'data') $relative
        if (Test-Path -LiteralPath $destination) { throw "Fixture conflicts with an existing test marker: $relative" }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $false)
    }
}
function AssertRestartData([string]$before, [string]$after, [bool]$settingsSaved) {
    if (-not $settingsSaved) {
        if ($before -ne $after) { throw 'Cancel/banner restart changed a data file.' }
        return
    }
    # Windows PowerShell ConvertFrom-Json emits an array as one pipeline object. Enumerate it explicitly
    # before filtering; otherwise Where-Object keeps the whole array, including the saved settings file.
    $beforeRows = ConvertFrom-Json -InputObject $before
    $afterRows = ConvertFrom-Json -InputObject $after
    $beforeFiles = @($beforeRows | Where-Object name -ne 'settings.json')
    $afterFiles = @($afterRows | Where-Object name -ne 'settings.json')
    if ((ConvertTo-Json -InputObject $beforeFiles -Compress) -ne (ConvertTo-Json -InputObject $afterFiles -Compress)) {
        throw 'Settings-save restart changed non-settings data.'
    }
}
