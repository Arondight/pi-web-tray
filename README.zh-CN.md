# Pi Web Tray

[English](README.md) | **简体中文**

Pi Web Tray 是基于 Pi 和 Pi Web 的 Windows 7 至 11 通用 Harness 解决方案，在托盘软件中完成运行环境检测、依赖安装与更新、服务托管和自更新。

- **一键使用** — 安装并管理 Pi 和 Pi Web，一键启停。
- **状态可见** — 彩色托盘图标、通知，以及实时日志。
- **开机自启动** — 使用普通 Run 键或管理员计划任务设置开机自启动。
- **端口安全** — 用原生 API 识别监听进程，不误杀无关程序。
- **自更新** — 检测并更新软件、清理旧版本文件。
- **绿色便携** — 无需安装，无第三方运行时依赖。

## 运行要求

| 组件           | 要求                                |
| -------------- | ----------------------------------- |
| Windows        | 7 SP1、8、8.1、10 或 11（仅 64 位） |
| .NET Framework | 4.8 — 见下方 Windows 7 说明         |
| Node.js        | 22.19 或更新版本，含 npm            |

**必须使用 64 位 Windows。** 不支持 32 位 Windows。

Windows 7 和 Windows 8 没有官方的 Node.js 22 构建。**下载 Node.js** 会在确认后打开一个未签名的社区构建，本项目不生产也不验证该构建。Pi Web 终端需要 ConPTY（Windows 10 1809+），因此在旧系统上托盘可以运行，但服务或终端可能不可用。

### Windows 7 运行库

