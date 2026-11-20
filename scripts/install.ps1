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

.PARAMETER ReloadedDir
  Where Reloaded-II goes. Defaults to the Reloaded-II already set up on this machine,
  or %LOCALAPPDATA%\Programs\Reloaded-II when there is none.
#>
[CmdletBinding()]
param(
    [string]$ReloadedDir,
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
$loaderConfig = Join-Path $env:APPDATA 'Reloaded-Mod-Loader-II\ReloadedII.json'
if (-not $ReloadedDir) {
    $existing = if (Test-Path $loaderConfig) { (Get-Content $loaderConfig -Raw | ConvertFrom-Json).LauncherPath }
    $ReloadedDir = if ($existing -and (Test-Path $existing)) { Split-Path $existing } else { Join-Path $env:LOCALAPPDATA 'Programs\Reloaded-II' }
}
$newlyInstalled = New-Object System.Collections.Generic.List[string]
$work = Join-Path ([IO.Path]::GetTempPath()) ("ivalice-install-" + [Guid]::NewGuid().ToString('N'))
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
New-Item -ItemType Directory -Force $work | Out-Null

function Write-Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Expand-Any([string]$archive, [string]$destination) {
    New-Item -ItemType Directory -Force $destination | Out-Null
    & $tar -xf $archive -C $destination
    if ($LASTEXITCODE -ne 0) {
        throw "Could not extract $archive. The tar.exe that ships with Windows 11 reads .7z; on older Windows, extract it by hand into the Reloaded-II Mods folder."
    }
}

function Install-WithWinget([string]$id, [string[]]$extra) {
    winget install --id $id -e @extra --silent --accept-source-agreements --accept-package-agreements --disable-interactivity | Out-Null
    # 0x8A15002B: winget reports "already installed, no upgrade available".
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) { throw "winget could not install $id (exit code $LASTEXITCODE)." }
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
    $newlyInstalled.Add($mod.Id)
    Write-Host "   $($mod.Id): installed"
}

function Find-Game {
    $key = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if (-not $key -or -not $key.PSObject.Properties['SteamPath']) { return $null }
    $steam = $key.SteamPath
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
        Install-WithWinget 'Microsoft.DotNet.SDK.9' @()
    }
    $x86 = Join-Path ${env:ProgramFiles(x86)} 'dotnet\shared\Microsoft.WindowsDesktop.App'
    if (-not (Test-Path $x86) -or -not (Get-ChildItem $x86 -Filter '9.*')) {
        Write-Host '   Installing .NET 9 Desktop Runtime x86 (required by the Reloaded-II launcher)...'
        Install-WithWinget 'Microsoft.DotNet.DesktopRuntime.9' @('--architecture', 'x86')
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

    Write-Step 'Mods built from source with this project''s patch (GPL-3.0, no published releases)'
    $env:RELOADEDIIMODS = $modsDir
    # Each is pinned to the commit its patch was written against. The patch limits the multiplier to
    # units on the player's side; a marker file records that the installed build carries it.
    foreach ($item in @(
            @{ Repo = 'knightsong/global-jp-boost'; Id = 'fftivc.mechanics.globaljpboost'; Commit = 'e60aaf18f9875f2df00f430744b0143bf0f30b2a'; Patch = 'global-jp-boost-player-units-only.patch' },
            @{ Repo = 'knightsong/xp-multiplier'; Id = 'fftivc.mechanics.xpmultiplier'; Commit = 'fede4e0e7e2c0404a17e8e79fb0a0cd4fdad0ce5'; Patch = 'xp-multiplier-player-units-only.patch' })) {
        $marker = Join-Path $modsDir "$($item.Id)\ivalice-patch.txt"
        if ((Test-Path $marker) -and -not $Force) { Write-Host "   $($item.Id): already installed"; continue }
        $source = Join-Path $work ($item.Repo -replace '/', '_')
        git clone --quiet "https://github.com/$($item.Repo)" $source
        if ($LASTEXITCODE -ne 0) { throw "Could not clone $($item.Repo)." }
        git -C $source checkout --quiet $item.Commit
        if ($LASTEXITCODE -ne 0) { throw "Could not check out $($item.Commit) of $($item.Repo)." }
        git -C $source apply --ignore-whitespace (Join-Path $repo "patches\$($item.Patch)")
        if ($LASTEXITCODE -ne 0) { throw "Could not apply $($item.Patch) to $($item.Repo)." }
        & $dotnet build $source -c Release --nologo -v q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Build of $($item.Repo) failed." }
        if (-not (Test-Path (Join-Path $modsDir "$($item.Id)\ModConfig.json"))) {
            throw "Build of $($item.Repo) succeeded but did not produce $($item.Id) in $modsDir."
        }
        Set-Content -Path $marker -Value $item.Patch -Encoding ascii
        $newlyInstalled.Add($item.Id)
        Write-Host "   $($item.Id): patched, built and installed"
    }

    Write-Step 'Smart Auto Battle (this repository)'
    $ownMod = 'ivalice.battle.smartautobattle'
    # A first install switches the mod on; after that the player's own on/off choice is kept.
    $alreadyThere = Test-Path (Join-Path $modsDir $ownMod)
    & $dotnet build (Join-Path $repo "mods\$ownMod") -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Building Smart Auto Battle failed. Close the game if it is running and try again.' }
    if (-not (Test-Path (Join-Path $modsDir "$ownMod\ModConfig.json"))) { throw "Smart Auto Battle was built but is missing from $modsDir." }
    if (-not $alreadyThere) { $newlyInstalled.Add($ownMod) }
    Write-Host "   ${ownMod}: built and installed"

    Write-Step 'Mods you download yourself (no redistribution licence)'
    $manual = @(
        @{ Id = 'fftivc.qol.turbo'; Pattern = '*Fast Forward*'; Url = 'https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/192' },
        @{ Id = 'fft.antiautorotate'; Pattern = '*AntiAutoRotate*'; Url = 'https://www.nexusmods.com/finalfantasytacticstheivalicechronicles/mods/173' }
    )
    foreach ($item in $manual) {
        if ((Test-Path (Join-Path $modsDir $item.Id)) -and -not $Force) { Write-Host "   $($item.Id): already installed"; continue }
        $archive = if ($ArchiveDir) {
            Get-ChildItem $ArchiveDir -File -Filter $item.Pattern | Where-Object { $_.Extension -in '.zip', '.7z' } |
                Sort-Object LastWriteTime | Select-Object -Last 1
        }
        if (-not $archive) {
            Write-Warning "$($item.Id): not installed. Download it from $($item.Url), keep the file name Nexus gives it, then rerun with -ArchiveDir pointing at the folder that holds it."
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
        # Keep the player's choices, but switch on what this run added along with what those mods need.
        $appConfig = Get-Content $appConfigPath -Raw | ConvertFrom-Json
        $enabled = New-Object System.Collections.Generic.List[string]
        $enabled.AddRange([string[]]@($appConfig.EnabledMods))
        foreach ($entry in $catalog.mods | Where-Object { $newlyInstalled -contains $_.id }) {
            foreach ($id in @($entry.requires) + $entry.id) {
                if (($installed -contains $id) -and -not ($enabled -contains $id)) { $enabled.Add($id) }
            }
        }
        if ($enabled.Count -ne @($appConfig.EnabledMods).Count) {
            $appConfig.EnabledMods = $enabled.ToArray()
            [IO.File]::WriteAllText($appConfigPath, ($appConfig | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding $false))
            Write-Host "   Already registered; enabled the newly installed mods"
        } else {
            Write-Host '   Already registered; enabled mods left as they are'
        }
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
