$ErrorActionPreference = 'Stop'
$project=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$stage=Join-Path $project 'dist/build-windows'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item -LiteralPath "$project/app","$project/locales","$PSScriptRoot/src","$PSScriptRoot/lib" -Destination $stage -Recurse -Force
Copy-Item -Path "$PSScriptRoot/*.dll" -Destination $stage -Force
Push-Location -LiteralPath $stage
try {
$fx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$winmd = Join-Path $env:WINDIR 'System32/WinMetadata'
$refs = @('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll',
    "$fx/System.Runtime.WindowsRuntime.dll","$fx/WPF/UIAutomationClient.dll","$fx/WPF/UIAutomationTypes.dll","$fx/WPF/WindowsBase.dll",
    "$winmd/Windows.Foundation.winmd","$winmd/Windows.Media.winmd","$winmd/Windows.Storage.winmd","$winmd/Windows.System.winmd",'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll')
foreach ($assembly in @('System.Runtime','System.Runtime.InteropServices.WindowsRuntime')) {
    $refs += (Get-ChildItem "$env:WINDIR/Microsoft.NET/assembly/GAC_MSIL/$assembly" -Recurse -Filter "$assembly.dll" | Select-Object -First 1).FullName
}
Copy-Item 'lib/Shelf.UIAutomation.dll' $stage -Force
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/reference:Shelf.UIAutomation.dll','/utf8output','/optimize+','/win32icon:app/record.ico')
$compilerArgs += $refs | ForEach-Object { "/reference:$_" }
$sources = (Get-ChildItem src/*.cs).FullName
& "$fx/csc.exe" @compilerArgs '/out:Self Coverflow.exe' @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$edition = Join-Path $stage 'English'
New-Item -ItemType Directory -Force -Path "$edition/app","$edition/lib" | Out-Null
& "$fx/csc.exe" @compilerArgs '/define:ENGLISH' "/out:$edition/Self Coverflow.exe" @sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item 'app/record.ico','app/shelf.css','app/shelf.js','app/drive-sound.js','app/disc-drive.wav','locales/en/index.html','locales/en/strings.js' "$edition/app" -Force
Copy-Item -Path 'lib/*.txt' -Destination "$edition/lib" -Force
Copy-Item 'Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','Shelf.UIAutomation.dll' $edition -Force
} finally {
    Pop-Location
}
exit 0
