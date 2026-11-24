param([Parameter(Mandatory=$true)][string]$ISCC)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& "$root/windows/build.ps1"
if($LASTEXITCODE){throw 'Windows app build failed'}
$dep=Join-Path $root 'dist/dependencies'
New-Item -ItemType Directory -Force -Path $dep | Out-Null
$bootstrap=Join-Path $dep 'MicrosoftEdgeWebview2Setup.exe'
if(!(Test-Path -LiteralPath $bootstrap)){Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $bootstrap}
$sig=Get-AuthenticodeSignature -LiteralPath $bootstrap
if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'WebView2 bootstrapper signature check failed'}
foreach($lang in 'en','zh'){
    & $ISCC /Qp "/DEdition=$lang" "$root/windows/setup/shelf.iss"
    if($LASTEXITCODE){throw "Installer compiler failed for $lang"}
}

