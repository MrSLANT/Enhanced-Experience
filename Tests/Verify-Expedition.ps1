#Requires -Version 7
param(
    [string] $GameDirectory = (Join-Path $PSScriptRoot '../../..'),
    [string] $LuaCsServerDirectory,
    [switch] $SkipPackagePaths,
    [switch] $AllowMissingPrerequisites
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$game = [IO.Path]::GetFullPath($GameDirectory)
if (!$LuaCsServerDirectory) {
    $LuaCsServerDirectory = Join-Path $game '../../workshop/content/602960/2559634234/Binary'
}
$server = [IO.Path]::GetFullPath($LuaCsServerDirectory)
if (!(Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    if (!$AllowMissingPrerequisites) { throw 'dotnet is required.' }
    'C# compilation and AI checks: SKIP - dotnet is not on PATH.'
    return
}
$sdk = dotnet --list-sdks | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'Could not list installed .NET SDKs.' }
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') {
    if (!$AllowMissingPrerequisites) { throw '.NET 8 SDK is required.' }
    'C# compilation and AI checks: SKIP - .NET 8 SDK is not installed.'
    return
}
$csc = Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
$runtimeInfo = dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' } | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'Could not list installed .NET runtimes.' }
if (!$runtimeInfo) {
    if (!$AllowMissingPrerequisites) { throw '.NET 8 runtime is required.' }
    'C# compilation and AI checks: SKIP - .NET 8 runtime is not installed.'
    return
}
if (!(Test-Path -LiteralPath $game -PathType Container) -or
    !(Test-Path -LiteralPath (Join-Path $game 'System.Runtime.dll') -PathType Leaf)) {
    if (!$AllowMissingPrerequisites) { throw "Game reference assemblies are missing in $game." }
    "C# compilation and AI checks: SKIP - game reference assemblies are missing in $game."
    return
}
$refs = Get-ChildItem -LiteralPath $game -File -Filter '*.dll' |
    Where-Object { $_.Name -match '^(System\.|mscorlib.dll|netstandard.dll|0Harmony.dll|Farseer|MonoGame|XNATypes)' -and $_.Name -notlike '*.Native.dll' } |
    ForEach-Object { '/r:' + $_.FullName }
$sources = (Get-ChildItem -LiteralPath (Join-Path $root 'CSharp/Shared') -Recurse -File -Filter '*.cs').FullName
$policy = Join-Path $root 'CSharp/Shared/ExpeditionPolicy.cs'
$output = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()))
$checks = Join-Path $output.FullName 'ExpeditionChecks.dll'
$runtime = Join-Path $output.FullName 'ExpeditionChecks.runtimeconfig.json'

try {
    if (!$SkipPackagePaths) {
        foreach ($manifest in 'filelist.xml', 'ModConfig.xml') {
            [xml] $xml = Get-Content -Raw -LiteralPath (Join-Path $root $manifest)
            foreach ($attribute in $xml.SelectNodes('//@file | //@Folder')) {
                $entry = $attribute.Value.Replace('%ModDir%', $root)
                if (!(Test-Path -LiteralPath $entry)) { throw "Missing package path $entry" }
            }
            "$manifest package paths: PASS"
        }
    }
    $aiUnavailable = if (!(Test-Path -LiteralPath (Join-Path $game 'Content/Items') -PathType Container)) {
        "Game item resources are missing in $game."
    }
    if ($aiUnavailable -and !$AllowMissingPrerequisites) { throw $aiUnavailable }

    $sideFailed = $false
    foreach ($side in 'Barotrauma', 'DedicatedServer') {
        $binaryDirectory = if ($side -eq 'DedicatedServer') { $server } else { $game }
        $assemblies = @("$binaryDirectory/$side.dll", "$binaryDirectory/BarotraumaCore.dll")
        $missing = @($assemblies | Where-Object { !(Test-Path -LiteralPath $_ -PathType Leaf) })
        if ($missing.Count) {
            if (!$AllowMissingPrerequisites) { throw "Missing assemblies: $($missing -join ', ')." }
            "$side compilation and AI checks: SKIP - missing $($missing -join ', ')."
            continue
        }
        try {
            $subdirectory = New-Item -ItemType Directory -Path (Join-Path $output.FullName $side)
            $plugin = Join-Path $subdirectory.FullName 'InternalsAwareAssembly.dll'
            $gameRefs = $assemblies | ForEach-Object { '/r:' + $_ }
            dotnet $csc /nologo /noconfig /nostdlib+ /target:library "/out:$plugin" $refs $gameRefs $sources
            if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
            "$side compilation: PASS"
        }
        catch {
            $sideFailed = $true
            "$side compilation: FAIL - $($_.Exception.Message)"
            "$side AI checks: SKIP - the mod did not compile."
            continue
        }
        if ($aiUnavailable) {
            "$side AI checks: SKIP - $aiUnavailable"
            continue
        }
        if (!(Test-Path -LiteralPath $checks -PathType Leaf)) {
            $runtimeVersion = $runtimeInfo.Split(' ')[1]
            @{ runtimeOptions = @{ tfm = 'net8.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $runtimeVersion } } } |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $runtime
            dotnet $csc /nologo /noconfig /nostdlib+ /target:exe "/out:$checks" $refs "$PSScriptRoot/ExpeditionChecks.cs" $policy
            if ($LASTEXITCODE -ne 0) { throw 'Could not compile expedition checks.' }
        }
        try {
            dotnet $checks $binaryDirectory $side $plugin $game
            if ($LASTEXITCODE -ne 0) { throw "Expedition checks exited with code $LASTEXITCODE." }
            "$side AI checks: PASS"
        }
        catch {
            $sideFailed = $true
            "$side AI checks: FAIL - $($_.Exception.Message)"
        }
    }
    if ($sideFailed) { throw 'One or more client/server checks failed.' }
}
finally {
    Get-ChildItem -LiteralPath $output.FullName -File | Remove-Item
    foreach ($side in 'Barotrauma', 'DedicatedServer') {
        $child = Join-Path $output.FullName $side
        if (Test-Path -LiteralPath $child) {
            Get-ChildItem -LiteralPath $child -File | Remove-Item
            Remove-Item -LiteralPath $child
        }
    }
    Remove-Item -LiteralPath $output.FullName
}
