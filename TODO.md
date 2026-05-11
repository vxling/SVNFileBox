# SVNFileBox 开发任务列表

> 最后更新: 2026-05-11

---

## 项目路径

- **SVNFileBox** (WPF 主程序): `~/aiworks/projects/repos/SVNFileBox/`
  - 工作副本 SVN 地址: https://66.154.112.116/repos/SVNFileBox
  - 用户名: agent / lobster123
  - 本地配置目录: `%APPDATA%/SVNFileBox/`
  - 日志目录: `%APPDATA%/SVNFileBox/logs/`
  - 构建输出: `src/bin/Debug/net10.0-windows/` 或 `src/bin/Release/net10.0-windows/`

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
- [x] 删除文件 + svn delete（不再立即 commit，由 SyncService FullSync 兜底）
- [x] 重命名 + svn delete + svn add（不再立即 commit，由 SyncService FullSync 兜底）
- [x] 在资源管理器中打开
- [x] 复制路径
- [x] 双击打开文件（系统默认程序）
- [x] 重复仓库检查（路径重复/URL 重复）
- [x] 设置页面基础功能
- [x] 同步记录持久化（JSON 文件，零依赖）
- [x] 同步记录查看 UI（点击切换文件列表/同步记录列表）
- [x] 冲突处理（Last-Write-Wins）
- [x] 最小化到托盘
- [x] 密码加密存储（DPAPI）
- [x] **文件复制进度窗口**（分析阶段 + 拷贝阶段独立显示，计时器不重置）
- [x] **ExecuteCopyAsync 重入保护**（Interlocked.CompareExchange guard，防止拖拽+粘贴并发）
- [x] **SvnService 操作串行化**（SemaphoreSlim(1,1) + 30s 超时，防止 SharpSvn 并发崩溃）
- [x] **手工同步加 FullSyncAsync 兜底**（SyncNowAsync 最后一步做全量扫描 add+commit）
- [x] **自定义 MsgBox 对话框**（解决 Alt+Tab 时 dialog 消失、title 遮蔽基类、binding 失效三个坑）
- [x] **本地化 MsgBox 按钮文本**（中文：是/否，英文：Yes/No）
- [x] **删除/重命名/新建文件夹去掉立即 commit**（由 SyncService 的 FullSyncAsync 统一兜底，避免频繁网络延迟阻塞）
- [x] **右键菜单图标注入**（首次打开右键菜单时按扩展名注入系统文件图标）
- [x] **单实例运行**（Global Mutex，重复启动弹 MessageBox 后退出）
- [x] **开机启动区分**（--autostart 参数，区分系统启动和手动启动）
- [x] **同步周期调整为 1-10 分钟**（UI 滑块 + 提示文本 + 注册表写入）
- [x] **移除 [IconInject] 调试日志**
- [x] **Windows 安装包**（Inno Setup，CI 自动编译，Release 同时包含 zip 便携版和 exe 安装包）

### ⏳ 待完成

- （暂无）

---

## 二、已完成记录

| Rev | 内容 |
|-----|------|
| r547 | Add Inno Setup installer for Windows (installer.iss + CI steps) |
| r546 | Remove [IconInject] debug logs |
| r545 | Sync interval slider: 1-30 min → 1-10 min (UI + zh/en tips + registry) |
| r544 | Auto-start: add --autostart arg to distinguish boot vs manual launch |
| r543 | Add single-instance Global Mutex (second launch shows MsgBox and exits) |
| r510 | Rename: svn delete old + svn add new, no immediate commit - SyncService auto-commits |
| r509 | Delete: svn delete then physical delete - SyncService auto-commits on FullSync |
| r508 | Delete: remove immediate SVN commit - let SyncService auto-commit to avoid blocking on network latency |
| r507 | Remove main window background color |
| r506 | Add owner to Delete_Click MsgBox - prevents dialog vanishing during Alt+Tab |
| r505 | Add owner(this) to all MsgBox.Show calls - prevents dialog vanishing behind main window |
| r504 | MsgBox: fix title/message not showing - assign TitleText/MessageText directly in code |
| r503 | MsgBox: rename 'new string Title' to 'BoxTitle' to fix window title bar showing empty |
| r502 | LocalizationService: add missing Yes/No button text for MsgBox (Chinese: 是/否, English: Yes/No) |
| r501 | Global: replace all native MessageBox.Show with MsgBox.Show for consistent UI |
| r500 | MainWindow: add ClearReadOnlyAndDelete helper for .svn read-only files when deleting network repo |
| r499 | CheckoutWindow: add MaxHeight=60 and TextTrimming=CharacterEllipsis to ErrorText |
| r498 | CheckoutWindow: fix ErrorText layout - Row 5 Auto instead of * |
| r497 | CheckoutWindow: increase SVN operation timeout to 120s, fix SetLoading not clearing error text |
| r496 | CheckoutAsync: set ForceCredentials on SvnClient so username/password sent to server |
| r495 | RemoveRepo: differentiate network vs local repo deletion - network repo removes local working copy |
| r494 | AddLocalRepoWindow: increase height to 180, error text row Auto height |
| r493 | ConflictWindow: align suggestion badge top, narrow resolution combobox to 140px |
| r492 | Fix sync log back button text: show 'Back'/'返回' in sync records view |
| r470 | 文件复制进度窗口（StartAnalysis 拆分，计时器不重置） |
| r468 | ExecuteCopyAsync 重入保护（Interlocked.CompareExchange guard） |
| r465 | SvnService 操作串行化（SemaphoreSlim(1,1) + 30s 超时） |
| r471 | 手工同步加 FullSyncAsync 兜底 |
| r228 | Code review 全修复（SyncService wiring，CommitAsync 传密码，.svn 过滤修正，Repository.Password，TrayIcon null-guard） |
| r225 | DPAPI 密码加密（DpapiService + ConfigService 集成） |
| r223 | 最小化到托盘（Hardcodet.NotifyIcon.Wpf TaskbarIcon，OnClosing hide，BalloonTip，托盘菜单） |
| r222 | Last-Write-Wins 冲突处理（HandleConflictsAsync） |
| r220 | 同步记录查看 UI（ToggleSyncRecordsView + SyncRecordDisplay + BoolToCollapsedConverter） |
| r219 | TODO.md: mark sync record persistence done |
| r218 | 同步记录 JSON 持久化（SyncRecord + SyncRecordService） |
| r217 | 粘贴/拖拽覆盖确认框；双击打开文件 |
| r215 | P0 右键菜单修复（重命名/删除/新建/粘贴/拖拽全部补 svn sync） |
| r214 | 文件状态列彩色徽章（GetStatusAsync + SvnStatusToColorConverter） |
| r213 | TODO.md 生成 |
| r211 | 修复添加本地仓库重复检查 |
| r210 | 添加重复仓库检查 |
| r207 | FileTypeIconConverter 缺少 using |
| r206 | 文件图标系统（Assets/Icons + FileTypeIconConverter） |
| r205 | RepoList 未绑定 ItemsSource |
| r204 | 类型列图标 |
| r203 | CheckoutWindow 隐藏本地路径行 |
| r202 | 删除冗余 Top Toolbar |
| r201 | nullable enable 导致的 using 顺序问题 |
| r200 | CS8632 nullable 警告 + Task using |
