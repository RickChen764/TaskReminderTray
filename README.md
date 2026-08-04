# TaskReminderTray

一款轻量的 Windows 任务栏个人任务与 Bug 排期提醒工具，可连接 Plane 企业分支的“我的工单”视图。

## 功能

- 在任务栏通知区域左侧常驻显示当前开发任务和后续数量。
- Hover 展示本周每日工作安排，长标题自动滚动且采用双缓冲避免闪烁。
- 按开发、待跟进、等待输入整理工作，并识别任务/Bug、状态和 S/A/B/C 优先级。
- 状态变化使用持久通知，只有点击“已知晓”或关闭按钮才会清除；重启后仍会提醒。
- 工单标题仅用于展示；每行常驻“打开工单”和“复制单信息”按钮，并支持右键复制编号或标题。
- 每日存在多项安排时可按天展开全部工单。
- 可将任意工单设为当前重点，并设置 30 分钟、1 小时或次日等稍后提醒。
- 支持临期、当天到期和逾期提醒。
- 支持账号密码登录或 Access Token，凭据使用 Windows DPAPI 加密。
- 自动检查 GitHub Releases，经用户确认后下载、校验、替换并自动重启。
- 能与 UsageTray 并列运行，自动排列在其左侧。

## 配置

启动后双击工具条或通过右键菜单打开设置，填写 Plane 工作区视图地址，例如：

```text
https://plane.example.com/workspace-slug/workspace-views/view-id
```

未配置凭据时，工具条只显示“待配置”，Hover 不展示详情。

本地配置和运行状态保存在 `%LOCALAPPDATA%\TaskReminderTray`，不会写入工程目录。密码和 Token 仅以 DPAPI 加密形式保存。

## 开发

需要 .NET 8 SDK 和 Windows：

```powershell
dotnet build .\TaskReminderTray.slnx
dotnet test .\TaskReminderTray.slnx
dotnet run --project .\TaskReminderTray\TaskReminderTray.csproj
```

## 打包

```powershell
dotnet publish .\TaskReminderTray\TaskReminderTray.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\artifacts\portable-win-x64
```

## 发布与自动更新

应用从 [GitHub Releases](https://github.com/RickChen764/TaskReminderTray/releases) 检查更新。推送与项目版本相同的 tag（例如 `v1.0.0`）后，GitHub Actions 会运行测试并创建以下 Release 资产：

```text
TaskReminderTray-win-x64.exe
TaskReminderTray-win-x64.exe.sha256
```

客户端启动后检查一次，之后每 6 小时检查。安装前会验证 HTTPS 来源、SHA-256 和文件版本；替换失败时自动恢复旧版本。
