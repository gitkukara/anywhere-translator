#ifndef AppVersion
  #error AppVersion must be supplied by package-setup.ps1
#endif
#define RepoRoot AddBackslash(SourcePath) + "..\..\"

[Setup]
AppId={{A8F0536C-37DA-44CF-9A12-04642B1137C1}
AppName=Anywhere Translator
AppVersion={#AppVersion}
AppPublisher=Anywhere Translator
AppPublisherURL=https://github.com/gitkukara/anywhere-translator
DefaultDirName={localappdata}\Programs\Anywhere Translator
DefaultGroupName=Anywhere Translator
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#RepoRoot}dist
OutputBaseFilename=Anywhere-Translator-v{#AppVersion}-win-x64-Setup
SetupIconFile={#RepoRoot}src\TranslatorAnywhere\Assets\app.ico
UninstallDisplayIcon={app}\Anywhere Translator.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#RepoRoot}LICENSE
CloseApplications=yes
RestartApplications=no
AppMutex=Local\TranslatorAnywhere.Desktop.v1

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#RepoRoot}dist\Anywhere-Translator-v{#AppVersion}-win-x64.exe"; DestDir: "{app}"; DestName: "Anywhere Translator.exe"; Flags: ignoreversion
Source: "installed-mode"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}docs\INSTALL.txt"; DestDir: "{app}"; DestName: "README.txt"; Flags: ignoreversion
Source: "{#RepoRoot}LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Anywhere Translator"; Filename: "{app}\Anywhere Translator.exe"
Name: "{autodesktop}\Anywhere Translator"; Filename: "{app}\Anywhere Translator.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Anywhere Translator.exe"; Description: "Launch Anywhere Translator"; Flags: nowait postinstall skipifsilent

[Code]
const
  DotNetUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0';
  WebViewUrl = 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/';
  DesktopKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  WebViewKey = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
var
  RuntimePage: TWizardPage;
  RuntimeStatus: TNewStaticText;

function DesktopInstalledAt(Root: Integer): Boolean;
var Names: TArrayOfString; I, Patch: Integer; Installed: Cardinal;
begin
  Result := False;
  if RegGetValueNames(Root, DesktopKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 5) = '10.0.' then begin
        Patch := StrToIntDef(Copy(Names[I], 6, Length(Names[I])), -1);
        if (Patch >= 0) and RegQueryDWordValue(Root, DesktopKey, Names[I], Installed) then
          if Installed = 1 then Result := True;
      end;
end;

function DesktopInstalled: Boolean;
begin
  Result := DesktopInstalledAt(HKLM64) or DesktopInstalledAt(HKLM32);
end;

function WebViewInstalledAt(Root: Integer): Boolean;
var Version: String; Packed: Int64;
begin
  Result := False;
  if RegQueryStringValue(Root, WebViewKey, 'pv', Version) then
    if StrToVersion(Version, Packed) then Result := Packed > 0;
end;

function WebViewInstalled: Boolean;
begin
  Result := WebViewInstalledAt(HKLM32) or WebViewInstalledAt(HKCU32) or WebViewInstalledAt(HKCU64);
end;

function MissingRuntimes: String;
begin
  Result := '';
  if not DesktopInstalled then Result := '.NET 10 Desktop Runtime (Windows x64)';
  if not WebViewInstalled then begin
    if Result <> '' then Result := Result + #13#10;
    Result := Result + 'Microsoft Edge WebView2 Evergreen Runtime';
  end;
end;

procedure RefreshRuntimes(Sender: TObject);
var Missing: String;
begin
  Missing := MissingRuntimes;
  if Missing = '' then RuntimeStatus.Caption := 'Required runtimes are installed. Click Next to continue.' + #13#10 + '运行环境已安装，点击 Next 继续。'
  else RuntimeStatus.Caption := 'Missing / 缺少：' + #13#10 + Missing + #13#10#13#10 +
    'Open the official download pages below, install the missing runtimes, then click Check again.' + #13#10 +
    '打开下方微软官方下载页，安装缺失的运行环境，再点击 Check again。' + #13#10 +
    'Choose .NET Desktop Runtime > Windows x64; the SDK is not needed.';
end;

procedure DownloadRuntime(Sender: TObject);
var Url: String; ErrorCode: Integer;
begin
  if TNewButton(Sender).Tag = 1 then Url := DotNetUrl else Url := WebViewUrl;
  if not ShellExec('open', Url, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
    MsgBox('Open this official download page in your browser:' + #13#10 + Url, mbInformation, MB_OK);
end;

procedure AddRuntimeButton(Caption: String; Top, Tag: Integer);
var Button: TNewButton;
begin
  Button := TNewButton.Create(RuntimePage);
  Button.Parent := RuntimePage.Surface;
  Button.SetBounds(0, ScaleY(Top), RuntimePage.SurfaceWidth, ScaleY(28));
  Button.Caption := Caption;
  Button.Tag := Tag;
  if Tag = 0 then Button.OnClick := @RefreshRuntimes else Button.OnClick := @DownloadRuntime;
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateCustomPage(wpWelcome, 'Runtime requirements / 运行环境',
    'Setup does not bundle .NET or WebView2 / 安装包不内置运行时');
  RuntimeStatus := TNewStaticText.Create(RuntimePage);
  RuntimeStatus.Parent := RuntimePage.Surface;
  RuntimeStatus.SetBounds(0, 0, RuntimePage.SurfaceWidth, ScaleY(145));
  RuntimeStatus.AutoSize := False;
  RuntimeStatus.WordWrap := True;
  AddRuntimeButton('Download .NET 10 Desktop Runtime (x64)', 150, 1);
  AddRuntimeButton('Download WebView2 Evergreen Runtime', 184, 2);
  AddRuntimeButton('Check again / 重新检测', 218, 0);
  RefreshRuntimes(nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = RuntimePage.ID) and not WizardSilent then begin
    RefreshRuntimes(nil);
    Result := MissingRuntimes = '';
    if not Result then MsgBox('Install the missing runtimes, then check again.' + #13#10 +
      '请先安装缺失的运行环境，再重新检测。', mbInformation, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := MissingRuntimes;
  if Result <> '' then Result := 'Required runtimes are missing:' + #13#10 + Result + #13#10 + DotNetUrl + #13#10 + WebViewUrl;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Shell, Link: Variant; Shortcut: String;
begin
  if CurUninstallStep = usUninstall then begin
    // Remove only a startup shortcut belonging to this installed executable.
    // Leave portable registrations and all user data untouched.
    Shortcut := ExpandConstant('{userstartup}\TranslatorAnywhere.lnk');
    if FileExists(Shortcut) then
      try
        Shell := CreateOleObject('WScript.Shell');
        Link := Shell.CreateShortcut(Shortcut);
        if CompareText(Link.TargetPath, ExpandConstant('{app}\Anywhere Translator.exe')) = 0 then
          DeleteFile(Shortcut);
      except
        Log('Could not inspect the startup shortcut; leaving it unchanged.');
      end;
  end;
end;
