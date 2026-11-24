param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')]
    [string]$Version,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    $Version = ([xml](Get-Content -LiteralPath (Join-Path $project 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
}
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $project 'artifacts'
}
$destination = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$artifacts = (New-Item -ItemType Directory -Path (Join-Path $project 'artifacts') -Force).FullName
$staging = (New-Item -ItemType Directory -Path (Join-Path $artifacts ('publish-' + [guid]::NewGuid().ToString('N')))).FullName

try {
    foreach ($name in 'BatteryDesk', 'BatteryDesk.Probe') {
        dotnet publish (Join-Path $project "$name/$name.csproj") -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $staging
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $name." }
    }
    foreach ($name in 'README.md', 'README.vi.md', 'LICENSE', 'CONTRIBUTING.md', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item -LiteralPath (Join-Path $project $name) -Destination $staging
    }
    $assetsPath = Join-Path $project 'BatteryDesk/obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $deps = Get-Content -LiteralPath (Join-Path $staging 'BatteryDesk.deps.json') -Raw | ConvertFrom-Json
    $notices = (New-Item -ItemType Directory -Path (Join-Path $staging 'licenses')).FullName
    foreach ($pack in @(
        @{ Name = 'Microsoft.NETCore.App.Runtime.win-x64'; Files = @{ 'LICENSE.TXT' = 'dotnet-runtime-LICENSE.txt'; 'THIRD-PARTY-NOTICES.TXT' = 'dotnet-runtime-THIRD-PARTY-NOTICES.txt' } }
        @{ Name = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Files = @{ 'LICENSE' = 'dotnet-windowsdesktop-LICENSE.txt' } }
    )) {
        $library = $deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like "runtimepack.$($pack.Name)/*" } | Select-Object -First 1
        if (!$library) { throw "No resolved runtime version found for $($pack.Name)." }
        $packagePath = $library.Substring('runtimepack.'.Length).ToLowerInvariant()
        $packages = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object { Join-Path $_ $packagePath } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (!$packages) { throw "Restored package for $($pack.Name) is missing." }
        foreach ($file in $pack.Files.GetEnumerator()) {
            Copy-Item -LiteralPath (Join-Path $packages $file.Key) -Destination (Join-Path $notices $file.Value)
        }
    }
    Copy-Item -LiteralPath (Join-Path $project 'docs') -Destination $staging -Recurse
    $archive = Join-Path $destination "BatteryDesk-$Version-win-x64.zip"
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Get-Item -LiteralPath $archive, "$archive.sha256"
} finally {
    $resolved = (Resolve-Path -LiteralPath $staging).Path
    if ([IO.Path]::GetDirectoryName($resolved) -eq $artifacts -and [IO.Path]::GetFileName($resolved) -match '^publish-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
