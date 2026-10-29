param([switch]$Test, [switch]$Installer)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$output = Join-Path $projectRoot 'build'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    throw 'Install .NET Framework 4.8 before building.'
}
$references = @('System.dll', 'System.Core.dll', 'System.Xml.dll',
    'System.Drawing.dll', 'System.Windows.Forms.dll',
    'System.ServiceProcess.dll', 'System.Management.dll',
    'System.Security.dll') | ForEach-Object { '/reference:' + $_ }
$sources = @(Get-ChildItem (Join-Path $projectRoot 'src') -Filter '*.cs' |
    ForEach-Object { $_.FullName })
$common = @('/nologo', '/warn:4', '/warnaserror+', '/optimize+',
    '/platform:anycpu') + $references
& $compiler @common '/target:winexe' "/out:$output\AxsManager.exe" `
    "/win32manifest:$projectRoot\src\app.manifest" @sources
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
Copy-Item -LiteralPath "$projectRoot\src\App.config" `
    -Destination "$output\AxsManager.exe.config" -Force
if ($Test) {
    $tests = @(Get-ChildItem "$projectRoot\tests" -Filter '*.cs' |
        ForEach-Object { $_.FullName })
    & $compiler @common '/target:exe' '/main:AxsManager.Tests.TestRunner' `
        "/out:$output\AxsManager.Tests.exe" @sources @tests
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    Copy-Item -LiteralPath "$projectRoot\src\App.config" `
        -Destination "$output\AxsManager.Tests.exe.config" -Force
    & "$output\AxsManager.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if ($Installer) {
    & (Join-Path $projectRoot 'prepare-installer.ps1')
    $iscc = Join-Path $projectRoot '.tools\inno\ISCC.exe'
    if (!(Test-Path -LiteralPath $iscc)) {
        $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if (!$command) { throw 'Install Inno Setup 6 and put ISCC.exe on PATH.' }
        $iscc = $command.Source
    }
    & $iscc "$projectRoot\installer\AxsManager.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
}
Write-Host "Build ready: $output\AxsManager.exe"
