# 关屏（Windows）

一个托盘小工具：**把屏幕关掉、同时静音，系统照常运行**。恢复时取消静音并点亮屏幕。

只做这一件事。无安装、无服务、无开机自启、无网络。

## 用法

1. 下载 `ScreenOff.exe`，双击运行（首次可能弹防火墙/未知发布者提示，选允许即可）。
2. 托盘会出现一个图标，程序常驻后台。

| 热键 | 作用 |
|---|---|
| **Alt+1** | 开始 · 静默模式（不拦输入，谁点亮都在 0.5 秒内补灭） |
| **Alt+2** | 开始 · 锁鼠标模式（吞掉物理鼠标/触摸板，键盘照常能点亮） |
| **Alt+3** | 恢复（停循环 + 取消静音 + 点亮屏幕） |

托盘图标：单击展开菜单（开始静默 / 开始锁鼠标 / 恢复 / 退出），**双击 = 恢复**。

退出程序：托盘菜单 → 退出（退出时会自动恢复静音和屏幕）。

## 行为细节

- **关屏**：广播 `WM_SYSCOMMAND / SC_MONITORPOWER`。
- **不再高频发命令**：只有检测到输入（`GetLastInputInfo` 变化）后的 0.5 秒才补灭一次，另有 15 秒兜底，避免显示器因重复命令闪烁。
- **静音**：切系统默认播放设备静音；运行期间每 5 秒复查，被手动取消就重新静音；恢复/退出时取消静音。
- **锁鼠标模式**：低级鼠标钩子（`WH_MOUSE_LL`）吞掉物理鼠标事件；远程桌面/串流软件**注入**的鼠标事件放行，不影响远程操作；键盘从不拦截。
- **进程被杀**：关掉托盘/结束进程时，退出流程会取消静音并点亮屏幕；若被强杀，按一下键盘即可点亮，声音需要手动恢复。

## 文件

| 文件 | 作用 |
|---|---|
| `src/TrayContext.cs` | 托盘、模式切换、灭屏调度、静音守护 |
| `src/HotkeyWindow.cs` | 全局热键 Alt+1/2/3 |
| `src/Native.cs` | 关屏/点亮/热键/空闲检测的 P/Invoke |
| `src/AudioMute.cs` | CoreAudio 静音开关 |
| `src/MouseBlocker.cs` | 锁鼠标模式的低级鼠标钩子 |
| `src/Program.cs` | 入口 |

日志：`%LOCALAPPDATA%\ScreenOff\screenoff.log`

## 构建

全部走 GitHub Actions（本地不构建）：

- 工作流：`.github/workflows/build.yml`
- 触发：push 到 `main`/`master`，或在 Actions 页 `workflow_dispatch`
- 产物：`ScreenOff-win-x64` artifact 里的 `ScreenOff.exe`（自包含单文件，目标机不需要装 .NET）

环境：.NET 8 + WinForms + `win-x64`，`PublishSingleFile` + 压缩。
