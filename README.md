# 未知伤亡音乐盒（UnknownCasualtiesMusicBox）

> **⚠️ 此内容（包含代码本体）含 AI 生成。**
> 本项目的源代码与文档由 AI 主要生成，经人工审查、编译与实机测试后发布。详见 [AI_DISCLOSURE.md](AI_DISCLOSURE.md)。
>
> **This content (including the source code itself) contains AI-generated material.**

参考 CS 音乐盒的事件驱动机制，为《Casualties Unknown》提供「一个音乐盒 + 按事件自动切换曲目」的 BepInEx 5 模组。

- 模组 GUID：`com.everywhere.unknowncasualties.musicbox`
- 版本：1.3.0
- 目标框架：.NET Framework 4.7.2（BepInEx 5 / Unity 2022.3.62f3）
- 依赖：BepInEx 5.4.23.5+、CUCoreLib 1.1（软依赖）
- 目标游戏版本：Casualties Unknown V1.0.2（本地实测）

## 设计要点

- **一个音乐盒，全部事件**：只需在设置里选一次音乐盒文件夹，模组会按事件自动从该文件夹取对应曲目。
- **12 个设置项，全部并入原版「音频」页**：`音乐盒`（下拉）+ 11 个按事件的音量滑条（0–100%，1% 步进）。
- **不新增标签、不新增侧栏**：直接注入原版 `Settings.settings`，控件、字体、间距、悬停/拖动反馈全部来自原版副本。
- **改动即时生效**：不需要“应用”按钮，也不需要重启游戏。
- **选歌键 = 文件夹名称**（严格），不使用音频元数据。

## 安装

1. 确认已安装 BepInEx 5.4.23.5+ 与 CUCoreLib 1.1。
2. 把 `dist\UnknownCasualtiesMusicBox.dll` 与 `data` 放到：

```
<游戏目录>\BepInEx\plugins\UnknownCasualtiesMusicBox\
    UnknownCasualtiesMusicBox.dll
    data\
        <音乐盒文件夹名称>\
            hall.mp3
            choose_part.mp3
            waiting_map_1.mp3
            waiting_map_2.mp3
            in_map.mp3
            attack_hurt.mp3
            articulos.mp3
            die.mp3
            final_result_1.mp3
            final_result_2.mp3
            final_result_3.mp3   （或 final_result_3_1 / final_result_3_2）
            suspend.mp3
```

3. 启动游戏 → 设置 → 「音频」页 → 选择音乐盒、拖动各事件音量。**选中即生效，无需应用按钮、无需重启。**

## 事件与文件对照

| 事件 | 文件名（无后缀） | 循环 | 触发点（已 IL 确认） |
|---|---|---|---|
| 大厅/主菜单 | `hall` | 循环 | 主菜单（`runSettingsScreen` 未激活，含开场封面/警告页） |
| 角色选择/交易 | `choose_part` | 循环 | `runSettingsScreen` 激活（点“开始游戏”后的角色/运行设置界面）、交易界面 |
| 加载地图（第一层） | `waiting_map_1` | 不循环 | `PreRunScript.StartRun` |
| 加载地图（第二层起） | `waiting_map_2` | 不循环 | `WorldGeneration.RegenerateWorld`（`biomeDepth ≥ 2`） |
| 进入地图/教程 | `in_map` | 不循环 | `MusicManager.PlayLayerSong`（层 BGM 起点）、教程 BGM |
| 攻击或受伤 | `attack_hurt` | 循环 | 真正受伤（受伤音 4 秒内）/ 原版威胁音乐 `threatMusicTime` / 疼痛骤增（>25 且跳变>8） |
| 濒死/辐射线 | `articulos` | 循环 | `Body.brainDying`、`Body.isDying`，或辐射线高度差<75 |
| 暂停/比赛结束 | `suspend` | 循环 | `PauseHandler.isPaused` 且 `pauseProgress > 0.5`（世界内、设置页未打开） |
| 死亡视角 | `die` | 不循环 | 原版 `deadClip`、自杀音乐 `whathaveidone` |
| 结局1（培养罐） | `final_result_1` | 不循环 | `BiopodEnding` / `Alt` / `Alt2` |
| 结局2（佣兵破土） | `final_result_2` | 不循环 | `MercEnding` |
| 结局3（逃脱自由）/MVP | `final_result_3` | 不循环 | `EscapeEnding` |
| 击杀长老棘背（Boss） | `final_result_2` | 不循环 | `ElderThornbackBehaviour.OnDestroy` → `Sound.Play("music/thornbackDefeat")` |

## 播放规则

