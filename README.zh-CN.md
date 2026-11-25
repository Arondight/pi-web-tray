# Pi Web Tray

[English](README.md) | **简体中文**

Pi Web Tray 是基于 [Pi Web](https://github.com/agegr/pi-web) 的 Windows 7 至 11 解决方案，提供运行环境检测、依赖安装与更新、服务托管和自更新。

- **一键使用** — 安装并管理 Pi Web，一键启停。
- **状态可见** — 彩色托盘图标、通知，以及实时日志。
- **开机自启动** — 使用普通 Run 键或管理员计划任务设置开机自启动。
- **一键更新** — 检测并更新软件、清理旧版本文件。
- **绿色便携** — 无需安装，不捆绑运行时。

## 运行要求

| 组件                  | 要求                                     |
| --------------------- | ---------------------------------------- |
| 64 位 Windows         | 7 SP1、8.1、10 或 11                     |
| .NET Framework 运行时 | 4.8；Windows 10 20H2+ 和 11 也支持 4.8.1 |
| Node.js               | 22.19 或更新版本，含 npm                 |

Windows 默认不提供 Bash。如果未额外安装 Bash，请在 Pi Web 中打开 **设置 → 常规 → Shell 工具**，启用 **使用 PowerShell 替代 Bash**。

### 旧版本 Windows

Windows 7 SP1 和 Windows 8.1 需要 [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) 而非 4.8.1。如果 Windows 7 SP1 下检查更新无法访问 GitHub，请安装 [TLS 1.2 更新补丁](https://support.microsoft.com/help/3140245)。

Windows 7 SP1 没有官方的 Node.js 22 构建。**下载 Node.js** 会在确认后打开一个未签名的社区构建，本项目不生产也不验证该构建。Windows 8.1 使用官方 Node.js 22 构建（实验性）。在 Windows 7 SP1 和 8.1 上，Pi Web 的终端不可用：其 PTY 层需要 ConPTY（Windows 10 1809+）。Next.js 在该系统上也可能无法加载原生 SWC 插件，会回退到 WebAssembly 构建，启动会明显变慢。

在 Windows 7 SP1 和 8.1 上，建议使用 [Supermium](https://github.com/win32ss/supermium/releases) 浏览器。

## 推荐安装的软件

本软件运行时不需要 Python 3、PowerShell 或 Git Bash，但是安装它们可以增强 Pi Web 的能力。

### Python 3

| 系统          | 推荐版本                                                                 |
| ------------- | ------------------------------------------------------------------------ |
| Windows 10/11 | [最新 Python 3](https://www.python.org/downloads/)                       |
| Windows 8.1   | [Python 3.13.17](https://www.python.org/downloads/release/python-31317/) |
| Windows 7 SP1 | [Python 3.8.10](https://www.python.org/downloads/release/python-3810/)   |

安装 Python 时请勾选 **Add python.exe to PATH**。

### PowerShell

| 系统          | 推荐版本                                                                                           |
| ------------- | -------------------------------------------------------------------------------------------------- |
| Windows 10/11 | [最新 PowerShell](https://github.com/PowerShell/PowerShell/releases)                               |
| Windows 8.1   | [PowerShell 7.2.24](https://github.com/PowerShell/PowerShell/releases/tag/v7.2.24)                 |
| Windows 7 SP1 | [Windows Management Framework 5.1](https://www.microsoft.com/en-us/download/details.aspx?id=54616) |

### Git Bash

| 系统          | 推荐版本                                                                                                  |
| ------------- | --------------------------------------------------------------------------------------------------------- |
| Windows 10/11 | [最新 Git for Windows](https://git-scm.com/install/windows)                                               |
| Windows 8.1   | [Git for Windows 2.55.0.windows.5](https://github.com/git-for-windows/git/releases/tag/v2.55.0.windows.5) |
| Windows 7 SP1 | [Git for Windows 2.46.1.windows.1](https://github.com/git-for-windows/git/releases/tag/v2.46.1.windows.1) |

## 快速开始

1. 下载并解压发布包。
2. 双击运行 `pi-web-tray.exe`。

首次启动会检查环境、安装缺失依赖、启动服务并打开浏览器。默认 `AutoInstall=1` 时，正常启动会自动安装缺失依赖。若缺少 Node.js，会说明所需版本并提供下载页，此时托盘保持运行，检测到 Node.js 后重新检查。若 Node.js 是在该提示之后安装的，则需要在右键菜单中更新服务。

配置与日志默认在 `%LOCALAPPDATA%\pi-web-tray`。设置 `PI_WEB_TRAY_PORTABLE=1` 可将 ini 和日志放在可执行文件旁。若可执行文件位于 `C:\Program Files` 等受保护的系统目录，便携模式会被忽略。`pi-web-tray.files.txt` 清单用于后续更新清理已移除的文件，请勿删除。

## 托盘菜单

图标颜色即可看出状态：

| 颜色    | 状态                                              |
| ------- | ------------------------------------------------- |
| 🟢 绿色 | 服务运行中                                        |
| ⚪ 灰色 | 服务已停止                                        |
| 🟠 橙色 | 托盘忙（检查、安装、启动/停止、未验证或应用更新） |
| 🔵 蓝色 | 缺少 Node.js 或服务组件                           |
| 🔴 红色 | 失败（如端口冲突）                                |

菜单内容与当前状态一致：

```text
● 运行中 · 127.0.0.1:30141 · 管理员模式
---------------------------------------
打开浏览器
启动服务
停止服务
重新启动服务
---------------------------------------
更新服务
下载 Node.js
---------------------------------------
环境检查
设置
查看日志...
  查看服务运行日志
  查看服务安装/更新日志
  查看软件更新日志
打开数据目录
打开程序目录
打开配置文件
复制网页链接地址
创建桌面快捷方式
---------------------------------------
[ ] 允许来自局域网的连接
[x] 开机时启动程序
[ ] 开机启动时使用管理员权限
---------------------------------------
以标准用户模式重启
检查更新
关于
退出并停止服务
```

- **更新服务** 会先停止服务，重新安装或更新 Pi Web（`InstallPi=1` 时也更新 Pi）。缺少服务组件时，同一菜单项显示为 **安装/更新服务**。
- 服务意外退出会通知。`AutoRestart=1` 时最多连续重启 3 次。

## 配置

正常使用不需要编辑配置文件。托盘菜单中的 **设置** 窗口已提供受支持的选项；下面的 ini 说明仅用于高级部署、自动化和故障排查。

### 配置文件

`pi-web-tray.ini` 默认位于 `%LOCALAPPDATA%\pi-web-tray`，不同 Windows 用户不会共享设置。设置 `PI_WEB_TRAY_PORTABLE=1` 可将 ini 和日志放在可执行文件旁（受保护的系统目录会忽略此设置）。带注释的样例见 [`config/pi-web-tray.ini`](config/pi-web-tray.ini)。

| 键                 | 默认值   | 说明                                                                                   |
| ------------------ | -------- | -------------------------------------------------------------------------------------- |
| `Port`             | `30141`  | 服务监听端口。                                                                         |
| `AutoInstall`      | `1`      | 启动时自动安装缺失依赖。                                                               |
| `InstallPi`        | `1`      | 随 Pi Web 一并安装/更新 Pi；设为 `0` 可跳过。                                          |
| `AutoRestart`      | `0`      | 服务意外停止后自动重启。                                                               |
| `AutoCheckUpdates` | `0`      | 启动后检查 Pi Web Tray 更新。                                                          |
| `InstallTag`       | `latest` | Pi Web 的 npm 分发标签或具体版本号；具体版本号会与已安装版本核对并在不匹配时重新安装。 |
| `NpmRegistry`      | 空       | 自定义 npm 源，如 `https://registry.npmmirror.com`。                                   |
| `Proxy`            | 空       | 仅用于 Pi Web Tray 更新检查和下载的可选 HTTP 或 SOCKS5 代理；留空使用系统代理。        |
| `NetworkAccess`    | `0`      | 允许网络上的其他设备访问服务，同时添加 Windows 防火墙端口规则。                        |
| `NodePath`         | 空       | 指定 `node.exe`；留空自动检测。                                                        |
| `Language`         | `auto`   | `auto`、`en`、`zh` 或 `zh-Hant`。                                                      |
| `AutostartAdmin`   | `0`      | 自启动使用管理员权限（由菜单维护）。                                                   |

值支持 `%ENV%` 展开。含 `;` 或 `#` 的值可加引号。注释符号前需有空白。相对的 `NodePath` 值会相对于可执行文件目录解析。

当 Pi Web Tray 安装目录、路径中的任一父目录、或管理员模式进程会加载的任一文件可被标准用户修改或重命名时，开启管理员自启动会被拒绝，且不会改变当前设置。安全警告窗口提供 **修复** 按钮；只有在路径其余部分受保护时，才会通过 UAC 修复目录 ACL。对于便携版或用户配置目录中的安装，修复操作会提示将程序移到 `C:\Program Files` 等受保护位置；可被标准用户替换的父目录无法安全修复。如果便携模式会把 ini 和日志放在可执行文件旁，修复操作也会拒绝，因为修复后的 ACL 会使这些运行时文件对标准用户只读。如果管理员偏好不可用，而用户从主菜单开启自启动，托盘会清除该偏好并改用标准用户自启动。

数据目录按用户隔离。托盘会在日志中隐藏常见凭据形式。设置 `Proxy` 后，只有托盘的更新检查和下载会使用该代理。留空时使用系统代理。该设置不会传递给 Node、npm 或 Pi Web 子进程。设置窗口中的 **测试** 按钮只测试代理连通性，不会保存。

### 环境变量

| 环境变量               | 说明                                                                                                                         |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `PI_WEB_TRAY_INSTANCE` | 隔离实例：独立互斥体、信号、配置、数据目录和开机自启动项。与 `PI_WEB_TRAY_HOME` 同用时，数据位于 `HOME\pi-web-tray.<名称>`。 |
| `PI_WEB_TRAY_PORT`     | 覆盖配置的端口。                                                                                                             |
| `PI_WEB_TRAY_LANG`     | 覆盖界面语言。                                                                                                               |
| `PI_WEB_TRAY_HOME`     | 覆盖数据目录根路径，并优先于便携模式。该目录必须可由当前用户写入；管理员模式更新助手会拒绝受保护的系统目录。                 |
| `PI_WEB_TRAY_PORTABLE` | 设为 `1` 时 ini 和日志放在可执行文件旁。                                                                                     |
| `PI_WEB_TRAY_NO_NODE`  | 测试钩子：模拟 Node.js 缺失。                                                                                                |
| `PI_WEB_TRAY_NO_PIWEB` | 测试钩子：模拟 Pi Web 包缺失。                                                                                               |
| `PI_WEB_TRAY_NO_PICLI` | 测试钩子：模拟 Pi 包缺失。                                                                                                   |

命名实例会为旧的启动条目使用独立名称，但命名实例不能启用开机自启动；请使用默认实例启用开机自启动。Run 值或计划任务不会保留启动时的环境变量。

## 命令行

正常使用通过托盘完成（双击可执行文件）；命令行仅用于自动化、脚本和故障排查。

```text
.\pi-web-tray.exe                   启动服务并打开浏览器。
.\pi-web-tray.exe --autostart       启动托盘和服务，不打开浏览器。
.\pi-web-tray.exe --tray-only       仅启动托盘。
.\pi-web-tray.exe --start-no-open   启动服务，不打开浏览器。
.\pi-web-tray.exe --stop            停止服务；如托盘正在运行则保留。
.\pi-web-tray.exe --quit            停止服务并退出。
.\pi-web-tray.exe --check           检查环境（0 = 就绪，1 = 缺少组件）。
.\pi-web-tray.exe --install         安装依赖，不启动托盘。
.\pi-web-tray.exe --dry-run         与 --install 配合，打印 npm 命令但不执行安装。
.\pi-web-tray.exe --diag            将诊断快照写入 diag.txt。
.\pi-web-tray.exe --lang-check      检查所有受支持语言的界面字符串是否完整。
.\pi-web-tray.exe --make-ico        生成程序图标（构建工具）。
.\pi-web-tray.exe --version         显示版本。
.\pi-web-tray.exe --help            显示命令行帮助。
```

未知参数返回退出码 2。

`--install` 会在 npm 运行前停止正在运行的服务，并仅在托盘正在运行时重新启动它；没有托盘时服务会保持停止。若托盘以管理员模式运行，也请从管理员模式命令行执行 `--install`。

## 从源码构建

构建工具二选一：

- **.NET SDK 8.0 或更高版本**（推荐命令行构建），或
- **Build Tools for Visual Studio 2022 或更高版本**（仅使用 MSBuild）。

两者都需要 **.NET Framework 4.8 Targeting Pack**；独立的 **.NET Framework 4.8 Developer Pack** 已包含它。

构建主机必须是 Windows 10/11（或 Windows Server CI runner）。兼容性检查脚本还需要 PowerShell 7。

**Visual Studio 2022 或更高版本** + **.NET 桌面开发**工作负载是可选的 IDE，不是额外的构建要求。程序目标仍然是 **.NET Framework 4.8**，不是 .NET 8。最终用户只需要 **.NET Framework 4.8 运行时**（系统支持时可用 4.8.1）。

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## 安全与限制

- 默认使用当前用户令牌，管理员模式仅在显式操作时启用。无遥测；除非开启网络访问，服务只监听 loopback。
- 安装路径或加载文件可被标准用户修改时，管理员模式和管理员自启都会被拒绝；请将程序移到 `C:\Program Files` 等受保护位置，或使用提示中的 **修复** 操作。管理员自启要求账户属于 Administrators 组，并且只为当前用户创建任务。该检查不保护用户配置、Node/npm 或 Pi Web/Pi 包；不要在不可信的同用户进程环境中启用管理员模式。
- 更新通过 GitHub HTTPS 下载并校验 SHA-256。发布包未签名，首次运行可能触发 SmartScreen。
- 日志在 4 MB 时轮转。管理员模式托盘需在管理员控制台执行 `--stop` / `--quit`，托盘菜单仍可用。日志宿主不可用时，标准用户模式可能写入未脱敏且不轮转的日志；管理员模式会丢弃服务输出。
- **允许来自局域网的连接** 会让服务监听所有网络接口并开放防火墙端口。服务没有独立登录，任何能访问这台电脑的人都能控制 Pi 并读取项目文件。仅在可信网络中开启。

## 相关项目

Pi Web Tray 负责安装和启动 [Pi Web](https://github.com/agegr/pi-web)；`InstallPi=1` 时也会安装或更新 [Pi](https://github.com/earendil-works/pi)。它不包含也不修改这两个项目，相关问题请提交到各自仓库。

## 参与贡献

参与前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。安全问题请按照 [SECURITY.md](SECURITY.md) 报告。

## 许可证

[MIT](LICENSE)
