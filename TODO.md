# SVNFileBox 开发任务列表

> 最后更新: 2026-05-01

---

## 项目路径

- **SVNFileBox** (WPF 主程序): `~/aiworks/projects/repos/SVNFileBox/`
  - 工作副本 SVN 地址: https://66.154.112.116/repos/SVNFileBox
  - 用户名: agent / lobster123
  - 本地配置目录: `%APPDATA%/SVNFileBox/`
  - 日志目录: `%APPDATA%/SVNFileBox/logs/`
  - 构建输出: `src/bin/Debug/net8.0-windows/` 或 `src/bin/Release/net8.0-windows/`

- **SVNFileManager1** (WPF 文件管理器原型): `~/aiworks/projects/repos/SVNFileManager1/`

---

## 一、功能项

### ✅ 已完成
- [x] 添加本地仓库
- [x] 添加网络仓库（Checkout）
- [x] 仓库列表持久化存储（JSON）
- [x] 删除仓库（从列表移除，不动 SVN 端）
- [x] FileSystemWatcher 监控 + 5秒防抖自动 commit
- [x] 下行轮询同步（定时 svn update）
- [x] 文件列表图标（按扩展名）
- [x] 文件列表列（Type/Name/Status/Size/Modified）
- [x] 文件状态列彩色徽章（调用 GetStatusAsync）
- [x] 拖拽文件到列表 + svn add
- [x] Ctrl+V 粘贴文件 + svn add（含覆盖确认框）
- [x] 新建文件夹 + svn add
- [x] 删除文件 + svn commit
- [x] 重命名 + svn commit
- [x] 在资源管理器中打开
- [x] 复制路径
- [x] 双击打开文件（系统默认程序）
- [x] 重复仓库检查（路径重复/URL 重复）
- [x] 设置页面基础功能
- [x] 同步记录持久化（JSON 文件，零依赖）
- [x] 同步记录查看 UI（点击切换文件列表/同步记录列表）

### ⏳ 待完成
- [x] **冲突处理（Last-Write-Wins）** — `HandleConflictsAsync` 检测冲突文件（status='C'），比对本地vs服务器时间戳，保留较新版本
- [x] **最小化到托盘** — 窗口关闭隐藏到托盘，双击托盘/菜单恢复，退出菜单完全关闭应用
- [x] **密码加密存储** — DPAPI（CurrentUser scope），`DpapiService` 加解密，ConfigService load/save 自动加解密，兼容明文旧密码

---

## 二、优先级建议

| 优先级 | 任务 | 原因 |
|--------|------|------|
| P2 | 冲突处理 Last-Write-Wins | 多设备场景核心功能 |
| P2 | 最小化到托盘 | 用户体验 |
| P3 | 密码 DPAPI 加密 | 安全 |

---

## 三、已完成记录

- Rev 228: Code review 全修复（SyncService wiring，CommitAsync 传密码，.svn 过滤修正，Repository.Password，TrayIcon null-guard）
- Rev 225: DPAPI 密码加密（DpapiService + ConfigService 集成）
- Rev 223: 最小化到托盘（Hardcodet.NotifyIcon.Wpf TaskbarIcon，OnClosing hide，BalloonTip，托盘菜单）
- Rev 222: Last-Write-Wins 冲突处理（HandleConflictsAsync）
- Rev 220: 同步记录查看 UI（ToggleSyncRecordsView + SyncRecordDisplay + BoolToCollapsedConverter）
- Rev 219: TODO.md: mark sync record persistence done
- Rev 218: 同步记录 JSON 持久化（SyncRecord + SyncRecordService）
- Rev 217: 粘贴/拖拽覆盖确认框；双击打开文件
- Rev 215: P0 右键菜单修复（重命名/删除/新建/粘贴/拖拽全部补 svn sync）
- Rev 214: 文件状态列彩色徽章（GetStatusAsync + SvnStatusToColorConverter）
- Rev 213: TODO.md 生成
- Rev 211: 修复添加本地仓库重复检查
- Rev 210: 添加重复仓库检查
- Rev 207: FileTypeIconConverter 缺少 using
- Rev 206: 文件图标系统（Assets/Icons + FileTypeIconConverter）
- Rev 205: RepoList 未绑定 ItemsSource
- Rev 204: 类型列图标
- Rev 203: CheckoutWindow 隐藏本地路径行
- Rev 202: 删除冗余 Top Toolbar
- Rev 201: nullable enable 导致的 using 顺序问题
- Rev 200: CS8632 nullable 警告 + Task using
