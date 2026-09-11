# Issue / PR 处理记录（2026-09-07）

核查仓库：`wormforce/battuta`。基线：`main` / `872dc693ca23f8ee9f4bf22ea557acd00783d918`。
以下是截至 2026-09-07 的处理与验证记录：当时修改位于本地分支 `codex/issue-feedback`，尚未推送、发布或发送 GitHub 评论。

## #20：长按连续发声

[原始反馈](https://github.com/wormforce/battuta/issues/20) 希望持续按住退格等按键时连续发声。

macOS 和 Windows 原先都过滤自动重复的按下事件。本次在两端键盘设置中增加“长按连续发声”开关，默认关闭并持久化。开启后重复事件播放按下音，跟随系统重复延迟和速度；松开后不再产生重复声音。回弹音设置独立生效，暂停键盘声音会同时停止重复事件的播放。统计仍收到原始 `isRepeat` 标记，物理按下次数不增加。

macOS 提供简体中文和英文文案。系统未产生自动重复事件的按键不连续发声，例如使用重音选择的字母长按。没有新增计时器或额外的全局输入监听。

## #22：Windows 找不到 BCP

[原始反馈](https://github.com/wormforce/battuta/issues/22) 没有给出版本号、安装来源或复现步骤。

已下载 GitHub `v1.2.2` 的 `Battuta-Windows-1.2.2-win-x64.zip`，确认：

- ZIP 的 SHA-256 与发布的校验文件匹配。
- 包含 `BundledSoundPacks/15d04652-5265-4ea7-a376-8a7e11ff6813.simuboardpack/`。
- manifest 名称为 `BCP (Suit80)`，28 段音频的 SHA-256 和大小均匹配，授权文件存在。
- 在 macOS 的临时 .NET 程序中编译实际 Windows 音色库及校验器源码，以发布包文件重建应用默认资源目录，成功列出 21 种音色并加载只读 BCP 的 28 段音频。这验证资源加载逻辑，不替代 Windows UI 或 Microsoft Store 安装验证。

没有复现缺失故障，不重复修改已有的 BCP 打包实现。已补充 Windows README 中的选择入口、完整解压要求和诊断信息，并新增默认应用资源路径回归测试。

仍需报告者提供版本号、安装来源和资源目录是否存在，才能区分版本差异、文件缺失与运行时加载失败。

## PR 核查

当前没有打开的 PR。最近的 [#21](https://github.com/wormforce/battuta/pull/21)（macOS 本地化和外观）已合并，没有 review 或行内评论待处理。

BCP 已通过 [#10](https://github.com/wormforce/battuta/pull/10)、[#11](https://github.com/wormforce/battuta/pull/11)、[#13](https://github.com/wormforce/battuta/pull/13) 接入 Windows、验证发布资源并修正测试；共享资源迁移 [#16](https://github.com/wormforce/battuta/pull/16) 也已合并。`v1.2.2` 的 Windows release workflow（run `33413402042`）结果为 success。

## 可发送的回复草稿（尚未发送）

### #20

已在 macOS 和 Windows 源码中增加“长按连续发声”开关。开启后，长按退格或方向键会跟随系统按键重复节奏播放声音；默认关闭，设置会保存，也不会增加统计中的物理按下次数。改动尚未发布到安装包。

### #22

核对了 GitHub 的 Windows 1.2.2 便携包，BCP (Suit80) 已包含在内。可以在通知区域面板的键盘音色下拉列表中向下滚动，找到“BCP (Suit80) · 线性”。请完整解压 ZIP，保留 EXE 旁的 BundledSoundPacks 文件夹。若仍找不到，请补充版本号、安装来源（GitHub 或 Microsoft Store），以及 BundledSoundPacks 文件夹是否存在，我们再继续定位。

## 验证

- macOS Release 构建通过。
- macOS DIY harness：523 项断言通过，含长按开关、保存和播放策略回归。
- macOS Typing Stats harness：216 项断言通过。
- Windows Release 全工程交叉编译通过，0 errors；8 条警告来自未修改的音频接口和统计测试代码。
- Windows Core 测试：121 / 121 通过。
- Windows 设置与播放开关测试：通过链接实际源码和原测试文件到临时 `net10.0` 测试项目，在 macOS 运行 7 / 7 通过。
- 共享资源校验、浏览器扩展 `npm test`、629 个中英文 localization key 对齐、strings / XAML 语法检查通过。
- `git diff --check` 通过。
- Windows 原生 WPF 测试尚未运行；新增 BCP 默认路径测试已编译，并另行执行上述资源加载检查。
