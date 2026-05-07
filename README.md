# SVNFileBox

Windows SVN 同步客户端，基于 .NET 8 + WPF 构建。

本地文件变更自动 commit 到 SVN，服务器更新自动 pull 到本地，保持工作副本始终同步。

## 功能

- 📁 **仓库管理** — 添加本地工作副本 / 从 URL checkout
- 🔄 **自动同步** — 文件变化自动 commit；服务器更新自动 update
- 📋 **同步记录** — 查看每次同步的时间、文件和结果
- 🗂️ **文件浏览** — 双击进入目录，路径栏导航
- ⚙️ **设置** — 同步周期、代理、开机启动、托盘

## 依赖

- .NET 8.0 SDK
- WPF
- SVN CLI (`svn`)

## 构建

```powershell
cd src
dotnet build -c Release
```

运行：`bin/Release/net8.0-windows/win-x64/SVNFileBox.exe`

## 技术栈

- .NET 8.0 + WPF
- CommunityToolkit.Mvvm（MVVM 框架）
- Serilog（日志）
- Hardcodet.NotifyIcon.Wpf（托盘图标）
- SVN CLI 封装（System.Diagnostics.Process）
- QFileSystemWatcher 文件监控 + 定时轮询
