# v1.3.0

> **此内容（包含代码本体）含 AI 生成。**
> This content (including the source code itself) contains AI-generated material.

《未知伤亡（Casualties Unknown）》的 CS 风格音乐盒模组：**一个音乐盒文件夹**同时提供全部事件音乐，按游戏事件自动切换。

## 功能

- **12 项设置，全部并入原版「音频」页**：`音乐盒`（点开弹出选择面板）+ 11 个按事件的音量滑条（0–100%，1% 步进，0% 静音但保留选择）。
- **选歌以文件夹名为准**：`data\<音乐盒名>\` 即一个音乐盒，不需要改音频元数据。
- **事件覆盖**：大厅 / 角色选择 / 交易 / 加载地图（按层）/ 进入地图 / 攻击受伤 / 濒死辐射线 / 暂停 / 死亡 / 结局 1·2·3（MVP）。
- **切换即时生效**：改动即生效，无需"应用"按钮，无需重启。
- **1.5 秒淡入 / 1.5 秒淡出**：切换时新旧音乐交叉过渡，不硬切。
- **压制原版音乐**：层 BGM、战斗主题、交易主题、地震氛围音轨；环境音保留。
- **对话/交易闷音**：跟随原版 `lowCutoff` 同步低通滤波，音乐变闷但不对消失。
- **击杀长老**：接管原版 `thornbackDefeat`，改用 `final_result_2`。

## 安装

1. 解压 `UnknownCasualtiesMusicBox_v1.3.0_noaudio.zip`，把 `BepInEx` 文件夹拖到游戏根目录（与 `winhttp.dll` 同级）覆盖。
2. **自备音频**：本包不含音乐，按 `data/<音乐盒名>/` 放入 12 个事件文件（见包内「音乐盒命名规则.md」）。
3. 游戏内：设置 → 音频 → 音乐盒。

## 文件命名

```
data\<音乐盒名>\
    hall.mp3              choose_part.mp3
    waiting_map_1.mp3     waiting_map_2.mp3
    in_map.mp3            attack_hurt.mp3
    articulos.mp3         suspend.mp3
    die.mp3               final_result_1.mp3
    final_result_2.mp3    final_result_3.mp3
```

支持 `mp3` / `wav` / `ogg`（**不支持 flac**）；多分段用 `_1`、`_2` 后缀，播放时随机取一段。

## 说明

- 本 Release **不附带任何第三方音乐盒音频**，音频版权归各自权利人所有。
- 依赖 BepInEx 5.4.23.5+、CUCoreLib。
- 联机时音乐为纯本地表现，不发送任何网络包，客机无需安装。
- 已知限制：不支持 flac；`surfacebreach` 等少数过场音仍为原版；第 11 层部分原版音轨可能未被完全接管。

## 致谢

思路参考 Ultimate Sound Swap、Survival Cues、ScavSetLib、CUCoreLib 等社区模组，未使用其代码。