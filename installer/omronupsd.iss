; dotnet publish OmronUpsd -c Release -o publish の出力からインストーラーを作成

#define PublishDir "..\publish"
#define AppExe "omronupsd.exe"
#define ServiceName "omronupsd"
#define FullVersion GetStringFileInfo(PublishDir + "\" + AppExe, "ProductVersion")
#if Pos("+", FullVersion) > 0
  #define AppVersion Copy(FullVersion, 1, Pos("+", FullVersion) - 1)
#else
  #define AppVersion FullVersion
#endif

[Setup]
AppId={{68726E1C-215F-4E24-89AF-BD1F524CF24E}
AppName=omronupsd
AppVersion={#AppVersion}
UninstallDisplayName=omronupsd
AppPublisher=njm2360
DefaultDirName={autopf}\omronupsd
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=omronupsd-{#AppVersion}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; 更新時は既存の設定を維持
Source: "{#PublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Code]
const
  RoleMaster = 0;
  RoleClient = 1;
  ErrorServiceDoesNotExist = 1060;

var
  RolePage: TInputOptionWizardPage;
  KeyPage: TInputQueryWizardPage;
  MasterPage: TInputQueryWizardPage;
  ClientPage: TInputQueryWizardPage;
  ConfigExisted: Boolean;

function ConfigPath: String;
begin
  Result := ExpandConstant('{app}\appsettings.json');
end;

function IsMaster: Boolean;
begin
  Result := RolePage.SelectedValueIndex = RoleMaster;
end;

function ExecHidden(const FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function ServiceExists: Boolean;
begin
  Result := ExecHidden(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}') <> ErrorServiceDoesNotExist;
end;

// sc.exe stop は停止完了を待たないため、完了まで待機する Stop-Service を使用
procedure StopService;
begin
  ExecHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Stop-Service -Name {#ServiceName} -ErrorAction SilentlyContinue"');
end;

procedure InitializeWizard;
begin
  RolePage := CreateInputOptionPage(wpSelectDir, '動作モード', '動作モードを選択してください。', '', True, False);
  RolePage.Add('マスター (UPS 接続機)');
  RolePage.Add('クライアント');
  RolePage.SelectedValueIndex := RoleMaster;

  KeyPage := CreateInputQueryPage(RolePage.ID, '認証設定', '共有鍵を入力してください。',
    '全台で同一の値を指定します。16 文字以上。');
  KeyPage.Add('共有鍵:', False);

  MasterPage := CreateInputQueryPage(KeyPage.ID, 'クライアント設定', '監視対象のクライアント名を入力してください。',
    'カンマ区切りで指定します。省略可。');
  MasterPage.Add('クライアント名:', False);

  ClientPage := CreateInputQueryPage(KeyPage.ID, 'マスター設定', '接続先マスターを入力してください。', '');
  ClientPage.Add('ホスト名または IP アドレス:', False);
  ClientPage.Add('クライアント名 (省略時はコンピューター名):', False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = RolePage.ID) or (PageID = KeyPage.ID) or (PageID = MasterPage.ID) or (PageID = ClientPage.ID) then
    Result := FileExists(ConfigPath);
  if (PageID = MasterPage.ID) and not IsMaster then
    Result := True;
  if (PageID = ClientPage.ID) and IsMaster then
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Key: String;
begin
  Result := True;
  if CurPageID = KeyPage.ID then
  begin
    Key := Trim(KeyPage.Values[0]);
    if (Length(Key) < 16) or (Pos('CHANGE-ME', Key) = 1) then
    begin
      MsgBox('共有鍵は 16 文字以上で指定してください。', mbError, MB_OK);
      Result := False;
    end;
  end
  else if (CurPageID = ClientPage.ID) and (Trim(ClientPage.Values[0]) = '') then
  begin
    MsgBox('ホスト名または IP アドレスを指定してください。', mbError, MB_OK);
    Result := False;
  end;
end;

function JsonString(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

function JsonStringArray(const Csv: String): String;
var
  Rest, Item: String;
  P: Integer;
begin
  Result := '';
  Rest := Csv;
  while Rest <> '' do
  begin
    P := Pos(',', Rest);
    if P = 0 then
    begin
      Item := Trim(Rest);
      Rest := '';
    end
    else
    begin
      Item := Trim(Copy(Rest, 1, P - 1));
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    if Item <> '' then
    begin
      if Result <> '' then
        Result := Result + ', ';
      Result := Result + JsonString(Item);
    end;
  end;
  Result := '[' + Result + ']';
end;

// キー名で対象行を特定して値を置換。appsettings.json が 1 行 1 キーの形式で、対象のキー名がファイル内で一意であることが前提
procedure ReplaceValue(var Lines: TArrayOfString; const Key, Value: String);
var
  I, P: Integer;
  Line, Rest: String;
begin
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    P := Pos('"' + Key + '":', Line);
    if (P > 0) and (Trim(Copy(Line, 1, P - 1)) = '') then
    begin
      Rest := Trim(Copy(Line, P + Length(Key) + 3, Length(Line)));
      if (Rest <> '') and (Rest[Length(Rest)] = ',') then
        Lines[I] := Copy(Line, 1, P + Length(Key) + 2) + ' ' + Value + ','
      else
        Lines[I] := Copy(Line, 1, P + Length(Key) + 2) + ' ' + Value;
      Exit;
    end;
  end;
  RaiseException('appsettings.json に ' + Key + ' がありません');
end;

procedure WriteConfig;
var
  Lines: TArrayOfString;
begin
  if not LoadStringsFromFile(ConfigPath, Lines) then
    RaiseException('appsettings.json を読み込めません');
  ReplaceValue(Lines, 'SharedKey', JsonString(Trim(KeyPage.Values[0])));
  if IsMaster then
  begin
    ReplaceValue(Lines, 'Role', '"Master"');
    ReplaceValue(Lines, 'ExpectedClients', JsonStringArray(MasterPage.Values[0]));
  end
  else
  begin
    ReplaceValue(Lines, 'Role', '"Client"');
    ReplaceValue(Lines, 'MasterHost', JsonString(Trim(ClientPage.Values[0])));
    ReplaceValue(Lines, 'Name', JsonString(Trim(ClientPage.Values[1])));
  end;
  if not SaveStringsToUTF8FileWithoutBOM(ConfigPath, Lines, False) then
    RaiseException('appsettings.json を書き込めません');
end;

// 共有鍵を含むため SYSTEM と Administrators 以外の読み取りを禁止
procedure RestrictConfigAccess;
begin
  ExecHidden(ExpandConstant('{sys}\icacls.exe'),
    '"' + ConfigPath + '" /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F');
end;

// 待受ポートの変更に追従するため、ポートではなく実行ファイルを許可
procedure AddFirewallRule;
begin
  ExecHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#ServiceName}"');
  ExecHidden(ExpandConstant('{sys}\netsh.exe'),
    'advfirewall firewall add rule name="{#ServiceName}" dir=in action=allow protocol=TCP program="' +
    ExpandConstant('{app}\{#AppExe}') + '"');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ConfigExisted := FileExists(ConfigPath);
  if ServiceExists then
    StopService;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;
  if not ConfigExisted then
  begin
    WriteConfig;
    RestrictConfigAccess;
    if IsMaster then
      AddFirewallRule;
  end;
  if not ServiceExists then
    if ExecHidden(ExpandConstant('{app}\{#AppExe}'), 'install') <> 0 then
      MsgBox('サービスの登録に失敗しました。', mbError, MB_OK);
  if ExecHidden(ExpandConstant('{sys}\sc.exe'), 'start {#ServiceName}') <> 0 then
    MsgBox('サービスの開始に失敗しました。%ProgramData%\omronupsd\logs のログを確認してください。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  StopService;
  ExecHidden(ExpandConstant('{app}\{#AppExe}'), 'uninstall');
  ExecHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#ServiceName}"');
end;
