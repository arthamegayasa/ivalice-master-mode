<#
.SYNOPSIS
  Sets up Reloaded-II, the mods in catalog/mods.json and Ivalice Control Center for
  FINAL FANTASY TACTICS - The Ivalice Chronicles (Enhanced).

.DESCRIPTION
  Safe to run again: anything already present is left alone unless -Force is given.
  Mods without a redistribution licence (Improved Fast Forward, AntiAutoRotate) are not
  downloaded. Put their archives from Nexus Mods in -ArchiveDir and they are installed from there.

.EXAMPLE
  .\scripts\install.ps1 -ArchiveDir "$env:USERPROFILE\Downloads"
#>
[CmdletBinding()]
param(
    [string]$ReloadedDir = 'D:\Modding\Reloaded-II',
    [string]$AppDir,
    [string]$GameDir,
    [string]$ArchiveDir,
    [switch]$Force,
    [switch]$NoShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $AppDir) { $AppDir = Join-Path $repo 'dist\IvaliceControlCenter' }
$work = Join-Path ([IO.Path]::GetTempPath()) ("ivalice-install-" + [Guid]::NewGuid().ToString('N'))
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
New-Item -ItemType Directory -Force $work | Out-Null

function Write-Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Expand-Any([string]$archive, [string]$destination) {
    New-Item -ItemType Directory -Force $destination | Out-Null
    & $tar -xf $archive -C $destination
    if ($LASTEXITCODE -ne 0) { throw "Could not extract $archive" }
}

function Get-ReleaseAsset([string]$repoName, [string]$pattern) {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repoName/releases/latest" -Headers @{ 'User-Agent' = 'ivalice-master-mode-installer' }
    $asset = $release.assets | Where-Object { $_.name -like $pattern -and $_.name -notlike '*_to_*' } | Select-Object -First 1
    if (-not $asset) { throw "No asset matching '$pattern' in the latest release of $repoName" }
    $target = Join-Path $work $asset.name
    Invoke-WebRequest $asset.browser_download_url -OutFile $target -UseBasicParsing
    return $target
}

function Get-ModId([string]$folder) {
    $config = Get-ChildItem $folder -Recurse -Filter ModConfig.json | Select-Object -First 1
    if (-not $config) { throw "No ModConfig.json found in $folder" }
    return @{ Id = (Get-Content $config.FullName -Raw | ConvertFrom-Json).ModId; Root = $config.DirectoryName }
}

function Install-ModFolder([string]$extracted, [string]$modsDir) {
    $mod = Get-ModId $extracted
    $target = Join-Path $modsDir $mod.Id
    if ((Test-Path $target) -and -not $Force) { Write-Host "   $($mod.Id): already installed"; return }
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $mod.Root '*') $target -Recurse -Force
    Write-Host "   $($mod.Id): installed"
}

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $libraries += Select-String -Path $vdf -Pattern '"path"\s+"(.+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value -replace '\\\\', '\' }
    }
    foreach ($library in $libraries) {
        $candidate = Join-Path $library 'steamapps\common\FINAL FANTASY TACTICS - The Ivalice Chronicles'
        if (Test-Path (Join-Path $candidate 'FFT_enhanced.exe')) { return $candidate }
    }
    return $null
}

