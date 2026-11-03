# Pi Web Tray

[English](README.md) | **简体中文**

基于 pi / pi-web 的 Windows 7–11 通用 Harness 解决方案。一个托盘程序完成运行环境
检测、依赖安装与更新、服务托管和自更新。

- **一键使用** — 启动 pi-web 并打开浏览器；服务已在运行时只打开浏览器。
- **安装托管** — 检测 Node.js 22.19+，通过 npm 安装 pi-web 和 pi 命令行。
- **状态可见** — 彩色托盘图标、通知，以及可暂停/筛选的实时日志。
- **开机自启动** — 普通 Run 键或管理员计划任务，登录时不打开浏览器。
- **端口安全** — 用原生 API 识别监听进程，绝不误杀无关程序。
- **自更新** — GitHub release、SHA-256 校验、清理旧版本文件。
- **绿色便携** — 无需安装程序，无第三方运行时依赖（.NET Framework 4.8）。
- **三语界面** — 英文、简体中文、繁体中文。

## 运行要求

| 组件           | 要求                                                 |
| -------------- | ---------------------------------------------------- |
| Windows        | 7 SP1、8、8.1、10 或 11（建议 64 位）                |
| .NET Framework | 4.8 — 见下方 Windows 7 说明                          |
| Node.js        | 22.19 或更新版本，含 npm                             |
| 网络           | 可访问 npm registry（可用镜像）；更新时需访问 GitHub |

### Windows 7 运行库

Windows 7 需要 **.NET Framework 4.8**，不是 4.8.1：4.8.1 不支持 Windows 7，
但系统提示仍可能打开 4.8.1 的下载页。请从这里安装 4.8：

<https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48>

请将 `pi-web-tray.exe.config` 与可执行文件放在同一目录。

Windows 7 和 Windows 8 没有官方的 Node.js 22 构建。**下载 Node.js** 会在确认后打开
一个未签名的社区构建，本项目不生产也不验证该构建。pi-web 终端需要 ConPTY
（Windows 10 1809+），因此在旧系统上托盘可以运行，但服务或终端可能不可用。
Windows 7 和 Windows 8.1 已停止微软支持，使用风险由用户自行承担。

## 快速开始

1. 下载并解压发布包。
2. 将 `pi-web-tray.exe`、`pi-web-tray.exe.config`、`pi-web-tray.files.txt` 和
   `zh-Hans` / `zh-Hant` 目录放在同一目录。
3. 运行 `pi-web-tray.exe`。

首次启动会检查环境、安装缺失依赖、启动 pi-web 并打开浏览器。若缺少 Node.js，
程序会说明所需版本、提供下载页并退出；安装 Node.js 22.19+ 后重新运行即可。
配置与日志（`server.log`、`install.log`、`update.log`）默认在可执行文件同目录；位于
`Program Files` 等受保护目录时改在 `%LOCALAPPDATA%\pi-web-tray`。
`pi-web-tray.files.txt` 清单用于后续更新清理已移除的文件，请勿删除。

首次对话前，请在 **Models** 面板登录模型提供方，或执行 `pi auth login`。
pi-web 与 pi 共用 `~/.pi/agent` 中的凭据。

## 托盘菜单

```text
打开 Pi Web · 启动 · 停止 · 重启
更新 pi-web / pi 依赖 · 更新 Pi Web Tray · 下载 Node.js…
环境自检 · 设置 · 日志 · 数据/程序目录 · 配置文件 · 复制地址 · 桌面快捷方式
开机自启动 · 自启动使用管理员权限 · 以管理员身份重启 · 关于 · 退出
```

- 左键单击托盘图标：服务未运行时启动并打开浏览器；已运行时仅打开浏览器。
  **启动服务** 和 **重启服务** 不会打开浏览器。
- **更新 pi-web / pi 依赖** 会先停止 pi-web（npm 无法替换运行中的包），
  `InstallPiCli=1` 时同时更新两个包；只有更新前服务在运行才自动重启。
  进度窗口支持取消。
- **更新 Pi Web Tray** 校验 release 的 SHA-256、替换文件（必要时 UAC）、
  保留服务运行状态，并清理新版本已移除的文件。取消会立即中止下载请求。
- 监听端口不是 pi-web 时报告 **端口冲突**：不会视为运行中，也不会停止或杀掉它；
  请在 **设置** 中修改 `Port`。
- pi-web 意外退出会通知；`AutoRestart=1` 时自动重启。`AutoCheckUpdates=1`
  会在启动后检查新版本。
- 环境自检显示运行组件（版本、路径、状态）和应用信息（配置、端口、目录、日志）。

## 配置

`pi-web-tray.ini` 在首次启动时生成：普通目录在可执行文件旁，受保护目录在
`%LOCALAPPDATA%\pi-web-tray`。带注释的样例见
[`config/pi-web-tray.ini`](config/pi-web-tray.ini)。

| 键                 | 默认值          | 说明                                                 |
| ------------------ | --------------- | ---------------------------------------------------- |
| `Port`             | `30141`         | pi-web 监听端口。                                    |
| `WorkingDir`       | `%USERPROFILE%` | 服务工作目录，也是 pi 的默认项目目录。               |
| `AutoInstall`      | `1`             | 缺少 pi-web 时自动安装。                             |
| `AutoRestart`      | `0`             | pi-web 意外停止后自动重启。                          |
| `AutoCheckUpdates` | `0`             | 启动后检查托盘更新。                                 |
| `InstallPiCli`     | `1`             | 同时安装 pi 命令行。                                 |
| `InstallTag`       | `latest`        | npm 分发标签或具体版本号。                           |
| `NpmRegistry`      | 空              | 自定义 npm 源，如 `https://registry.npmmirror.com`。 |
| `NodePath`         | 空              | 指定 `node.exe`；留空自动检测。                      |
| `ServerScript`     | 空              | 指定 `pi-web.js`；留空自动检测。                     |
| `Language`         | `auto`          | `auto`、`en`、`zh`（简体）或 `zh-Hant`。             |
| `AutostartAdmin`   | `0`             | 自启动使用管理员权限（由菜单维护）。                 |

