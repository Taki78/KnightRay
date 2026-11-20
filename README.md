# KnightRay for Linux, Windows, and Android

[Download KnightRay from GitHub Releases](https://github.com/Taki78/KnightRay/releases).

KnightRay forks v2rayN on desktop and v2rayNG on Android. On desktop, open **Tools → Config Fetcher & Tester**
to manage repository sources, paste share links, test real HTTPS traffic, and import
selected working profiles into **Fetched Configs**. In **Working**, select one result
and click **Share QR code** to share it before importing.

On Windows x64, extract `KnightRay-windows-x64.zip` and open `KnightRay.exe`.
The ZIP includes the .NET runtime, Xray, and sing-box. No installer or administrator
access is needed for ordinary proxy use. The app keeps its files in
`%LOCALAPPDATA%\v2rayTaki`, so it can reuse your previous v2rayTaki settings and stay separate from v2rayN. To build the ZIP from source after
initializing the submodules, run `python scripts/package-v2raytaki-windows.py`
with the .NET 10 SDK and Python 3 installed.

Build a self-contained Linux archive with the .NET 10 SDK and Python 3. Initialize the upstream
hotkey submodule if needed:

```sh
git submodule update --init --recursive
./scripts/package-v2raytaki.sh
tar -xzf dist/KnightRay-linux-x64.tar.gz
./KnightRay-linux-x64/install.sh
```

The installer requires no administrator access. It installs a **KnightRay** desktop
entry and a `~/.local/bin/knightray` launcher. The Linux archive includes the
.NET runtime, Xray, and sing-box. The packaging script also supports ARM64.
Avoid the upstream Debian/RPM packaging
scripts when installing this fork.

On Linux, KnightRay reuses the existing settings and profiles under
`${XDG_DATA_HOME:-~/.local/share}/v2rayTaki`. On both platforms it defaults to
local proxy port 11808 and leaves desktop proxy settings unchanged. Use only one
client to manage the system proxy or TUN at a time.

The desktop fetcher starts with **All enabled repositories** selected and tests up to 35 entries per
file. Click **Fetch & Test** to start, or use the menu next to **Entries per file**
to test a single repository or pasted links. The results tabs fill as tests finish;
check **Other results** for rejected entries and download issues.
Use **Restore defaults** in the Sources tab to bring back deleted default
repositories. Existing sources are left as they are.

On desktop, click **Start Auto Select** to check all supported configs in the
current list. Turn on **Selected rows only** to use just the highlighted rows
(hold Ctrl or Shift to select multiple). The list is captured when you start.
After each round finishes, Auto Select waits 300 seconds by default; set the
wait as low as 5 seconds.
The app picks the working config with the lowest HTTPS delay in each check.
It keeps the current config if it ties for lowest delay or if none pass.
Checks continue while the app is in the tray, until you click
**Stop Auto Select** or exit KnightRay.

See [plan.md](plan.md) for the design and verification criteria. The upstream README
continues below.

On [Android](android/README.md), install the ARM64 APK and open **KnightRay**.
Use **Config Fetcher & Tester** in the navigation drawer or **Start Auto Select**
above the main config list.

---

# v2rayN

### A GUI client for Windows, Linux and macOS. Support [Xray](https://github.com/XTLS/Xray-core) and [sing-box](https://github.com/SagerNet/sing-box) and [others](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![CodeFactor](https://www.codefactor.io/repository/github/2dust/v2rayn/badge)](https://www.codefactor.io/repository/github/2dust/v2rayn)
[![Release](https://img.shields.io/github/v/release/2dust/v2rayN?logo=github&label=Release)](https://github.com/2dust/v2rayN/releases)
[![Downloads](https://img.shields.io/github/downloads/2dust/v2rayN/latest/total?logo=github&label=Downloads)](https://github.com/2dust/v2rayN/releases)
[![Telegram](https://img.shields.io/badge/Telegram-Chat-26A5E4?logo=telegram)](https://t.me/v2rayn)
 
[![Windows](https://img.shields.io/badge/Windows-supported-0078D6?logo=windows)](https://github.com/2dust/v2rayN) 
[![Linux](https://img.shields.io/badge/Linux-supported-FCC624?logo=linux&logoColor=000)](https://github.com/2dust/v2rayN) 
[![macOS](https://img.shields.io/badge/macOS-supported-000000?logo=apple)](https://github.com/2dust/v2rayN) 
[![GPG Signed](https://img.shields.io/badge/GPG-signed-4B32C3?logo=gnuprivacyguard)](https://github.com/2dust/v2rayN)


---

## Download / 下载

Download the latest release here:

在这里下载最新版本：

[https://github.com/2dust/v2rayN/releases](https://github.com/2dust/v2rayN/releases)


> [!TIP]
> v2rayN is the desktop version. For the mobile version, please visit the v2rayNG \
> v2rayN 是电脑版，手机版请访问 v2rayNG
>
> https://github.com/2dust/v2rayNG

---

## Documentation / 使用文档

Read the Wiki for usage guides and configuration details.

请阅读 Wiki 获取使用说明和配置教程。

[https://github.com/2dust/v2rayN/wiki](https://github.com/2dust/v2rayN/wiki)

---

## Supported Platforms / 支持平台

| Platform / 平台 | x64 | x86 | arm64 | riscv64 | loong64 | ppc64le |
| --- | --- | --- | --- | --- | --- | --- |
| Windows | ✅ | ✅ | ✅ | - | - | - |
| Linux | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| macOS | ✅ | - | ✅ | - | - | - |

Minimum OS requirements: [Release files introduction](https://github.com/2dust/v2rayN/wiki/Release-files-introduction) / 最低系统要求：[发布文件介绍](https://github.com/2dust/v2rayN/wiki/Release-files-introduction)

---

## GPG Verification / GPG 签名校验

Release files are signed with GPG to verify authenticity and integrity, helping prevent mirror, ISP, or CDN hijacking.

发布文件已使用 GPG 签名，可用于校验文件真实性与完整性，预防镜像站、运营商或 CDN 劫持。

### Fingerprint / 公钥指纹

```text
7694 5E9F 3E9A 168F 8070 F195 805D 661C
134D FAF6 8903 C199 463C 31E5 AE90 3AE0
```

---

## Community / 社区

Telegram Group / Telegram 群组：

[https://t.me/v2rayN](https://t.me/v2rayN)

Telegram Channel / Telegram 频道：

[https://t.me/github_2dust](https://t.me/github_2dust)
