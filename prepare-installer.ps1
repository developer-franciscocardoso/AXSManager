$ErrorActionPreference = 'Stop'
$downloadFolder = Join-Path $PSScriptRoot '.tools\downloads'
$package = Join-Path $downloadFolder 'NDP48-x86-x64-AllOS-ENU.exe'
New-Item -ItemType Directory -Force -Path $downloadFolder | Out-Null
if (!(Test-Path -LiteralPath $package)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -UseBasicParsing `
        -Uri 'https://go.microsoft.com/fwlink/?linkid=2088631' `
        -OutFile $package
}
$expected = '0A3A390C47E639D0F7FC65B21195FEE6B7F65B066F80F70C60FAB191D14B7E40'
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $expected) {
    throw 'The runtime package differs from the verified release. Do not package it.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw 'Microsoft runtime signature verification failed.'
}
Write-Host 'Verified offline .NET Framework 4.8 package is ready.'