- 新音乐立即开始，旧音乐在 **3 秒**内淡出到 0 后停止（循环音乐同样适用）。
- 非循环音乐自然播完即停；被打断时同样 3 秒淡出。
- 状态结束后**一定会淡出**（不会残留）。这是自动播放类事件的核心保证：`Poll()` 在“无目标事件”分支里必须淡出，而不是直接 return。
- 接管世界内音乐时，**原版音乐源也走 3 秒淡出**（不硬 `Stop`），避免叠加与硬切。
- 优先级：结局/死亡 > 暂停 > 濒死 > 攻击受伤 > 加载/进入 > 场景（大厅/角色选择）。
- MP3 播放器播放期间，`attack_hurt`、`articulos`、`in_map`、`waiting_map` 让位；结局/死亡/暂停仍接管。
- 音量 0% 为静音但保留所选音乐盒。
- 实际音量 = 音乐盒音量% × 游戏「音乐音量」，并沿用原版音乐混音组。
- 选择「游戏默认」= 完全不接管，原版行为保持不变。

## 文件解析规则

- 同名多格式优先级：`.mp3` > `.wav` > `.ogg`。
- 主文件缺失但存在 `<名称>_1` / `<名称>_2` … → 在分段中随机选择（`final_result_3` 即用此机制）。
- `.flac` 不受支持（Unity 2022 与 CUCoreLib 的 NAudio 均无 FLAC 解码器）：记日志并回退游戏默认。
- 文件夹被重命名/删除 → 回退「游戏默认」并记录清晰日志；恢复文件夹后自动继续生效（cfg 不被改写）。

## 配置

`BepInEx\config\com.everywhere.unknowncasualties.musicbox.cfg`

```ini
[General]
## 全局音乐盒：填写 data 下的音乐盒文件夹名称；留空表示“游戏默认”。
MusicBox =

[Volume]
## 每事件音量（0-100 整数百分比，1% 步进；0 表示静音但保留所选音乐盒）。
hall = 100
choose_part = 100
waiting_map = 100
in_map = 100
attack_hurt = 100
articulos = 100
suspend = 100
die = 100
final_result_1 = 100
final_result_2 = 100
final_result_3 = 100
```

- 原生 `settings.json` 也会镜像这 12 个值（`ucmb.box` / `ucmb.vol.<事件>`）；冲突时**以 cfg 为准**。
- `waiting_map` 一项同时控制 `waiting_map_1` 与 `waiting_map_2`。
- 从 v1.0.0（每事件配置）与 v1.1.0（单一 `Volume`）升级时，会自动迁移并清理旧键。

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

- 使用系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 离线编译。
- 源码为 C# 5 语法，输出 `dist\UnknownCasualtiesMusicBox.dll`。
- `-SelfTest` 开关可编译带自检的调试版（自检代码不会进入正式 DLL）。

## 已知限制

1. 不支持 `.flac`。
2. 接管 `in_map` 后，该层的原版层 BGM 会被整层压制（CS 音乐盒语义）。
3. `SurfaceBreachCutscene`、`EndLabScript end1/end2`、`levelup`、`Mindwipe` 保留原版行为。
4. 联机仅本地表现，不发送任何网络包，客机无需安装。
5. 原版没有独立的“角色选择界面”，角色选择与大厅在主菜单上；模组以 `runSettingsScreen` 是否激活区分两首曲子。
6. 与 Ultimate Sound Swap / Survival Cues 的共存未在本机验证（本机未安装、无网络）。
7. 单个音乐盒 11 个事件音量大列表需滚动；下拉列表超过 10 个音乐盒时的滚动体验未压测。

## 变更记录

### 1.3.0

