#define MyAppName "Cheems翻译"
#define MyAppVersion "0.0.3"
#define MyAppPublisher "萤灬虫"
#define MyAppExeName "CursorTranslator.exe"

[Setup]
AppId={{8F06FC4D-2A51-42E2-9AE8-E63A57EA74EF}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://cheems.cn
DefaultDirName={localappdata}\Programs\Cheems翻译
DefaultGroupName=Cheems翻译
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=CursorTranslator\Assets\translator_icon.ico
OutputDir=release\v0.0.3
OutputBaseFilename=CheemsTranslator-v0.0.3-Setup
VersionInfoVersion=0.0.3.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Cheems翻译 安装程序
VersionInfoProductName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ShowLanguageDialog=no

[Languages]
Name: "chsimp"; MessagesFile: "compiler:Default.isl"

[Messages]
chsimp.SetupAppTitle=安装程序
chsimp.SetupWindowTitle=安装向导 - %1
chsimp.UninstallAppTitle=卸载程序
chsimp.UninstallAppFullTitle=%1 卸载程序
chsimp.InformationTitle=提示
chsimp.ConfirmTitle=确认
chsimp.ErrorTitle=错误
chsimp.SetupAppRunningError=安装程序检测到 %1 正在运行。%n%n请关闭程序后单击“确定”继续，或单击“取消”退出安装程序。
chsimp.ButtonBack=< 上一步(&B)
chsimp.ButtonNext=下一步(&N) >
chsimp.ButtonInstall=安装(&I)
chsimp.ButtonOK=确定
chsimp.ButtonCancel=取消
chsimp.ButtonYes=是(&Y)
chsimp.ButtonNo=否(&N)
chsimp.ButtonFinish=完成(&F)
chsimp.ButtonBrowse=浏览(&B)...
chsimp.ClickNext=单击“下一步”继续，或单击“取消”退出安装程序。
chsimp.BeveledLabel=Cheems翻译
chsimp.WelcomeLabel1=欢迎使用 [name] 安装向导
chsimp.WelcomeLabel2=此向导将在您的计算机上安装 [name/ver]。%n%n建议您在开始安装前关闭其他程序。%n%n单击“下一步”继续。
chsimp.WizardSelectDir=选择安装位置
chsimp.SelectDirDesc=选择 [name] 的安装位置。
chsimp.SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
chsimp.SelectDirBrowseLabel=单击“下一步”继续，或单击“浏览”选择其他文件夹。
chsimp.WizardSelectTasks=选择附加任务
chsimp.SelectTasksDesc=要执行哪些附加任务？
chsimp.SelectTasksLabel2=选择要执行的附加任务，然后单击“下一步”。
chsimp.WizardReady=准备安装
chsimp.ReadyLabel1=安装程序已准备好在您的计算机上安装 [name]。
chsimp.ReadyLabel2a=单击“安装”开始安装，或单击“上一步”检查或更改设置。
chsimp.ReadyLabel2b=单击“安装”开始安装。
chsimp.WizardPreparing=正在准备安装
chsimp.PreparingDesc=安装程序正在准备安装 [name]。
chsimp.ApplicationsFound=以下程序正在使用需要更新的文件。建议允许安装程序自动关闭这些程序。
chsimp.ApplicationsFound2=以下程序正在使用需要更新的文件。安装程序将尝试自动关闭这些程序，并在安装完成后重新启动。
chsimp.CloseApplications=自动关闭程序(&A)
chsimp.DontCloseApplications=不关闭程序(&D)
chsimp.ErrorCloseApplications=安装程序无法自动关闭所有程序。请先手动关闭正在使用相关文件的程序，然后继续。
chsimp.WizardInstalling=正在安装
chsimp.InstallingLabel=请稍候，安装程序正在安装 [name]。
chsimp.FinishedHeadingLabel=安装完成
chsimp.FinishedLabelNoIcons=Cheems翻译已成功安装到您的计算机。
chsimp.FinishedLabel=Cheems翻译已成功安装。单击“完成”退出安装程序。
chsimp.ClickFinish=单击“完成”退出安装程序。
chsimp.StatusClosingApplications=正在关闭应用程序...
chsimp.StatusCreateDirs=正在创建文件夹...
chsimp.StatusExtractFiles=正在解压文件...
chsimp.StatusCreateIcons=正在创建快捷方式...
chsimp.StatusSavingUninstall=正在保存卸载信息...
chsimp.StatusRunProgram=正在完成安装...
chsimp.UninstallStatusLabel=请稍候，正在从计算机中删除 %1。
chsimp.UninstalledAll=已成功从计算机中删除 %1。

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "release\v0.0.3\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\Cheems翻译"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--show-settings"; WorkingDir: "{app}"
Name: "{autodesktop}\Cheems翻译"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--show-settings"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 Cheems翻译"; Parameters: "--show-settings"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
