param([string]$PowerToysDir, [switch]$Install, [switch]$CompileFromSource)
$ErrorActionPreference = 'Stop'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnetCandidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $dotnetCandidate) { $dotnet = $dotnetCandidate }
}
if (-not $dotnet) { throw 'Das .NET SDK fehlt. Installiere das zur PowerToys-Version passende SDK von https://dotnet.microsoft.com/download.' }
if (-not $PowerToysDir) {
    $candidates = @("$env:LOCALAPPDATA\PowerToys", "$env:ProgramFiles\PowerToys")
    $PowerToysDir = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Wox.Plugin.dll') } | Select-Object -First 1
}
if (-not $PowerToysDir -or -not (Test-Path -LiteralPath (Join-Path $PowerToysDir 'Wox.Plugin.dll'))) { throw 'PowerToys Run nicht gefunden. Installiere PowerToys oder übergebe -PowerToysDir mit dem Ordner, der Wox.Plugin.dll enthält.' }
$runtimePath = Join-Path $PowerToysDir 'PowerToys.PowerLauncher.runtimeconfig.json'
$tfm = 'net10.0-windows10.0.22621.0'
if (Test-Path -LiteralPath $runtimePath) {
    $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -match '^net(\d+)\.') { $tfm = "net$($Matches[1]).0-windows10.0.22621.0" }
}
$prebuilt = Join-Path $PSScriptRoot 'dist\AIHub'
$out = Join-Path $PSScriptRoot 'build\AIHub'
$installSource = $out
if ($Install -and -not $CompileFromSource -and (Test-Path -LiteralPath (Join-Path $prebuilt 'Community.PowerToys.Run.Plugin.AIHub.dll'))) {
    $installSource = $prebuilt
    Write-Host 'Verwende den mitgelieferten, auf diesem Rechner freigegebenen Plugin-Host. Mit -CompileFromSource wird stattdessen neu kompiliert.'
} else {
    & $dotnet restore (Join-Path $PSScriptRoot 'AIHub.csproj') "-p:PowerToysDir=$PowerToysDir" "-p:TargetFramework=$tfm"
    if ($LASTEXITCODE -ne 0) { throw 'Restore fehlgeschlagen. Prüfe SDK und PowerToys-Version.' }
    & $dotnet build (Join-Path $PSScriptRoot 'AIHub.csproj') --no-restore -c Release "-p:PowerToysDir=$PowerToysDir" "-p:TargetFramework=$tfm" -o $out
    if ($LASTEXITCODE -ne 0) { throw 'Build fehlgeschlagen. Prüfe SDK und PowerToys-Version.' }
}
if ($Install) {
    if (Get-Process -Name PowerToys,PowerToys.PowerLauncher -ErrorAction SilentlyContinue) { throw "Bitte PowerToys vollständig beenden und das Skript erneut mit -Install ausführen. Die Installationsdateien liegen unter $installSource." }
    $destination = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\PowerToys Run\Plugins\AIHub'
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -Path (Join-Path $installSource '*') -Destination $destination -Recurse -Force
    $settingsPath = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\PowerToys Run\settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        $pluginSettings = $settings.plugins | Where-Object { $_.Id -eq 'F50D9A0DE73943768D8C77C065E7B6E9' } | Select-Object -First 1
        if ($pluginSettings) {
            $pluginSettings.ActionKeyword = '@'
            $pluginSettings.IsGlobal = $false
            $pluginSettings.Disabled = $false
            $settingsJson = $settings | ConvertTo-Json -Depth 100 -Compress
            [IO.File]::WriteAllText($settingsPath, $settingsJson, [Text.UTF8Encoding]::new($false))
        }
    }
    Write-Host "Installiert: $destination. PowerToys starten und AI Hub unter PowerToys Run aktivieren."
} else { Write-Host "Build fertig: $out" }
