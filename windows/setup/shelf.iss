#ifndef Edition
  #define Edition "en"
#endif
#if Edition == "en"
  #define EditionName "English"
  #define SourceDir "..\..\dist\build-windows\English"
#else
  #define EditionName "简体中文"
  #define SourceDir "..\..\dist\build-windows"
#endif

[Setup]
AppId=io.github.flapflops3.selfcoverflow.{#Edition}
AppName=Self Coverflow
AppVersion=1.0.0-beta.4
VersionInfoVersion=1.0.0.4
AppVerName=Self Coverflow 1.0 Beta 4 · {#EditionName}
AppPublisher=Flapflops3
AppPublisherURL=https://github.com/Flapflops3/Self-Coverflow
AppSupportURL=https://github.com/Flapflops3/Self-Coverflow/issues
DefaultDirName={localappdata}\Programs\Self Coverflow\{#Edition}
DefaultGroupName=Self Coverflow
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.19045
OutputDir=..\..\dist
OutputBaseFilename=Self-Coverflow-Windows-x64-{#Edition}-Setup
SetupIconFile=..\..\app\record.ico
UninstallDisplayIcon={app}\Self Coverflow.exe
UninstallDisplayName=Self Coverflow ({#EditionName})
WizardStyle=modern
WizardSizePercent=110
WizardImageFile=sleeves.bmp
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableDirPage=auto
Compression=lzma2
SolidCompression=yes
ShowLanguageDialog=no
UsePreviousLanguage=no
CloseApplications=yes

[Languages]
#if Edition == "en"
Name: "en"; MessagesFile: "compiler:Default.isl"
#else
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
#endif

[Messages]
#if Edition == "en"
WelcomeLabel1=Self Coverflow
WelcomeLabel2=Your Apple Music albums, on your desktop.%n%nThis guided setup checks the playback components and installs the English edition for your Windows account.
FinishedHeadingLabel=Your shelf is ready
FinishedLabel=Open Apple Music, sign in and let your library sync. Launch Self Coverflow, select All Albums and double-click a cover. The song title appears when Music starts playing.
#else
WelcomeLabel1=Self Coverflow
WelcomeLabel2=把 Apple Music 的唱片放在桌面上。%n%n向导会检查播放所需的组件，为当前 Windows 用户安装简体中文版。
FinishedHeadingLabel=唱片架已就绪
FinishedLabel=打开 Apple Music，登录并等待资料库同步。启动 Self Coverflow，切换到「全部专辑」，双击一张封面。开始播放后，底部会显示曲名。
#endif

[Tasks]
Name: "desktop"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
Name: "follow"; Description: "{code:FollowLabel}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\Self Coverflow.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs
Source: "{#SourceDir}\lib\*.txt"; DestDir: "{app}\lib"; Flags: ignoreversion
Source: "..\..\dist\dependencies\MicrosoftEdgeWebview2Setup.exe"; Flags: dontcopy

[Icons]
Name: "{group}\Self Coverflow ({#EditionName})"; Filename: "{app}\Self Coverflow.exe"
Name: "{autodesktop}\Self Coverflow ({#EditionName})"; Filename: "{app}\Self Coverflow.exe"; Tasks: desktop
Name: "{userstartup}\Self Coverflow ({#EditionName})"; Filename: "{app}\Self Coverflow.exe"; Parameters: "--watch-music"; Tasks: follow

[Run]
Filename: "{app}\Self Coverflow.exe"; Description: "{code:LaunchLabel}"; Flags: postinstall nowait skipifsilent

[Code]
var
  CheckPage: TWizardPage;
  RuntimeText, MusicText: TNewStaticText;
  StoreButton, RecheckButton: TNewButton;

function L(En, Zh: String): String;
begin
  if ActiveLanguage='en' then Result:=En else Result:=Zh;
end;

function FollowLabel(Param: String): String;
begin Result:=L('Follow Apple Music after Windows sign-in (choose one language edition)','登录 Windows 后随 Apple Music 启动（只需选择一个语言版本）'); end;
function LaunchLabel(Param: String): String;
begin Result:=L('Open Self Coverflow','打开 Self Coverflow'); end;

function HasRuntime: Boolean;
var version,key: String;
begin
  key:='Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  Result:=(RegQueryStringValue(HKLM32,key,'pv',version) and (version<>'') and (version<>'0.0.0.0')) or
          (RegQueryStringValue(HKCU32,key,'pv',version) and (version<>'') and (version<>'0.0.0.0'));
end;

function HasMusic: Boolean;
var code: Integer;
begin
  Result:=Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-AppxPackage -Name AppleInc.AppleMusicWin) {exit 0} else {exit 1}"',
    '',SW_HIDE,ewWaitUntilTerminated,code) and (code=0);
end;

procedure Refresh(Sender:TObject);
begin
  if HasRuntime then RuntimeText.Caption:=L('WebView2 Runtime is installed.','WebView2 运行时已安装。')
  else RuntimeText.Caption:=L('Setup will install the Microsoft WebView2 Runtime. An internet connection is required.','向导将联网安装 Microsoft WebView2 运行时。');
  if HasMusic then MusicText.Caption:=L('Apple Music is installed. Make sure Library → Albums contains your music.','检测到 Apple Music。请确认「资料库 → 专辑」中能看到自己的唱片。')
  else MusicText.Caption:=L('Install Apple Music from the Store and sign in before using the shelf.','请先从微软商店安装 Apple Music，再登录并同步资料库。');
end;
procedure OpenStore(Sender:TObject);
var code:Integer;
begin ShellExec('open','ms-windows-store://pdp/?productid=9PFHDD62MXS1','','',SW_SHOWNORMAL,ewNoWait,code);end;
function LabelAt(Y:Integer; Caption:String; Bold:Boolean):TNewStaticText;
begin
  Result:=TNewStaticText.Create(CheckPage);
  Result.Parent:=CheckPage.Surface;Result.AutoSize:=False;Result.WordWrap:=True;
  Result.SetBounds(0,ScaleY(Y),CheckPage.SurfaceWidth,ScaleY(38));Result.Caption:=Caption;
  if Bold then begin Result.Font.Style:=[fsBold];Result.Font.Size:=12; end;
end;
procedure InitializeWizard;
begin
  WizardForm.WelcomeLabel1.Font.Size:=24;
  WizardForm.FinishedHeadingLabel.Font.Size:=22;
  CheckPage:=CreateCustomPage(wpWelcome,L('Prepare your music library','准备音乐资料库'),L('Two components bring the shelf to life.','确认以下两项，即可开始使用。'));
  LabelAt(4,'Apple Music',True);
  MusicText:=LabelAt(32,'',False);
  StoreButton:=TNewButton.Create(CheckPage);StoreButton.Parent:=CheckPage.Surface;
  StoreButton.SetBounds(0,ScaleY(76),ScaleX(156),ScaleY(26));
  StoreButton.Caption:=L('Open Microsoft Store','打开微软商店');StoreButton.OnClick:=@OpenStore;
  LabelAt(124,'Microsoft Edge WebView2',True);
  RuntimeText:=LabelAt(152,'',False);
  RecheckButton:=TNewButton.Create(CheckPage);RecheckButton.Parent:=CheckPage.Surface;
  RecheckButton.SetBounds(0,ScaleY(198),ScaleX(120),ScaleY(26));RecheckButton.Caption:=L('Check again','重新检查');RecheckButton.OnClick:=@Refresh;
end;
procedure CurPageChanged(PageID:Integer);
begin if PageID=CheckPage.ID then Refresh(nil); end;
function PrepareToInstall(var NeedsRestart:Boolean):String;
var code:Integer;
begin
  Result:='';
  if HasRuntime then exit;
  ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'),'/silent /install','',SW_HIDE,ewWaitUntilTerminated,code) or not HasRuntime then
    Result:=L('WebView2 could not be installed. Connect to the internet or install Microsoft Edge WebView2 Evergreen Runtime, then try again.',
    'WebView2 运行时安装失败。请联网或先安装 Microsoft Edge WebView2 Evergreen Runtime，再重试。');
end;

