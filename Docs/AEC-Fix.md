# 回声消除（AEC）修复说明

## 背景

LiveKit Unity SDK 之前依赖 `AudioSourceOptions.EchoCancellation = true` 来开启回声消除。Rust/WebRTC APM 在 Unity 管线下**永远拿不到远端播放音频（reverse stream）**，因此 AEC 的自适应滤波器没有参考信号，表现为：

- 轻度：持续的回声（对方听到自己说话被延迟回传）
- 中度：回声累积成"金属颗粒"声
- 重度：正反馈自激 → **持续啸叫**

## 修复方案

核心改造是让 SDK 自己在 C# 端持有一个共享 APM 实例，把 Unity 管线两端的音频都喂进去：

- **近端（capture）**：`RtcAudioSource.OnAudioRead` 把麦克风 PCM 在送往 Rust 前先过 `AudioProcessingModule.ProcessStream`。
- **远端（reverse）**：`AudioStream.OnAudioRead` 把即将播给 Unity `AudioSource` 的 PCM 同步喂给 `AudioProcessingModule.ProcessReverseStream`，作为 AEC 参考信号。
- APM 帧长固定为 10 ms，C# 侧做了分帧环形缓冲 + iOS 24 kHz 升/降采样。
- 多路同时播放的远端流会被混音后再送 APM。

具体涉及的文件：

- 新增 `Runtime/Scripts/AudioProcessingModule.cs`
- 新增 `Runtime/Scripts/Internal/AecBus.cs`
- 改动 `Runtime/Scripts/RtcAudioSource.cs`、`Runtime/Scripts/AudioStream.cs`
- 新增 `Tests/EditMode/AudioProcessingModuleTests.cs`

---

## 业务侧可调参数

所有调优入口都在静态类 `LiveKit.Internal.AecBus`（`internal`，集成方可以通过 `InternalsVisibleTo` 或把它公开为 `public` 使用）。

```csharp
using LiveKit.Internal;

AecBus.AecEnabled     = true;   // 回声消除
AecBus.NsEnabled      = true;   // 噪声抑制
AecBus.AgcEnabled     = true;   // 自动增益控制
AecBus.HpfEnabled     = true;   // 高通滤波（去低频轰鸣）
AecBus.StreamDelayMs  = 80;     // 近端 vs 远端延迟提示（ms）
```

这些开关只是 APM 内部模块的启停；`StreamDelayMs` 是告诉 AEC **"同一声音从远端播放到它被麦克风采到，大约经过了多少毫秒"**，是最需要针对运行环境调整的参数。

### 各开关推荐值

| 参数 | 默认 | 推荐保持 | 什么时候关 |
|------|------|----------|-------------|
| `AecEnabled` | `true` | 语音通话场景 **务必开启** | 纯单向广播、远端永远没人说话、或 UGC 录制时可关 |
| `NsEnabled` | `true` | 手机/笔记本内置麦都建议开 | 专业外置麦 + 录音棚环境可关以保留细节 |
| `AgcEnabled` | `true` | 大多数场景开 | 如果上层有自己的音量控制/压限器，建议关掉避免双重压缩 |
| `HpfEnabled` | `true` | 保持开启 | 一般没有关闭需求；只有需要保留 80 Hz 以下低频时关 |

### `StreamDelayMs` 推荐值（**最重要**）

`StreamDelayMs` 是 WebRTC APM 的 `set_stream_delay_ms`，默认 80 ms。值**偏小**会让 AEC 对不上回声导致残留；值**偏大**会带来细微的语音劣化但通常不致命。宁可略大，不要偏小。

下表按"外放/耳机 + 采集链路"给出建议起始值（具体还要叠加 `AudioSettings.GetDSPBufferSize` 的 `bufferLength / sampleRate`）：

| 运行环境 | 音频输出 | 推荐 `StreamDelayMs` |
|----------|----------|----------------------|
| Windows PC（DSP buffer = Best latency） | 有线耳机 | **40 – 70** |
| Windows PC（DSP buffer = Good/Best performance） | 有线耳机 | **80 – 120** |
| Windows PC | 外放扬声器 | **100 – 180** |
| macOS | 有线耳机 | **40 – 80** |
| macOS | 外放扬声器 | **100 – 160** |
| iOS | 有线/Lightning 耳机 | **60 – 100** |
| iOS | 手机扬声器 | **120 – 200** |
| Android（低延迟音频，通常旗舰机） | 有线耳机 | **60 – 120** |
| Android（普通/OpenSL） | 有线耳机 | **120 – 200** |
| Android | 外放扬声器 | **150 – 250** |
| 任意平台 | **蓝牙耳机/音箱** | 在上表基础上 **+150 ~ +300** |
| 任意平台 | USB/雷电声卡 | 以驱动 buffer 为准，通常 **40 – 120** |