Windows 7 SP1 需要 [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48)。托盘自更新还需要 TLS 1.2；如果无法访问 GitHub，请安装 [Windows 7 的 TLS 1.2 更新](https://support.microsoft.com/help/3140245)（KB3140245）。

## 推荐安装的软件

本软件不依赖 Python 3 和 PowerShell，但是安装它们可以增强 Pi 的能力。

### Python 3

Windows 10 / 11：使用[官方下载页](https://www.python.org/downloads/)，安装时勾选 **Add python.exe to PATH**：

旧版 Windows 已不再获得当前 Python 版本的安装包，以下是最新仍提供安装包的版本：

| 系统        | 最新仍提供安装包的版本                                                   |
| ----------- | ------------------------------------------------------------------------ |
| Windows 7   | [Python 3.8.10](https://www.python.org/downloads/release/python-3810/)   |
| Windows 8   | [Python 3.8.10](https://www.python.org/downloads/release/python-3810/)   |
| Windows 8.1 | [Python 3.13.17](https://www.python.org/downloads/release/python-31317/) |

### PowerShell

Windows 8.1、10、11 可以安装最新的 PowerShell，从[官方发布页](https://github.com/PowerShell/PowerShell/releases)下载。Windows 7 SP1 需要安装 [Windows Management Framework 5.1](https://www.microsoft.com/en-us/download/details.aspx?id=54616) 以获得 Windows PowerShell 5.1。Windows 8 无法升级 PowerShell，只能使用系统自带的 3.0 版本。

## 快速开始

1. 下载并解压发布包。
2. 运行 `.\pi-web-tray.exe`。

首次启动会检查环境、安装缺失依赖、启动 Pi Web 并打开浏览器。若缺少 Node.js，软件会说明所需版本、提供下载页并退出；安装 Node.js 后重新运行即可。若安装被中断，下次尝试会重新安装相关包，而不会直接信任不完整的目录。

配置与日志默认在 `%LOCALAPPDATA%\pi-web-tray`；设置 `PI_WEB_TRAY_PORTABLE=1` 可将它们放在可执行文件旁。`pi-web-tray.files.txt` 清单用于后续更新清理已移除的文件，请勿删除。

## 托盘菜单

图标颜色即可看出状态：绿色运行中、灰色已停止、橙色忙、蓝色缺少依赖、红色错误。菜单内容与当前状态一致：

```text
● 运行中 · 127.0.0.1:30141 · 管理员模式
---------------------------------------
在浏览器中打开 Pi Web
启动服务
停止服务
重新启动服务
---------------------------------------
更新 Pi Web 和 Pi
更新本软件
下载 Node.js
---------------------------------------
环境自检
设置...
日志 >
  查看服务日志
  查看安装日志
  查看更新日志
打开数据目录
打开程序目录
打开配置文件
复制网页链接地址
创建桌面快捷方式
---------------------------------------
开机自启动：已启用（标准用户权限）
自启动时使用管理员权限：否
---------------------------------------
以管理员身份重启
关于
退出并停止 Pi Web
```

- **更新 Pi Web 和 Pi** 会先停止 Pi Web，配置 `InstallPiCli=1` 时同时更新两个包；只有更新前服务在运行才自动重启。进度窗口支持取消。
- 只有同一 Windows 会话中运行已安装 `pi-web.js` 的监听者才被视为本托盘的服务；其他会话或安装会报告 **端口冲突**，托盘不会打开或停止它。如有需要，请在 **设置** 中修改 `Port`。
- Pi Web 意外退出会通知；`AutoRestart=1` 时自动重启，`AutoCheckUpdates=1` 会在启动后检查新版本。

## 配置

### 配置文件

`pi-web-tray.ini` 默认位于 `%LOCALAPPDATA%\pi-web-tray`，不同 Windows 用户不会共享设置；设置 `PI_WEB_TRAY_PORTABLE=1` 可将 ini 和日志放在可执行文件旁。带注释的样例见 [`config/pi-web-tray.ini`](config/pi-web-tray.ini)。

| 键                 | 默认值          | 说明                                                                                  |
| ------------------ | --------------- | ------------------------------------------------------------------------------------- |
| `Port`             | `30141`         | Pi Web 监听端口。                                                                     |
| `WorkingDir`       | `%USERPROFILE%` | 服务工作目录，也是 Pi 的默认项目目录。                                                |
| `AutoInstall`      | `1`             | 缺少 Pi Web 时自动安装。                                                              |
| `AutoRestart`      | `0`             | Pi Web 意外停止后自动重启。                                                           |
| `AutoCheckUpdates` | `0`             | 启动后检查托盘更新。                                                                  |
| `InstallPiCli`     | `1`             | 同时安装 Pi 命令行。                                                                  |
| `InstallTag`       | `latest`        | npm 分发标签或具体版本号。                                                            |
| `NpmRegistry`      | 空              | 自定义 npm 源，如 `https://registry.npmmirror.com`。                                  |
| `Proxy`            | 空              | 可选的 HTTP 或 SOCKS5 代理，如 `http://127.0.0.1:7890` 或 `socks5://127.0.0.1:1080`。 |
| `NodePath`         | 空              | 指定 `node.exe`；留空自动检测。                                                       |
| `ServerScript`     | 空              | 指定 `pi-web.js`；留空自动检测。                                                      |
| `Language`         | `auto`          | `auto`、`en`、`zh`（简体）或 `zh-Hant`。                                              |
| `AutostartAdmin`   | `0`             | 自启动使用管理员权限（由菜单维护）。                                                  |

值支持 `%ENV%` 展开；含 `;` 或 `#` 的值可加引号；注释符号前需有空白。

数据目录按用户隔离，因为 ini 可能包含源或代理凭据，日志可能包含项目数据；不要在共享目录中使用便携模式。避免把凭据写进 `NpmRegistry`，请在 npm 自己的 `.npmrc` 中配置认证，并把 ini 当作机密文件。托盘会在日志中隐藏 URL 用户信息和 token 查询参数；仅在日志宿主子进程无法启动时才启用的 `1>>` 兼容回退不经过脱敏。设置 `Proxy` 后，更新检查、下载以及 npm/Pi Web 子进程都会使用该代理；设置窗口中的 **测试** 按钮只测试代理连通性，不会保存。

### 环境变量

| 环境变量                     | 说明                                     |
| ---------------------------- | ---------------------------------------- |
| `PI_WEB_TRAY_PORT`           | 覆盖配置的端口。                         |
| `PI_WEB_TRAY_LANG`           | 覆盖界面语言。                           |
| `PI_WEB_TRAY_HOME`           | 覆盖数据目录。                           |
| `PI_WEB_TRAY_PORTABLE`       | 设为 `1` 时 ini 和日志放在可执行文件旁。 |
| `HTTP_PROXY` / `HTTPS_PROXY` | 供 npm 和 Pi Web 模型请求使用。          |

## 命令行

```text
.\pi-web-tray.exe                   启动 Pi Web 并打开浏览器。
.\pi-web-tray.exe --autostart       启动托盘和服务，不打开浏览器。
.\pi-web-tray.exe --tray-only       仅启动托盘。
.\pi-web-tray.exe --start-no-open   启动服务，不打开浏览器。
.\pi-web-tray.exe --stop            停止服务，保留托盘。
.\pi-web-tray.exe --quit            停止服务并退出。
.\pi-web-tray.exe --check           检查环境（0 = 就绪，1 = 缺少组件）。
.\pi-web-tray.exe --install         安装依赖，不启动托盘。
.\pi-web-tray.exe --dry-run         与 --install 配合，仅打印 npm 命令。
.\pi-web-tray.exe --diag            将诊断快照写入 diag.txt。
.\pi-web-tray.exe --version         显示版本。
.\pi-web-tray.exe --help            显示命令行帮助。
```

未知参数返回退出码 2。

## 从源码构建

需要 Windows 11 和 Visual Studio 2026，安装 **.NET 桌面开发** 工作负载（其中包含 .NET Framework 4.8 targeting pack）。

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## 安全与限制

- 默认以当前用户令牌运行（`asInvoker`），仅在用户请求时提权；无遥测、无网络监听。
- 更新通过 HTTPS 从 GitHub 下载，并用 release 发布的 SHA-256 校验；更新助手还会校验目标安装与暂存文件指纹。发布包未签名，首次运行可能触发 SmartScreen。
- 管理员自启动要求当前账户是管理员，并为当前用户创建登录任务；任务名包含用户 SID，不会覆盖其他用户的任务。
- `server.log`、`install.log` 和 `update.log` 在 4 MB 时轮转。托盘以管理员运行时，`--stop` / `--quit` 需在管理员命令行执行；托盘菜单始终可用。

## 相关项目

Pi Web Tray 只负责安装和启动 [Pi Web](https://github.com/agegr/pi-web)（`@agegr/pi-web`）与 [Pi](https://github.com/earendil-works/pi)（`@earendil-works/pi-coding-agent`），不包含也不修改它们；相关问题请提交到各自仓库。

## 参与贡献

参与前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。安全问题请按照 [SECURITY.md](SECURITY.md) 报告。

## 许可证

[MIT](LICENSE)
