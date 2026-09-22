# Battuta Privacy Policy

Effective date: September 22, 2026

Battuta is a keyboard and pointer sound application for Windows. This policy
describes the information processed by the Windows version of Battuta and how
that information remains under the user's control.

## Information processed on the device

Battuta processes the following information locally to play sounds and create
optional aggregate statistics:

- physical keyboard key identifiers and press/release states;
- mouse button identifiers and press/release states;
- event timestamps;
- the identity of the foreground application;
- aggregate per-key, per-day, and per-application counts, including daily mouse click totals;
- application settings and user-created sound packs.

Battuta does **not** read, reconstruct, store, or transmit typed text,
passwords, clipboard contents, window titles, pointer coordinates, file
contents, or the contents of other applications.

## Storage and retention

Settings, custom sound packs, and aggregate statistics are stored only on the
user's Windows device. Battuta does not provide cloud synchronization or send
these records to Wormforce or any third party.

Users can delete aggregate statistics from within Battuta by selecting
"Clear all statistics". Uninstalling the Microsoft Store version removes its
application data. Portable-version data can also be removed from the Battuta
folder under the user's local application-data directory.

## Network access

The Microsoft Store version relies on Microsoft Store for installation and
updates and does not use an independent updater. A portable build may contact
the public GitHub Releases API only when the user enables or manually requests
an update check. Battuta contains no advertising, telemetry, analytics, or
remote input-logging service.

## Windows permissions

Battuta is a packaged WPF desktop application. It uses full-trust desktop
execution to provide notification-area integration, low-level keyboard and
mouse hooks, WASAPI audio playback, local SQLite storage, and an optional
Windows StartupTask. Battuta does not request administrator elevation, install
a service or driver, or inject code into other processes.

## Sharing and sale

Battuta does not sell, rent, disclose, or share user information with third
parties. All input-derived statistics remain local to the user's device.

## Changes to this policy

Material changes to this policy will be published in this repository and, when
appropriate, reflected on the Battuta product page.

## Contact

Questions about this policy can be sent to
[team@wormforce.net](mailto:team@wormforce.net).

---

# Battuta 隐私政策

生效日期：2026 年 9 月 22 日

Battuta 是一款 Windows 键盘与鼠标音效应用。本政策说明 Windows 版 Battuta
在用户设备上处理哪些信息，以及用户如何控制这些信息。

## 在设备上处理的信息

为了播放声音并生成可选的汇总统计，Battuta 仅在本机处理：

- 物理键盘按键标识及按下/抬起状态；
- 鼠标按钮标识及按下/抬起状态；
- 事件时间；
- 前台应用身份；
- 按键、日期和应用维度的汇总次数，包括每日鼠标点击总数；
- 应用设置和用户创建的音色包。

Battuta **不会**读取、还原、保存或传输输入文字、密码、剪贴板内容、窗口标题、
鼠标坐标、文件内容或其他应用的内容。

## 存储与保留

设置、自定义音色包和汇总统计仅保存在用户的 Windows 设备上。Battuta 不提供云
同步，也不会把这些记录发送给 Wormforce 或任何第三方。

用户可以在 Battuta 中选择“清除全部统计”删除汇总统计。卸载 Microsoft Store
版本会删除其应用数据；便携版数据也可以从用户本地应用数据目录中的 Battuta
文件夹手动删除。

## 网络访问

Microsoft Store 版本由 Microsoft Store 管理安装和更新，不使用独立更新器。
便携版仅在用户允许自动检查或主动检查更新时访问公开的 GitHub Releases API。
Battuta 不包含广告、遥测、分析或远程输入记录服务。

## Windows 权限

Battuta 是打包的 WPF 桌面应用。它使用完全信任桌面执行来提供通知区域集成、
低级键盘与鼠标钩子、WASAPI 音频播放、本地 SQLite 存储和可选的 Windows
登录启动任务。Battuta 不请求管理员提权，不安装服务或驱动，也不向其他进程
注入代码。

## 共享与出售

Battuta 不出售、出租、披露或与第三方共享用户信息。所有由输入事件产生的统计
都保留在用户设备上。

## 政策变更

本政策如有重大变更，将在本仓库中公布，并在适当情况下同步更新 Battuta 产品页。

## 联系方式

如有隐私相关问题，请联系
[team@wormforce.net](mailto:team@wormforce.net)。
