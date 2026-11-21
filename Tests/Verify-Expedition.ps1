#Requires -Version 7
param(
    [string] $GameDirectory = (Join-Path $PSScriptRoot '../../..'),
    [string] $LuaCsServerDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$game = (Resolve-Path -LiteralPath $GameDirectory).Path
if (!$LuaCsServerDirectory) {
    $LuaCsServerDirectory = Join-Path $game '../../workshop/content/602960/2559634234/Binary'
}
$server = (Resolve-Path -LiteralPath $LuaCsServerDirectory).Path
$sdk = dotnet --list-sdks | Where-Object { $_ -match '^8\.' } | Select-Object -Last 1
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw '.NET 8 SDK is required.' }
$csc = Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
$output = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString()))
$refs = Get-ChildItem -LiteralPath $game -File -Filter '*.dll' |
    Where-Object { $_.Name -match '^(System\.|mscorlib.dll|netstandard.dll|0Harmony.dll|Farseer|MonoGame|XNATypes)' -and $_.Name -notlike '*.Native.dll' } |
    ForEach-Object { '/r:' + $_.FullName }
$sources = (Get-ChildItem -LiteralPath (Join-Path $root 'CSharp/Shared') -Filter '*.cs').FullName
$policy = Join-Path $root 'CSharp/Shared/ExpeditionPolicy.cs'
$checks = Join-Path $output.FullName 'ExpeditionChecks.dll'
$runtime = Join-Path $output.FullName 'ExpeditionChecks.runtimeconfig.json'

try {
    foreach ($manifest in 'filelist.xml', 'ModConfig.xml') {
        [xml] $xml = Get-Content -Raw -LiteralPath (Join-Path $root $manifest)
        foreach ($attribute in $xml.SelectNodes('//@file | //@Folder')) {
            $entry = $attribute.Value.Replace('%ModDir%', $root)
            if (!(Test-Path -LiteralPath $entry)) { throw "Missing package path $entry" }
        }
        "$manifest package paths: PASS"
    }
    $runtimeVersion = (dotnet --list-runtimes | Where-Object { $_ -match '^Microsoft.NETCore.App 8\.' } |
        Select-Object -Last 1).Split(' ')[1]
    @{ runtimeOptions = @{ tfm = 'net8.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $runtimeVersion } } } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $runtime
    dotnet $csc /nologo /noconfig /nostdlib+ /target:exe "/out:$checks" $refs "$PSScriptRoot/ExpeditionChecks.cs" $policy
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile expedition checks.' }

    foreach ($side in 'Barotrauma', 'DedicatedServer') {
        $subdirectory = New-Item -ItemType Directory -Path (Join-Path $output.FullName $side)
        $plugin = Join-Path $subdirectory.FullName 'InternalsAwareAssembly.dll'
        $binaryDirectory = if ($side -eq 'DedicatedServer') { $server } else { $game }
        $gameRefs = "/r:$binaryDirectory/$side.dll", "/r:$binaryDirectory/BarotraumaCore.dll"
        dotnet $csc /nologo /noconfig /nostdlib+ /target:library "/out:$plugin" $refs $gameRefs $sources
        if ($LASTEXITCODE -ne 0) { throw "Compilation failed for $side." }
        "$side compilation: PASS"
        dotnet $checks $binaryDirectory $side $plugin $game
        if ($LASTEXITCODE -ne 0) { throw "Expedition checks failed for $side." }
    }
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
