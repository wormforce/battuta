# Battuta for Windows

原生 Windows 10/11 键盘与鼠标音效应用。Windows 版本以现有 macOS SwiftUI 界面、行为和 `shared/` 中的 `237` 个基础内置音频资源加 `1` 个只读 bundled `.simuboardpack`（BCP (Suit80)，含 `28` 个 WAV）为事实来源，在 `BattutaWindows/` 中独立实现，不读取或修改 `SimuBoardMac/`。

## 开发环境

- Windows 10 22H2 或 Windows 11
- .NET SDK 10.0.400（由仓库根目录 `global.json` 固定）
- Visual Studio 2022/2026 的 .NET 桌面开发工作负载，或 `dotnet` CLI

## 构建

```powershell
dotnet restore BattutaWindows.sln
dotnet build BattutaWindows.sln
dotnet test tests/Battuta.Core.Tests/Battuta.Core.Tests.csproj
dotnet test tests/Battuta.Windows.Tests/Battuta.Windows.Tests.csproj
```

## 长按连续发声

在通知区域面板的键盘设置中开启“长按连续发声”，长按退格、方向键等会跟随 Windows 的按键重复延迟和速度播放按下音，松开后停止。默认关闭，修改后自动保存。回弹音仍只在真实松开时播放，物理按下次数不计入长按重复。

## 找到 BCP (Suit80)

打开通知区域的 Battuta 面板，在键盘音色下拉列表中向下滚动，选择 `BCP (Suit80) · 线性`。它位于基础内置音色之后，是只读内置音色，无需自行导入。

GitHub 的 Windows 1.2.2 便携 ZIP 包已包含 BCP 的 28 段录音。请完整解压后运行 `Battuta.exe`，不要只复制 EXE：同目录必须保留 `BundledSoundPacks/15d04652-5265-4ea7-a376-8a7e11ff6813.simuboardpack/` 及其 `manifest.json`、`assets/` 和 `licenses/`。旧版本用户可从 [GitHub Releases](https://github.com/wormforce/battuta/releases) 下载完整便携包。

若仍未显示，请在 issue 中提供应用版本、安装来源（GitHub 便携包或 Microsoft Store）及该目录是否存在，以便区分版本差异、解压不完整和加载失败。无需删除设置或统计数据库。

## Windows 安装包

正式安装链路使用 MSIX。开发签名包、Microsoft Store 包、官网直发签名包和
`.appinstaller` 自动更新的构建方法见
[`src/Battuta.Packaging/README.md`](src/Battuta.Packaging/README.md)。

xUnit v3 使用 Microsoft Testing Platform；测试工程必须作为位置参数传给
`dotnet test`，不要改写成 `dotnet test --project ...`。

Windows 代码分为：

- `src/Battuta.Core`：平台无关的物理键、音色、设置和 DIY 包规则。
- `src/Battuta.Windows`：WPF 界面、Win32 输入、WASAPI 音频、SQLite 统计和系统集成。
- `src/Battuta.Packaging`：发布清单与打包辅助资料。
- `tests`：平台无关和 Windows 集成测试。

## 已实现功能

- Windows 通知区域面板、深色右键菜单、单实例激活和登录启动。
- 全局物理键盘与鼠标按下/抬起监听，不做字符转换。
- 21 种键盘音色、5 种指针音色、独立音量、回弹音和自然变化；其中 BCP (Suit80) 以只读 bundled `.simuboardpack` 发布。
- 本地 SQLite 输入统计、四种趋势范围、应用时间线、年度对比和逐键热力图。
- DIY 音色的三种映射模式、音频导入/试听、完整击键拆音、保存启用和安全导入导出。
- 内置与自定义音色重启恢复；GitHub Release 更新检查。

统计、DIY 和拆音窗口使用 Windows 原生标题栏；应用内容的颜色、卡片、尺寸比例和
信息层级继续以 SwiftUI 版本为准。portable 版本发现更新后打开经过校验的 GitHub
Release 页面，不会在运行中自行覆盖程序目录。

## 隐私边界

公开隐私政策见仓库根目录的 [`PRIVACY.md`](../PRIVACY.md)。

应用只使用物理键 ID、按下/抬起、鼠标按钮、时间和前台应用身份来播放音效与生成本地聚合统计；不调用字符转换 API，不读取或保存输入文本、密码、窗口标题、剪贴板或鼠标位置。