值支持 `%ENV%` 展开；含 `;` 或 `#` 的值可加引号；注释符号前需有空白。

| 环境变量                     | 说明                            |
| ---------------------------- | ------------------------------- |
| `PI_WEB_TRAY_PORT`           | 覆盖配置的端口。                |
| `PI_WEB_TRAY_LANG`           | 覆盖界面语言。                  |
| `PI_WEB_TRAY_HOME`           | 覆盖数据目录。                  |
| `HTTP_PROXY` / `HTTPS_PROXY` | 供 npm 和 pi-web 模型请求使用。 |

## 命令行

```text
pi-web-tray.exe                  启动 pi-web 并打开浏览器。
pi-web-tray.exe --autostart      启动托盘和服务，不打开浏览器。
pi-web-tray.exe --tray-only      仅启动托盘。
pi-web-tray.exe --start-no-open  启动服务，不打开浏览器。
pi-web-tray.exe --stop           停止服务，保留托盘。
pi-web-tray.exe --quit           停止服务并退出。
pi-web-tray.exe --check          检查环境（0 = 就绪，1 = 缺少组件）。
pi-web-tray.exe --install        安装依赖，不启动托盘。
pi-web-tray.exe --dry-run        与 --install 配合，仅打印 npm 命令。
pi-web-tray.exe --diag           将诊断快照写入 diag.txt。
pi-web-tray.exe --version        显示版本。
pi-web-tray.exe --help           显示命令行帮助。
```

未知参数返回退出码 2。

## 从源码构建

需要 Windows 10/11、Visual Studio 2022/2026（.NET 桌面开发工作负载）和
.NET Framework 4.8 targeting pack（或 .NET SDK 8+）。

```bat
git clone https://github.com/Arondight/pi-web-tray.git
cd pi-web-tray
build.cmd
```

输出：`artifacts\pi-web-tray.exe`、`pi-web-tray.exe.config` 以及 `zh-Hans` /
`zh-Hant` 资源目录。应用本身无 NuGet 依赖；测试项目会还原 MSTest。
构建前请先退出运行中的 `pi-web-tray.exe`。
`scripts\package-release.ps1` 生成发布包和文件清单。

## 故障排查

| 现象                   | 处理方法                                                                                       |
| ---------------------- | ---------------------------------------------------------------------------------------------- |
| 提示需要 Node.js       | 程序会提供下载页并退出；安装 Node.js 22.19+ 后重新运行。仍检测不到时在 ini 中设置 `NodePath`。 |
| 安装失败               | 查看 `install.log`：npm 全局目录不可写、`InstallTag` 无效、代理或网络问题。                    |
| 更新失败               | 查看 `update.log`：常见原因是取消 UAC、代理或网络；可重新执行 **更新 Pi Web Tray**。           |
| 红色图标，端口冲突     | 其他程序占用了端口，请在 **设置** 中换一个空闲端口；托盘不会杀掉它。                           |
| 红色图标，服务失败     | 查看 `server.log`：`EADDRINUSE` 表示端口冲突，`Cannot find module` 通常表示安装不完整。        |
| `Cannot launch conpty` | ConPTY 需要 Windows 10 1809+；Node.js 可用的环境下聊天和工具仍可使用。                         |
| 开机自启动无效         | 检查托盘菜单状态，然后检查 `HKCU\...\Run` 或 `schtasks /query /tn PiWebTray`。                 |
| Windows 7 无法启动     | 从上方链接安装 .NET Framework 4.8，并确认 `pi-web-tray.exe.config` 与可执行文件同目录。        |
| 需要更多诊断           | 执行 `pi-web-tray.exe --diag` 查看 `diag.txt`；托盘崩溃时查看 `crash.log`。                    |

## 安全与限制

- 默认以当前用户令牌运行（`asInvoker`），仅在用户请求时提权；无遥测、无网络监听。
- 更新通过 HTTPS 从 GitHub 下载，并用 release 发布的 SHA-256 校验；更新助手还会校验
  目标安装与暂存文件指纹。发布包未签名，首次运行可能触发 SmartScreen。
- `NodePath`、`ServerScript`、`WorkingDir`、`NpmRegistry`、`InstallTag` 属于受信任配置：
  可写的 ini 等同于代码执行权限。
- 不会自动安装 Node.js；要求 22.19 或更新版本。
- npm 12 默认阻止依赖脚本。安装 pi-web 时一次性传入 `--allow-scripts`
  （保留已有条目并加入 `@agegr/pi-web`、`node-pty`），pi CLI 使用 `--ignore-scripts`；
  不会修改 npm 配置。
- pi 正在运行时无法更新 pi CLI（文件被占用）；请退出 pi 后重试。
- 管理员自启动要求当前账户是管理员，且是整机范围。托盘以管理员运行时，
  `--stop` / `--quit` 需在管理员命令行执行；托盘菜单始终可用。

## 相关项目

Pi Web Tray 只负责安装和启动 [pi-web](https://github.com/agegr/pi-web)
（`@agegr/pi-web`）与 [pi](https://github.com/earendil-works/pi)
（`@earendil-works/pi-coding-agent`），不包含也不修改它们；相关问题请提交到各自仓库。

## 参与贡献

参与前请阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。安全问题请按照 [SECURITY.md](SECURITY.md)
报告。

## 许可证

[MIT](LICENSE)
