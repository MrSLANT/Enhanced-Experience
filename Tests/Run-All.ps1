#Requires -Version 7
param(
    [string] $GameDirectory = (Join-Path $PSScriptRoot '../../..'),
    [string] $LuaCsServerDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$failed = $false

function Resolve-PackageEntry([string] $Entry) {
    if ($Entry -notmatch '^%ModDir%[/\\](.+)$') {
        throw "Expected a package-relative path, found '$Entry'."
    }
    $path = [IO.Path]::GetFullPath((Join-Path $root $Matches[1]))
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    $comparison = if ($IsWindows) { 'OrdinalIgnoreCase' } else { 'Ordinal' }
    if (!$path.StartsWith($prefix, $comparison)) {
        throw "Path leaves the mod directory: '$Entry'."
    }
    return $path
}

try {
    $documents = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.xml') {
        $document = [xml]::new()
        $document.XmlResolver = $null
        $document.Load($file.FullName)
        $documents[$file.FullName] = $document
    }
    "XML syntax: PASS ($($documents.Count) files)"

    $manifestPath = Join-Path $root 'filelist.xml'
    $configPath = Join-Path $root 'ModConfig.xml'
    $manifest = $documents[$manifestPath].DocumentElement
    $config = $documents[$configPath].DocumentElement
    if (!$manifest -or $manifest.PSBase.Name -cne 'contentpackage' -or
        $manifest.GetAttribute('name') -cne 'Enhanced Experience' -or
        $manifest.GetAttribute('corepackage') -cne 'false' -or
        ![version]::TryParse($manifest.GetAttribute('gameversion'), [ref] $null) -or
        ![version]::TryParse($manifest.GetAttribute('modversion'), [ref] $null)) {
        throw 'Invalid contentpackage metadata in filelist.xml.'
    }
    $assembly = $config.SelectSingleNode('Assembly[not(@Target) or @Target="Any"]')
    $sharedFolder = Join-Path $root 'CSharp/Shared'
    if (!$config -or $config.PSBase.Name -cne 'ModConfig' -or $config.SelectNodes('*').Count -ne 1 -or
        !$assembly -or $assembly.GetAttribute('IsScript') -cne 'true' -or
        $assembly.GetAttribute('UseInternalAccessName') -cne 'true' -or
        (Resolve-PackageEntry $assembly.GetAttribute('Folder')) -cne $sharedFolder -or
        !(Test-Path -LiteralPath $sharedFolder -PathType Container)) {
        throw 'Invalid shared script assembly declaration in ModConfig.xml.'
    }

    $comparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    $declared = [Collections.Generic.HashSet[string]]::new($comparison)
    $otherFiles = [Collections.Generic.HashSet[string]]::new($comparison)
    foreach ($entry in $manifest.SelectNodes('*')) {
        $path = Resolve-PackageEntry $entry.GetAttribute('file')
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing package file: $path."
        }
        if (!$declared.Add($path)) { throw "Duplicate package file: $path." }
        if ($entry.PSBase.Name -ceq 'Other') { [void] $otherFiles.Add($path) }
    }
    $required = @($configPath)
    $required += (Get-ChildItem -LiteralPath (Join-Path $root 'CSharp') -Recurse -File -Filter '*.cs').FullName
    foreach ($path in $required | Select-Object -Unique) {
        if (!$otherFiles.Contains($path)) { throw "Missing Other declaration for $path." }
    }
    'Package configuration and resources: PASS'
}
catch {
    $failed = $true
    "XML and package validation: FAIL - $($_.Exception.Message)"
}

try {
    $verification = @{
        GameDirectory = $GameDirectory
        LuaCsServerDirectory = $LuaCsServerDirectory
        SkipPackagePaths = $true
        AllowMissingPrerequisites = $true
    }
    & (Join-Path $PSScriptRoot 'Verify-Expedition.ps1') @verification
}
catch {
    $failed = $true
    "C# verification: FAIL - $($_.Exception.Message)"
}

$luaTests = @(Get-ChildItem -LiteralPath $PSScriptRoot -File -Filter '*.lua')
$luac = Get-Command luac -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$luaTests.Count) {
    'Lua syntax: SKIP - no Lua test scripts found.'
}
elseif (!$luac) {
    'Lua syntax: SKIP - luac is not installed or on PATH.'
}
else {
    foreach ($test in $luaTests) {
        try {
            & $luac.Source -p $test.FullName
            if ($LASTEXITCODE -ne 0) { throw "luac exited with code $LASTEXITCODE." }
            "Lua syntax ($($test.Name)): PASS"
        }
        catch {
            $failed = $true
            "Lua syntax ($($test.Name)): FAIL - $($_.Exception.Message)"
        }
    }
}
foreach ($test in $luaTests | Where-Object { $_.BaseName -match '^\w+Quickstart$' }) {
    "In-game scenario ($($test.Name)): SKIP - use Tests/Run-Quickstart.lua in Barotrauma with LuaCs. See README.md."
}
'Multiplayer behavior: SKIP - needs a running multiplayer session.'

if ($failed) {
    'Run-All: FAIL'
    exit 1
}
'Run-All: PASS - all checks that ran passed. See SKIP entries for coverage gaps.'
exit 0
