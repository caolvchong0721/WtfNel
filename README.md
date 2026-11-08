<div align="center">

# WtfNel

**脱离 WPF 启动器，直接启动布吉岛的网易版 Minecraft 启动器**

内置进程注入器，附带两个原生 payload：一个做包转发，一个做硬件信息伪装。

![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D6?logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)
![language](https://img.shields.io/badge/language-C%23%20%2B%20C%2B%2B-00599C?logo=cplusplus&logoColor=white)
![arch](https://img.shields.io/badge/arch-x64-lightgrey)

</div>

---

## 目录

- [功能](#功能)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [构建](#构建)
- [在 Rider 中开发](#在-rider-中开发)
- [日志](#日志)
- [关于](#关于)
- [致谢](#致谢)

---

## 功能

| 模块 | 说明 |
| --- | --- |
| **脱离 WPFLauncher** | 自己拼启动参数、自己维护父进程，不再依赖官方启动器窗口 |
| **账号** | 网易邮箱密码或 `sauth_json`，走 Yggdrasil 认证，可选择角色进入 |
| **安装** | 按 md5 增量补齐客户端、核心 mods 与服务器资源包 |
| **转发** | 可选本地端口转发，把客户端流量引到指定服务器 |
| **注入** | 反射注入两个 payload，检测到窗口标题含「布吉岛」就注入 |
| **端口** | B 侧服务端从 `127.0.0.1:13337` 起，被占用自动加一 |

---

## 环境要求

- Windows 10 / 11 **x64**
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Visual Studio Build Tools，需要包含 **「使用 C++ 的桌面开发」**（提供 MSVC、CMake、Ninja）
- 目标进程是 64 位 JVM，payload 必须是 **x64**

---

## 快速开始

双击 `WtfNel.Ui.exe`，或者在仓库根目录执行：

```bash
dotnet run --project src/WtfNel.Ui
```

在「账号」页添加账号，回到「主页」登录、选角色，点「启动游戏」。「设置 → 注入」里开着的 payload 会在检测到游戏窗口后自动注入。

端口就绪时「日志」页会显示：

```text
[inject] 代理端口已开启：127.0.0.1:13337
```

---

## 构建

### 构建整个解决方案

```bash
dotnet build WtfNel.slnx
```

> 输出：`src/WtfNel.Ui/bin/Debug/net9.0-windows/WtfNel.Ui.exe`，payload 自动复制到同目录 `payloads/`。

### 发布单文件版本

```bash
dotnet publish src/WtfNel.Ui/WtfNel.Ui.csproj -p:PublishProfile=win-x64-single
```

> 输出：`src/WtfNel.Ui/bin/publish/WtfNel.Ui.exe`，payload 在 `publish/payloads/`。

### 只构建 payload

```powershell
powershell -File native/build.ps1 -Configuration Release
```

> 输出：`native/build/WtfNel.Payload.Proxy.dll` 与 `native/build/WtfNel.Payload.HwidSpoof.dll`。

### 跳过原生构建（只编 C#）

```bash
dotnet build WtfNel.slnx -p:SkipNativePayload=true
```

---

## 在 Rider 中开发

1. 打开仓库根目录的 `WtfNel.slnx`，等待 NuGet 还原。
2. SDK 设为 **.NET 9**，选中 `WtfNel` 运行配置后直接运行。
3. 点「构建解决方案」时会顺带调用 `native/build.ps1` 编译两个原生 DLL，并复制到 `payloads/`。
4. 要单独调 C++，在 Rider 里打开 `native/` 目录，用 CMake 配置构建。

---

## 日志

| 路径 | 内容 |
| --- | --- |
| `logs/shadow-*.log` | 启动器与注入器 |
| `%TEMP%\WtfNel\proxy.log` | 包转发 payload |
| `%TEMP%\WtfNel\hwidspoof.log` | 硬件伪装 payload |
| `%TEMP%\WtfNel\proxy-port.txt` | B 侧实际监听端口 |

---

## 关于

- payload 的原始实现来自 **SakuraTools** ，本仓库只做移植、整合与构建脚本。
- 注入器、启动器、认证与安装链路为 **C#** 实现。
- 端口、进程名、类名等按 **1.20 客户端**对齐，换版本需要重新核对。

---

## 致谢

- **人工智能！。，？！**：DeepSeek-V4.1-Flash 1017、Claude Opus 5.5、Claude Opus 4.8
- **包转发**：[GzSakura1338/SakuraTools](https://github.com/GzSakura1338/SakuraTools)
- **HwidSpoof**：caolvchong1337
