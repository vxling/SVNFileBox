！！注意： 本项目完全由AI开发，使用Hermes+MiniMax2.7模型开发，无人工代码！！！

# SVNFileBox

Windows SVN 同步客户端，类似 Dropbox，自动同步文档，last-write-win 的手工决策冲突机制，基于 .NET 10 + WPF 构建。

本地文件变更自动 commit 到 SVN，服务器更新自动 pull 到本地，保持工作副本始终同步。

<img width="1354" height="710" alt="image" src="https://github.com/user-attachments/assets/6b02608c-5a24-4c31-bb56-6015f864ba54" />


## 功能

- 📁 **仓库管理** — 添加本地工作副本 / 从 URL checkout
- 🔄 **自动同步** — 文件变化自动 commit；服务器更新自动 update
- 📋 **同步记录** — 查看每次同步的时间、文件和结果
- 🗂️ **文件浏览** — 双击进入目录，路径栏导航
- ⚙️ **设置** — 同步周期、代理、开机启动、托盘

## 依赖

- .NET 10.0 SDK
- WPF
- SharpSvn 1.14005.390（原生 .NET SVN 绑定，无需 svn CLI）

## 构建

```powershell
cd src
dotnet build -c Release
```

运行：`bin/Release/net10.0-windows/win-x64/SVNFileBox.exe`

## 下载

- **便携版** `SVNFileBox-win64.zip`：绿色版，解压即用，内置 .NET 10 运行时
- **安装包** `SVNFileBox-Setup.exe`：安装向导，支持开始菜单/桌面快捷方式/卸载程序

## 技术栈

- .NET 10.0 + WPF + Fluent UI（内置 `PresentationFramework.Fluent`）
- CommunityToolkit.Mvvm（MVVM 框架）
- Serilog（日志）
- Hardcodet.NotifyIcon.Wpf（托盘图标）
- SharpSvn 1.14005.390（SVN 操作）
- 定时轮询（本地变化检测 + 服务器更新拉取）+ 全量安全兜底扫描
