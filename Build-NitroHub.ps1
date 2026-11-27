param(
    [string]$Version = '1.0.0',
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { throw "C# compiler not found: $compiler" }

# Derive a 4-part numeric version (e.g. "1.2.0" -> "1.2.0.0") for Windows file/assembly metadata.
$core = ($Version -split '[-+]', 2)[0]
$parts = $core -split '\.'
if ($parts.Count -lt 2 -or $parts.Count -gt 4 -or ($parts | Where-Object { $_ -notmatch '^\d+$' })) {
    throw "Invalid version '$Version'. Use a format like 1.0.0 or 1.2.3."
}
while ($parts.Count -lt 4) { $parts += '0' }
$numericVersion = ($parts -join '.')

$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$sourceFile = Join-Path $root 'src\NitroHubApp.cs'
$manifestFile = Join-Path $root 'src\NitroHub.manifest'
$iconFile = Join-Path $root 'assets\NitroHub.ico'
$logoFile = Join-Path $root 'assets\NitroHubLogo.png'
$localesDir = Join-Path $root 'locales'
$outputExe = Join-Path $distDir 'NitroHub.exe'
$uninstallerSource = Join-Path $root 'installer\NitroHubUninstaller.cs'
$uninstallerExe = Join-Path $distDir 'NitroHub-Uninstaller.exe'

$localeResourceArgs = @()
if (Test-Path $localesDir) {
    $localeFiles = Get-ChildItem -Path $localesDir -Filter '*.txt' -File |
        Where-Object { $_.Name -notlike '*.template.txt' } |
        Sort-Object Name
    foreach ($lf in $localeFiles) {
        $localeResourceArgs += "/resource:$($lf.FullName),locale.$($lf.Name)"
    }
}

$versionFile = Join-Path $env:TEMP ("NitroHub-AssemblyInfo-" + [Guid]::NewGuid().ToString('N') + '.cs')
try {
    $escapedInfo = $Version.Replace('\', '\\').Replace('"', '\"')
    @(
        'using System.Reflection;'
        "[assembly: AssemblyVersion(`"$numericVersion`")]"
        "[assembly: AssemblyFileVersion(`"$numericVersion`")]"
        "[assembly: AssemblyInformationalVersion(`"$escapedInfo`")]"
    ) | Set-Content -Path $versionFile -Encoding UTF8

    & $compiler /nologo /target:winexe /platform:anycpu "/out:$outputExe" `
        "/win32manifest:$manifestFile" "/win32icon:$iconFile" `
        "/resource:$logoFile,NitroHubLogo.png" `
        @localeResourceArgs `
        /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
        /reference:System.Management.dll /reference:System.Windows.Forms.dll `
        $sourceFile $versionFile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Built dist\NitroHub.exe (v$Version, $($localeResourceArgs.Count + 1) languages)"

    if ($Installer -and (Test-Path $uninstallerSource)) {
        & $compiler /nologo /target:winexe /platform:anycpu "/out:$uninstallerExe" `
            "/win32manifest:$manifestFile" "/win32icon:$iconFile" `
            /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
            $uninstallerSource $versionFile
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "Built dist\NitroHub-Uninstaller.exe (v$Version)"
    }
}
finally {
    if (Test-Path $versionFile) { Remove-Item $versionFile -Force -ErrorAction SilentlyContinue }
}

if ($Installer) {
    $isccCandidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }

    $iscc = $isccCandidates | Select-Object -First 1
    if (-not $iscc) {
        throw "Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup"
    }

    $issFile = Join-Path $root 'installer\NitroHub.iss'
    & $iscc "/DMyAppVersion=$Version" "/DMyAppFileVersion=$numericVersion" $issFile
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Host "Built dist\NitroHub-Setup-$Version.exe"
}
