# Linux 音频产物契约(BTP1 + 混音器 golden)

冻结 PR 6 引入的跨实现音频契约:Rust `battuta-audio-prep` 是唯一写入方,
C# `PreparedAudioProductLoader`(`Battuta.Engine/Audio/Linux`)是读取方。
变更任何字节布局都需要两侧同步更新并重跑 golden 测试。

## 1. BTP1 产物容器

每个规范化资产对应一个 `.p48m` 文件,字节布局(全部 little endian):

| 偏移 | 长度 | 字段 | 值 |
| --- | --- | --- | --- |
| 0 | 4 | magic | `BTP1`(`0x31505442`)|
| 4 | 4 | sample rate | `48000` |
| 8 | 4 | channels | `1`(mono)|
| 12 | 4 | frame count | N(单声道样本数 = 帧数)|
| 16 | 4·N | payload | f32 LE 帧序列 |

读取方必须校验四项(magic/rate/channels/长度),任何不匹配抛
`InvalidDataException`;这是构建产物损坏时的快速失败,不做静默修复。

产物生成管线(`battuta-audio-prep`):

1. symphonia 解码 MP3/WAV → interleaved f32;
2. 多声道 mean downmix 到 mono;
3. 非特 48 kHz 源用 Catmull-Rom 立方重采样到 48 kHz
   (输出位置 i 恰好落在源位置 i·src/48000);
4. 键盘资产(press/release 目录)应用前导静音裁剪,逐行镜像
   `LeadingSilenceTrimmer.cs`(阈值 0.0008 f32、扫描上限 0.25 s、
   保留 preroll 0.00015 s、最小裁剪 0.0005 s,全部 ceil 取帧);
5. 指针资产不裁剪。

**已记录偏差**:重采样器为 Catmull-Rom 立方,与 Windows 管线的 WDL
重采样器不逐字节一致(计划 §6.2 允许);裁剪语义与 C# 完全一致。

## 2. manifest.json

与产物同目录,记录每个产物:

```json
{
  "sample_rate": 48000,
  "channels": 1,
  "products": [
    {
      "input": "mxblack/press/GENERIC_R0.mp3",
      "input_sha256": "<sha256 of the source asset>",
      "output": "mxblack/press/GENERIC_R0.p48m",
      "output_sha256": "<sha256 of the .p48m file>",
      "frame_count": 1234,
      "duration_seconds": 0.025708
    }
  ]
}
```

- `input` 相对 builtin 根(`shared/audio/builtin/`),`output` 相对产物根。
- 读取方在存在 `.sha256` sidecar 时必须校验产物哈希。
- 全流程确定性:同一输入集合的两次完整运行产生 byte-identical 的产物树
  (`battuta-audio-prep` 测试套件锁定)。

## 3. 混音器 golden fixture(`fixtures/audio/mixer-golden.bin`)

由 Rust `golden_gen` 生成,C# `MixerGoldenFixtureTests` 重放:

- 头:`BTAU` + u32 version(1)+ u32 sample_count;
- 每样本:u32 handle、u32 frame_count、f32 LE 数据;
- u32 chunk_frames(128)+ u32 event_count;
- 事件:u8 kind,`0` = schedule {u32 handle, f32 gain, f32 rate, u8
  allows_stealing},`1` = render(渲染 chunk_frames×2 帧立体声);
- 尾部:期望输出 f32 LE(interleaved stereo),与重放输出**逐位相等**
  (int32 bits 比较,非容差)。

transcript 必须行使 voice steal(键盘偷最旧)与 command drop(512 满、
指针抢占被拒)两条路径,两侧计数器同时断言。

## 4. C ABI 语义(非 wire 契约,参考)

`battuta_audio_*` 符号的完整清单见 `BattutaLinux/native/crates/battuta-audio/src/ffi.rs`;
本文件只冻结跨语言可观察语义:

- `battuta_audio_sample_register` 返回 handle+1,`0` 表示失败;
- release 是延迟回收:voice 仍引用的 slot 由 RT 线程最终递减,实际
  释放只发生在控制线程 sweep;
- `battuta_audio_schedule` 返回 `1` = 接受,`0` = 拒绝(非有限参数、
  ring 满),`<0` = 引擎错误;
- 事件回调:`1` = STREAM_READY,`2` = STREAM_ERROR(需要宿主重建流,
  `battuta_audio_stream_rebuild`),回调在 PipeWire 主循环线程触发,
  不得在回调内重入 shim。
