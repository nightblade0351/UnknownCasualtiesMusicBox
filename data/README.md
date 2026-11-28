# data 目录说明

本目录用于存放「音乐盒」文件夹，每个子文件夹就是一个音乐盒，文件夹名就是设置列表里显示的名字。

```
data\
└─ 我的音乐盒\
   ├─ hall.mp3
   ├─ choose_part.mp3
   ├─ waiting_map_1.mp3
   ├─ waiting_map_2.mp3
   ├─ in_map.mp3
   ├─ attack_hurt.mp3
   ├─ articulos.mp3
   ├─ suspend.mp3
   ├─ die.mp3
   ├─ final_result_1.mp3
   ├─ final_result_2.mp3
   └─ final_result_3.mp3
```

- 文件夹名 = 游戏设置里显示的音乐盒名，改文件夹名就是改选项名。
- 支持 `.mp3` / `.wav` / `.ogg`，**不支持 `.flac`**。
- 加载地图音乐按槽位编号：`waiting_map_1` 给第一层，`waiting_map_2` … `waiting_map_12` 是第二层及以后的随机池，**不要再写 `waiting_map_1_1` 这种双数字名**。
- 其他事件要多首随机时才用 `_2` / `_3` 后缀，例如 `final_result_3_1`、`attack_hurt_2`。
- 完整命名规则见 `docs/音乐盒命名规则.md`（发布包里叫「音乐盒命名规则.md」）。

> 本仓库**不附带任何音乐盒音频**（第三方音乐版权归各权利人所有）。
> 请自备音频后按上面的结构放入本目录。
