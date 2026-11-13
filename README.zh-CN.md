# Pi Web Tray

[English](README.md) | **简体中文**

Pi Web Tray 是基于 Pi Web 的 Windows 7 至 11 Harness 解决方案，提供运行环境检测、依赖安装与更新、服务托管和自更新。

- **一键使用** — 安装并管理 Pi Web，一键启停。
- **状态可见** — 彩色托盘图标、通知，以及实时日志。
- **开机自启动** — 使用普通 Run 键或管理员计划任务设置开机自启动。
- **端口安全** — 用原生 API 识别监听进程，不误杀无关程序。
- **自更新** — 检测并更新软件、清理旧版本文件。
- **绿色便携** — 无需安装，不捆绑运行时。

## 运行要求

| 组件           | 要求                                            |
| -------------- | ----------------------------------------------- |
| 64 位 Windows  | 7 SP1、8.1、10 或 11                            |
| .NET Framework | 4.8 — 必需；Windows 10 20H2+ 和 11 也支持 4.8.1 |
| Node.js        | 22.19 或更新版本，含 npm                        |

Windows 7 没有官方的 Node.js 22 构建。**下载 Node.js** 会在确认后打开一个未签名的社区构建，本项目不生产也不验证该构建。Pi Web 终端需要 ConPTY（Windows 10 1809+），因此在旧系统上 Pi Web 可以运行，但终端功能可能异常。

### 旧版本 Windows 运行库