#### 经验法则

- **默认 80 ms 足以应付 PC/Mac 有线场景**，先别改。
- 如果听到明显回声（说话 0.2~0.5 秒后自己听到 / 对方听到）说明 delay 设小了，**每次 +40 ms** 往上加直到回声消失。
- 手机外放、蓝牙、低端安卓场景建议 **直接起步给 150 – 200 ms**，几乎不会出错。
- 没必要每帧改动；一次调用会立即走 FFI 生效（修复后已去掉 500ms 节流）。

#### 如何估算

```csharp
AudioSettings.GetDSPBufferSize(out var bufferLength, out var numBuffers);
float dspMs = 1000f * bufferLength / AudioSettings.outputSampleRate;

// DSP buffer 往返 + 设备自身延迟 + 经验偏置
AecBus.StreamDelayMs = Mathf.CeilToInt(dspMs * 2f) + 40;
```

若有条件做**启动自测**：关闭 AEC → 近端播一段白噪 → 用麦录下来 → 做互相关找峰值延迟 → 给 `StreamDelayMs` 赋这个值。生产项目一般不需要这么精细，按上表查一档即可。

---

## 运行时动态切换

`AecBus` 的五个属性都可以在任意时刻读写。修复后：

- 开/关 `AecEnabled / NsEnabled / AgcEnabled / HpfEnabled` 会触发 APM 实例重建（轨道起始会有极短的一帧静音约 10–20 ms，不可察觉）。
- 改 `StreamDelayMs` **不会**重建 APM，只下发一次 FFI，开销可忽略。

典型用法：

```csharp
// 进入"扬声器外放"模式
AecBus.StreamDelayMs = 180;

// 切回"有线耳机"
AecBus.StreamDelayMs = 60;

// 录音棚场景（用户插了专业设备）
AecBus.AecEnabled = false;
AecBus.NsEnabled  = false;
AecBus.AgcEnabled = false;
// HPF 保持开或关都行
```

---

## 关闭 Unity 侧、验证 Rust 侧效果

`RtcAudioSource` 构造时会把 Rust 端原本的三个开关强制关掉：

```csharp
newAudioSource.Options.EchoCancellation = false;
newAudioSource.Options.AutoGainControl  = false;
newAudioSource.Options.NoiseSuppression = false;
```

因为这三个开关在 Unity 场景下等价于"没开"（Rust 侧拿不到 reverse 信号）。现在完全由 `AecBus` 在 C# 侧统一处理，不要再去打开它们。

---

## 自检清单

部署后按顺序检查：

1. **本地自环测试**：自己加入一个 Room，同设备既是发送者也是订阅者。开外放 + 默认 `StreamDelayMs = 80`，说话应**不再啸叫**。如果仍有回声，按"经验法则"每次 +40 ms。
2. **切 `AecBus.AecEnabled = false`** 应立刻**复现啸叫**，以此确认 AEC 链路确实在工作。
3. 断开远端（没有 `AudioStream`），仅本地麦克风，不应该有任何 AEC 处理引入的伪音；说话应清晰、无颗粒。
4. 多路远端同时播放时，对方听到的对方声音应仍被正确消除（多路 reverse 自动混音喂 APM）。
5. 切后台/切前台后恢复（调用了 `MonoBehaviourContext.OnApplicationPauseEvent`），AEC 应在几帧内重新收敛。

---

## 常见问题

**Q：我想关掉 APM 所有处理，完全走"透传"。**
A：`AecEnabled = NsEnabled = AgcEnabled = HpfEnabled = false`，任意开关全关时 `AecBus` 会释放共享 APM 实例，完全零开销透传。

**Q：`StreamDelayMs` 设得太大会有什么副作用？**
A：AEC 自适应滤波器的对齐窗口变宽，对**极快的回声**（比如 20 ms 以内）消除效果会略打折，但总好过完全失配。偏大是安全方向。

**Q：延迟对不上会不会导致**"去掉了真正的语音"？
A：WebRTC AEC 是自适应的，对不上时主要表现为"消除不干净 / 残留回声"，不会大量削去近端语音。真正会误伤语音的是 NS 太强；此修复不调 NS 强度。

**Q：可以给不同的 `RtcAudioSource`/`AudioStream` 用不同 APM 吗？**
A：目前全局共享一个 APM 实例（由 `AecBus` 引用计数管理）。这也是 WebRTC 推荐做法——只有一个 APM 能正确建立 near-end 与 far-end 的对应关系。