- 设置项从「只有 2 项」恢复为 **12 项**：`音乐盒` + 11 个按事件音量，全部落在原版「音频」页（不再删除音量细项）。
- **修好“改了没反应”**：`Setting.apply` 现在作为即时生效入口（与原版设置同一条路径），并用 `InApplyAll` / `InLoadSettings` / 对账时刻三重判定区分“启动加载”与“用户操作”；另加 2 秒超时自愈，防止标志卡死导致永久失效。
- **修好“多出来的侧栏 / 挤掉多人游戏”**：不注册任何 CUCoreLib 自定义分类（`ModOptionsRegistry`），只做原生 `Setting` 注入；自检确认标签始终是原版 6 个（含「多人游戏」），`CUCoreLibModOptionTabFrame` 不存在。
- **修好“音乐盒显示两个”**：注入前按 `name` 幂等去重；自检打印重复项数量为 0。
- **修好“音频页一打开就停在底部”**：切页后延迟 3 帧把设置列表滚回顶部（否则用户只看到后半段音量项，像是音量项被删了）。
- **修好“角色选择界面不播 / 点了才播”**：新增 `PreRunScript.SelectCharacter` 后处理即时触发 `choose_part`。
- **修好“没暂停也在放暂停音乐、靠近尸体也有音乐”**：暂停判定要求 `pauseProgress > 0.5`；攻击/受伤判定移除 `averagePain > 60`、`earthquakeIntensity`、`irradiateIntensity` 等长期残留量，只保留真实受伤 / 原版威胁音乐 / 疼痛骤增。
- **修好“有些歌没有淡出”**：一次性音乐不再无条件屏蔽高优先级事件；接管世界内音乐时原版音源同样 3 秒淡出。
- **修好“切换音乐盒无效”**：`apply` 直接读取被点击控件的自身值（不再回头 `Find`）；删除会覆盖用户选择的 0.2 秒轮询；`SyncOpenMenu` 同时校正显示值与 `Setting.value`；切换后立即重播。
- **修好“进入地图音乐不播”**：原版 `StopSong` 不再打断一次性音乐；`in_map` 播放成功即标记本层，避免两条路径重复播放；状态机补兜底播放。
- **修好“重启后音乐盒变回游戏默认”**：`ValidateSavedBox` 不再因为名称比对失败就清空运行时选择；`HasBox` / `IndexOf` 改为忽略大小写与首尾空白，并新增 `ResolveBoxName` 按磁盘实际名称校正。
- **修好“点开始冒险就响加载音乐”**：改为 `StartRun` **后置** + 延迟 0.35 秒播放，等加载界面真正出现后再播 `waiting_map_1`。
- **修好“重启后完全没有音乐盒音乐”**（关键）：原先由自建 `MonoBehaviour`（`UC-MusicBox-Host`）的 `Update` 驱动状态机，实测在该运行环境下**该组件的 `Update` 从未被 Unity 调用**，导致状态机一次都没跑过——只有切换音乐盒时同步触发的重播会响一下，重启后自然只剩原版音乐。现改为用 Harmony 补丁驱动游戏自身的每帧方法：`PreRunScript.Update`（菜单/角色选择）与 `PlayerCamera.Update`（世界内），并用 `Time.frameCount` 去重保证每帧只 Tick 一次。
- **交易界面补上音乐**：`ResolveDesired()` 新增 `PlayerCamera.tradeMenu.activeSelf` 判定，映射到“角色选择界面音乐”；交易属于强交互态，允许打断仍在播放的 `in_map`。
- **地震补上音乐**：`earthquakeIntensity > 0.05` 重新纳入“攻击或受伤”判定。
- **压住原版战斗/交易音乐**：虫子/守卫/蜘蛛/交易触发的是 `MusicManager.droneSources`（drone 音轨），不在主 `source` 上，只拦 `PlaySong` 压不住。`DuckVanillaDrones()` 只把战斗主题（`currentThreatTheme`）与交易主题（索引 16）静音，其余环境/身体状态音轨不动。
- **对话/交易时的“闷响感”**：原版用 `PlayerCamera.muteTimeVolume`（`Clamp(-globalMuteTime*15,-80,0)` dB）压低整条音乐总线。本模组不再挂该 mixer，改为读取同一数值，映射成 0.30~1 的 duck 系数：给音乐盒音源加 `AudioLowPassFilter`（22000Hz → 480Hz）并同步小幅衰减，听感上是“变闷、变小”，但不会像原版那样被压到静音。
- **淡入**：新音乐改为 1.5 秒淡入（原先 0.25 秒，切入感偏硬）。
- **进入地图音乐等“层标题窗口”出现后再播**：`in_map` 不再在世界生成过程中响起；`PlayLayerSong` 在 `generatingWorld` 期间只压制原版、不播放，由状态机在 `!generatingWorld && (biomeTitle 激活 || 生成完成 3 秒)` 时补播。
- **第一层加载音乐只响一次**：`RegenerateWorld` 在 `biomeDepth <= 1` 时直接跳过（第一层交给 `StartRun`），并新增一次性音乐 3 秒防重（同事件同文件）。
- **`in_map` 过早**：`WorldGeneration.timeSinceFinishedGeneration` 的初始占位值是 `1000`，`>1.5` 会立刻成立。已改为要求 `2 < 值 < 999`，并把“进世界兜底”从 4 秒延长到 8 秒；优先等 `biomeTitle` 窗口出现。
- **进图闷感**：进入世界前 3 秒给 0.80 → 1.00 的 duck 过渡（贴合原版“从闷到通透”的进入感）。
- **代码审查修复**：`AudioEngine.Play` 的 resume 分支补 `Source.isPlaying` 判断（已播完的 voice 会被误当“正在播”而静音）；`BoxLibrary.ResolveFile` 增加解析缓存（原先每帧 `File.Exists` + `Directory.GetFiles`，被 `MusicManager.Update` 反复调用时是磁盘 IO 热点）；高频日志改为节流且“已在播”不重复打印；`Mp3Active` 改为带 20 分钟上限的 `Mp3Busy`。
- **MP3 播放器不再压制战斗音乐**：`Mp3Suppressible` 收窄为仅 `in_map` / `waiting_map`，避免 `Mp3Active` 残留导致战斗/濒死永久无声。

## 参考与许可证

见 `NOTICE.md`。本模组为原创实现（MIT），运行时依赖 BepInEx 5、HarmonyX、CUCoreLib（不打包其代码）。