Windows 7 SP1 和 Windows 8.1 需要 [.NET Framework 4.8](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48) 而非 4.8.1。如果 Windows 7 下检查更新无法访问 GitHub，请安装 [TLS 1.2 更新补丁](https://support.microsoft.com/help/3140245)。

## 快速开始

1. 下载并解压发布包。
2. 运行 `.\pi-web-tray.exe`。

首次启动会检查环境、安装缺失依赖、启动 Pi Web 并打开浏览器。默认 `AutoInstall=1` 时，正常启动会自动安装缺失依赖。若缺少 Node.js，会说明所需版本并提供下载页，此时 Pi Web Tray 保持运行，检测到 Node.js 后重新检查。若 Node.js 是在该提示之后安装的，则不会自动安装依赖：会报告缺失的依赖，并在菜单中提供 **安装/更新服务**。若 Pi Web 运行期间 Node.js 被移除，会通知但不会停止正在运行的服务，并在下次启动前重新检查。若安装被中断，下次尝试会重新安装相关包，而不会直接信任不完整的目录。

配置与日志默认在 `%LOCALAPPDATA%\pi-web-tray`。设置 `PI_WEB_TRAY_PORTABLE=1` 可将 ini 和日志放在可执行文件旁。若可执行文件位于 `C:\Program Files` 等受保护的系统目录，便携模式会被忽略。即使在便携模式，`state.ini`、`crash.log` 和更新工作文件仍位于数据目录。`pi-web-tray.files.txt` 清单用于后续更新清理已移除的文件，请勿删除。

## 托盘菜单

图标颜色即可看出状态：

| 颜色    | 状态                                    |
| ------- | --------------------------------------- |
| 🟢 绿色 | 运行中                                  |
| ⚪ 灰色 | 已停止                                  |
| 🟠 橙色 | 忙：检查、安装、启动/停止服务或应用更新 |
| 🔵 蓝色 | 缺少依赖（Node.js / Pi Web）            |
| 🔴 红色 | 错误或端口冲突                          |

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
设置...
日志 >
  查看服务运行日志
  查看服务安装/更新日志
  查看软件更新日志
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
检查更新
关于
退出并停止服务
```

- **更新服务** 会先停止 Pi Web，重新安装或更新 Pi Web（`InstallPi=1` 时也更新 Pi），并且只在更新前服务运行时自动重启。缺少依赖时，同一菜单项显示为 **安装/更新服务**。进度窗口支持取消；自更新助手替换文件期间，托盘退出操作会被禁用。
- 只有同一 Windows 会话中运行已安装 `pi-web.js` 的监听者才被视为 Pi Web Tray 的服务。其他会话或安装会报告 **端口冲突**，Pi Web Tray 不会打开或停止它。如有需要，请在 **设置** 中修改 `Port`。
- Pi Web 意外退出会通知。`AutoRestart=1` 时最多连续快速重启 3 次（间隔 5/15/30 秒）；服务稳定运行 60 秒后重试计数会重置。`AutoCheckUpdates=1` 会在启动后检查新版本。

## 配置

### 配置文件

`pi-web-tray.ini` 默认位于 `%LOCALAPPDATA%\pi-web-tray`，不同 Windows 用户不会共享设置。设置 `PI_WEB_TRAY_PORTABLE=1` 可将 ini 和日志放在可执行文件旁（受保护的系统目录会忽略此设置）。带注释的样例见 [`config/pi-web-tray.ini`](config/pi-web-tray.ini)。

| 键                 | 默认值          | 说明                                                               |
| ------------------ | --------------- | ------------------------------------------------------------------ |
| `Port`             | `30141`         | Pi Web 监听端口。                                                  |
| `WorkingDir`       | `%USERPROFILE%` | 服务工作目录。                                                     |
| `AutoInstall`      | `1`             | 启动时自动安装缺失依赖。                                           |
| `InstallPi`        | `1`             | 随 Pi Web 一并安装/更新 Pi；设为 `0` 可跳过。                      |
| `AutoRestart`      | `0`             | Pi Web 意外停止后自动重启。                                        |
| `AutoCheckUpdates` | `0`             | 启动后检查 Pi Web Tray 更新。                                      |
| `InstallTag`       | `latest`        | Pi Web 的 npm 分发标签或具体版本号。                               |
| `NpmRegistry`      | 空              | 自定义 npm 源，如 `https://registry.npmmirror.com`。               |
| `Proxy`            | 空              | 仅用于更新检查和下载的可选 HTTP 或 SOCKS5 代理；留空使用系统代理。 |
| `NodePath`         | 空              | 指定 `node.exe`；留空自动检测。                                    |
| `Language`         | `auto`          | `auto`、`en`、`zh`（简体）或 `zh-Hant`。                           |
| `AutostartAdmin`   | `0`             | 自启动使用管理员权限（由菜单维护）。                               |

值支持 `%ENV%` 展开。含 `;` 或 `#` 的值可加引号。注释符号前需有空白。相对的 `WorkingDir` 和 `NodePath` 值会相对于可执行文件目录解析。

当 Pi Web Tray 安装目录、路径中的任一父目录、或提权进程会加载的任一文件可被标准用户修改或重命名时，开启管理员自启动会被拒绝，且不会改变当前设置。安全警告窗口提供 **修复** 按钮；只有在路径其余部分受保护时，才会通过 UAC 修复目录 ACL。对于便携版或用户配置目录中的安装，修复操作会提示将程序移到 Program Files 等受保护位置；可被标准用户替换的父目录无法安全修复。如果管理员偏好不可用，而用户从主菜单开启自启动，Pi Web Tray 会清除该偏好并改用标准用户自启动。

数据目录按用户隔离。Pi Web Tray 会在日志中隐藏 URL 用户信息和 token 查询参数。设置 `Proxy` 后，只有 Pi Web Tray 的更新检查和下载会使用该代理。留空时使用系统代理。该设置不会传递给 Node、npm 或 Pi Web 子进程。设置窗口中的 **测试** 按钮只测试代理连通性，不会保存。

### 环境变量

| 环境变量               | 说明                                                                                                                         |
| ---------------------- | ---------------------------------------------------------------------------------------------------------------------------- |
| `PI_WEB_TRAY_INSTANCE` | 隔离实例：独立互斥体、信号、配置、数据目录和开机自启动项。与 `PI_WEB_TRAY_HOME` 同用时，数据位于 `HOME\pi-web-tray.<名称>`。 |
| `PI_WEB_TRAY_PORT`     | 覆盖配置的端口。                                                                                                             |
| `PI_WEB_TRAY_LANG`     | 覆盖界面语言。                                                                                                               |
| `PI_WEB_TRAY_HOME`     | 覆盖数据目录根路径，并优先于便携模式。                                                                                       |
| `PI_WEB_TRAY_PORTABLE` | 设为 `1` 时 ini 和日志放在可执行文件旁。                                                                                     |
| `PI_WEB_TRAY_NO_NODE`  | 测试钩子：模拟 Node.js 缺失。                                                                                                |
| `PI_WEB_TRAY_NO_PIWEB` | 测试钩子：模拟 Pi Web 包缺失。                                                                                               |
| `PI_WEB_TRAY_NO_PICLI` | 测试钩子：模拟 Pi 包缺失。                                                                                                   |

命名实例也会使用独立的开机自启动名称，避免覆盖默认安装的条目。但 Run 值或计划任务不会保留启动时的环境变量，因此开机自启动只应从默认实例使用。

## 命令行

```text
.\pi-web-tray.exe                   启动服务并打开浏览器。
.\pi-web-tray.exe --autostart       启动 Pi Web Tray 和服务，不打开浏览器。
.\pi-web-tray.exe --tray-only       仅启动 Pi Web Tray 托盘。
.\pi-web-tray.exe --start-no-open   启动服务，不打开浏览器。
.\pi-web-tray.exe --stop            停止服务；如 Pi Web Tray 托盘正在运行则保留。
.\pi-web-tray.exe --quit            停止服务并退出。
.\pi-web-tray.exe --check           检查环境（0 = 就绪，1 = 缺少组件）。
.\pi-web-tray.exe --install         安装依赖，不启动 Pi Web Tray 托盘。
.\pi-web-tray.exe --dry-run         与 --install 配合，打印 npm 命令但不执行安装。
.\pi-web-tray.exe --diag            将诊断快照写入 diag.txt。
.\pi-web-tray.exe --lang-check      检查所有受支持语言的界面字符串是否完整。
.\pi-web-tray.exe --make-ico        生成程序图标（构建工具）。
.\pi-web-tray.exe --version         显示版本。
.\pi-web-tray.exe --help            显示命令行帮助。
```

未知参数返回退出码 2。

`--install` 会在 npm 运行前停止正在运行的服务，并仅在托盘正在运行时重新启动它；没有托盘时服务会保持停止。若托盘以管理员权限运行，也请从管理员命令行执行 `--install`。

## 从源码构建

需要 Windows 10 或 11，并安装 Visual Studio 2022/2026 的 **.NET 桌面开发** 工作负载，或安装 .NET SDK 8.0+ 和 .NET Framework 4.8 targeting pack。

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd .\pi-web-tray
.\build.cmd
```

## 安全与限制

- 默认以当前用户令牌运行（`asInvoker`），仅在用户请求时提权。无遥测；托盘自身不开放持久监听（Pi Web 在 loopback 上监听）。SOCKS5 更新代理会打开一个仅当前托盘进程可用的、进程级生命周期的 loopback 监听；代理设置变化或进程退出时关闭。
- 管理员模式要求托盘安装目录及提权进程会加载的所有文件（可执行文件、`.config`、卫星资源）都受保护。安装目录可被标准用户修改时，手动提权、管理员权限依赖安装和管理员权限自更新都会被拒绝。从用户可写安装目录提权启动时，会在读取 ini 和加载卫星资源之前拒绝启动。该检查**不保护**用户可写配置和用户安装的 Node/npm、Pi Web、Pi 包；管理员模式仍会以提权令牌执行它们。不要在存在不可信同用户进程的机器上启用。
- 更新通过 HTTPS 从 GitHub 下载，并用 release 发布的 SHA-256 校验。更新助手会把已验证的压缩包复制到目标快照，重新计算哈希并解压，再用解压结果替换文件；不会信任用户可写的暂存目录。安装目录受保护时该快照也受保护。发布包未签名，首次运行可能触发 SmartScreen；该校验和不能替代代码签名。
- 管理员自启动要求当前 Windows 账户属于 Administrators 组，并为当前用户创建登录任务。标准用户通过其他管理员凭据的 UAC，以及手动提权，都会被拒绝。资格检查会覆盖提权托盘加载的文件，而不仅是 `pi-web-tray.exe`。任务名包含用户 SID 和命名实例后缀，不会覆盖其他用户的任务。旧版 `PiWebTray` 任务会被报告。属于当前用户的任务会在下次修改自启动设置时移除；其他用户的计算机级任务不会被改动。
- `server.log`、`install.log` 和 `update.log` 在 4 MB 时轮转。托盘以管理员运行时，`--stop` / `--quit` 需在管理员命令行执行。托盘菜单始终可用。若日志宿主子进程无法创建，服务输出会回退到未经脱敏、不会轮转的 `1>>` 路径，详见 [SECURITY.md](SECURITY.md)；若配置的日志路径是重解析点，服务输出会被丢弃而不是跟随它。
- 管理员自启动任务只针对当前 Windows 用户，并且使用电池供电时也不会被阻止或停止。它不会继承启用自启动时的命令行环境。

## 相关项目

Pi Web Tray 负责安装和启动 [Pi Web](https://github.com/agegr/pi-web)；`InstallPi=1` 时也会安装或更新 Pi。它不包含也不修改这两个项目，相关问题请提交到各自仓库。

## 参与贡献

参与前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。安全问题请按照 [SECURITY.md](SECURITY.md) 报告。

## 许可证

[MIT](LICENSE)