try {
    $catalog = Get-Content (Join-Path $repo 'catalog\mods.json') -Raw | ConvertFrom-Json

    Write-Step 'Checking prerequisites'
    foreach ($tool in 'git', 'winget') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is required but was not found." }
    }
    $sdks = if (Test-Path $dotnet) { & $dotnet --list-sdks } else { @() }
    if (-not ($sdks -match '^9\.')) {
        Write-Host '   Installing .NET 9 SDK (needed to build the app and two mods)...'
        winget install --id Microsoft.DotNet.SDK.9 -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity | Out-Null
    }
    $x86 = Join-Path ${env:ProgramFiles(x86)} 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (-not (Test-Path $x86) -or -not (Get-ChildItem $x86 -Filter '9.*')) {
        Write-Host '   Installing .NET 9 Desktop Runtime x86 (required by the Reloaded-II launcher)...'
        winget install --id Microsoft.DotNet.DesktopRuntime.9 -e --architecture x86 --silent --accept-source-agreements --accept-package-agreements --disable-interactivity | Out-Null
    }

    if (-not $GameDir) { $GameDir = Find-Game }
    if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'FFT_enhanced.exe'))) {
        throw 'Could not find FFT_enhanced.exe. Pass -GameDir with the game folder.'
    }
    Write-Host "   Game: $GameDir"

    Write-Step 'Reloaded-II'
    $launcher = Join-Path $ReloadedDir 'Reloaded-II.exe'
    if (-not (Test-Path $launcher)) {
        Expand-Any (Get-ReleaseAsset 'Reloaded-Project/Reloaded-II' 'Release.zip') $ReloadedDir
        Write-Host "   Installed to $ReloadedDir"
    } else { Write-Host '   Already installed' }

    $loaderConfig = Join-Path $env:APPDATA 'Reloaded-Mod-Loader-II\ReloadedII.json'
    if (-not (Test-Path $loaderConfig)) {
        Write-Host '   Starting the launcher once so it writes its configuration...'
        $process = Start-Process $launcher -WorkingDirectory $ReloadedDir -PassThru
        for ($i = 0; $i -lt 60 -and -not (Test-Path $loaderConfig); $i++) { Start-Sleep -Seconds 1 }
        Start-Sleep -Seconds 2
        if (-not $process.HasExited) { $process | Stop-Process -Force }
        if (-not (Test-Path $loaderConfig)) { throw 'Reloaded-II did not create its configuration.' }
    }
    $layout = Get-Content $loaderConfig -Raw | ConvertFrom-Json
    $modsDir = $layout.ModConfigDirectory
    $appsDir = $layout.ApplicationConfigDirectory
    New-Item -ItemType Directory -Force $modsDir, $appsDir | Out-Null

    Write-Step 'Mods that may be downloaded (open licences)'
    $downloads = @(
        @{ Repo = 'Reloaded-Project/Reloaded.Memory.SigScan'; Pattern = 'Reloaded.Memory.SigScan.ReloadedII*.7z'; Id = 'Reloaded.Memory.SigScan.ReloadedII' },
        @{ Repo = 'Sewer56/Reloaded.SharedLib.Hooks.ReloadedII'; Pattern = 'Reloaded.Hooks.ReloadedII*.7z'; Id = 'reloaded.sharedlib.hooks' },
        @{ Repo = 'Nenkai/fftivc.utility.modloader'; Pattern = 'fftivc.utility.modloader*.7z'; Id = 'fftivc.utility.modloader' }
    )
    foreach ($item in $downloads) {
        if ((Test-Path (Join-Path $modsDir $item.Id)) -and -not $Force) { Write-Host "   $($item.Id): already installed"; continue }
        $folder = Join-Path $work $item.Id
        Expand-Any (Get-ReleaseAsset $item.Repo $item.Pattern) $folder
        Install-ModFolder $folder $modsDir
    }

    Write-Step 'Mods built from source (GPL-3.0, no published releases)'
    $env:RELOADEDIIMODS = $modsDir
    foreach ($item in @(
            @{ Repo = 'knightsong/global-jp-boost'; Id = 'fftivc.mechanics.globaljpboost' },
            @{ Repo = 'knightsong/xp-multiplier'; Id = 'fftivc.mechanics.xpmultiplier' })) {
        if ((Test-Path (Join-Path $modsDir $item.Id)) -and -not $Force) { Write-Host "   $($item.Id): already installed"; continue }
        $source = Join-Path $work ($item.Repo -replace '/', '_')
        git clone --quiet --depth 1 "https://github.com/$($item.Repo)" $source
        & $dotnet build $source -c Release --nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Build of $($item.Repo) failed." }
        Write-Host "   $($item.Id): built and installed"
    }

    Write-Step 'Mods you download yourself (no redistribution licence)'
    $manual = @(
        @{ Id = 'fftivc.qol.turbo'; Pattern = '*Fast Forward*.zip'; Url = 'https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/192' },
        @{ Id = 'fft.antiautorotate'; Pattern = '*AntiAutoRotate*.zip'; Url = 'https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/173' }
    )
    foreach ($item in $manual) {
        if ((Test-Path (Join-Path $modsDir $item.Id)) -and -not $Force) { Write-Host "   $($item.Id): already installed"; continue }
        $archive = if ($ArchiveDir) { Get-ChildItem $ArchiveDir -Filter $item.Pattern | Sort-Object LastWriteTime | Select-Object -Last 1 }
        if (-not $archive) {
            Write-Warning "$($item.Id): download it from $($item.Url), then rerun with -ArchiveDir pointing at the folder that holds the zip."
            continue
        }
        $folder = Join-Path $work $item.Id
        Expand-Any $archive.FullName $folder
        Install-ModFolder $folder $modsDir
    }

    Write-Step 'Registering the game'
    $appDirCfg = Join-Path $appsDir $catalog.app.id
    $appConfigPath = Join-Path $appDirCfg 'AppConfig.json'
    $installed = Get-ChildItem $modsDir -Directory | ForEach-Object { $_.Name }
    $wanted = @($catalog.mods | Where-Object { $installed -contains $_.id } | ForEach-Object { $_.id })
    if (Test-Path $appConfigPath) {
        Write-Host '   Already registered; enabled mods left as they are'
    } else {
        New-Item -ItemType Directory -Force $appDirCfg | Out-Null
        $appConfig = [ordered]@{
            AppId = $catalog.app.id; AppName = $catalog.app.name
            AppLocation = (Join-Path $GameDir 'FFT_enhanced.exe'); AppArguments = ''; AppIcon = 'Icon.png'
            AutoInject = $false; EnabledMods = $wanted; WorkingDirectory = $GameDir; PluginData = @{}
        }
        [IO.File]::WriteAllText($appConfigPath, ($appConfig | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
        Write-Host "   Registered with $($wanted.Count) mods enabled"
    }

    Write-Step 'Ivalice Control Center'
    & $dotnet publish (Join-Path $repo 'src\Ivalice.ControlCenter') -c Release -o $AppDir --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Building Ivalice Control Center failed.' }
    $exe = (Resolve-Path (Join-Path $AppDir 'IvaliceControlCenter.exe')).Path
    Write-Host "   Built: $exe"
    if (-not $NoShortcut) {
        $shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Ivalice Control Center.lnk'
        $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = Split-Path $exe
        $shortcut.Description = 'Manage and launch Final Fantasy Tactics mods'
        $shortcut.Save()
        Write-Host "   Shortcut: $shortcutPath"
    }

    Write-Host "`nDone. Open 'Ivalice Control Center' and press Main." -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
