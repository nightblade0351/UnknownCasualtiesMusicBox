using System;

namespace UCMB
{
    /// <summary>
    /// 事件枚举。顺序即优先级从低到高的语义分组（实际优先级见 EventDef.Priority）。
    /// </summary>
    public enum MusicEvent
    {
        Hall = 0,
        ChoosePart = 1,
        WaitingMap = 2,
        InMap = 3,
        AttackHurt = 4,
        Articulos = 5,
        Die = 6,
        Result1 = 7,
        Result2 = 8,
        Result3 = 9,
        Suspend = 10
    }

    /// <summary>
    /// 单个事件的音频映射：文件名基名、循环规则、优先级。
    /// 音乐盒是**全局唯一**的一个选择（一个文件夹同时提供全部事件），
    /// 因此这里不再有“每事件选歌”的概念。
    /// </summary>
    public sealed class EventDef
    {
        public readonly MusicEvent Id;
        public readonly string Key;      // 日志/配置用短键
        public readonly string Display;  // 设置项显示名（原版设置项标题）
        public readonly string Desc;     // 设置项说明（原版 tooltip）
        public readonly string[] Files;  // 事件文件名（无后缀），第一个为主名
        public readonly bool Loop;       // 是否循环
        public readonly int Priority;    // 优先级（越大越高）
        public readonly bool OneShot;    // 触发式（非持续状态轮询）

        public EventDef(MusicEvent id, string key, string display, string desc,
            string[] files, bool loop, int priority, bool oneShot)
        {
            Id = id;
            Key = key;
            Display = display;
            Desc = desc;
            Files = files;
            Loop = loop;
            Priority = priority;
            OneShot = oneShot;
        }

        public static readonly EventDef[] All = new EventDef[]
        {
            new EventDef(MusicEvent.Hall, "hall", "大厅音乐",
                "大厅 / 主菜单循环播放，对应 CS 音乐盒的“主菜单音乐”。",
                new string[] { "hall" }, true, 10, false),

            new EventDef(MusicEvent.ChoosePart, "choose_part", "角色选择界面音乐",
                "角色选择 / 队伍选择 / 交易界面循环播放。",
                new string[] { "choose_part" }, true, 20, false),

            new EventDef(MusicEvent.WaitingMap, "waiting_map", "加载地图界面音乐",
                "第一层读取 waiting_map_1（行动开始），第二层起依次尝试 waiting_map_2 / _3 / _4（回合开始），缺失则回退上一级；全部共用这一项设置与音量。不循环。",
                new string[] { "waiting_map_1", "waiting_map_2", "waiting_map_3", "waiting_map_4" },
                false, 30, true),

            new EventDef(MusicEvent.InMap, "in_map", "进入地图音乐",
                "加载完成进入地图时播放（比赛开始），不循环。",
                new string[] { "in_map" }, false, 31, true),

            new EventDef(MusicEvent.AttackHurt, "attack_hurt", "攻击或受伤音乐",
                "攻击怪物或自身受伤时循环；重复触发不重启，全部解除后 3 秒淡出。",
                new string[] { "attack_hurt" }, true, 50, false),

            new EventDef(MusicEvent.Articulos, "articulos", "濒死音乐",
                "多人游戏中他人到达底部、辐射线出现，或自身濒临死亡时循环（炸弹十秒倒计时语义）。",
                new string[] { "articulos" }, true, 60, false),

            new EventDef(MusicEvent.Suspend, "suspend", "暂停音乐",
                "角色暂停 / 比赛结束时循环（比赛结束语义）。",
                new string[] { "suspend" }, true, 90, false),

            new EventDef(MusicEvent.Die, "die", "死亡音乐",
                "死亡 / 死亡视角，不循环，播完即停（死亡视角音乐语义）。",
                new string[] { "die" }, false, 95, true),

            new EventDef(MusicEvent.Result1, "final_result_1", "结局1音乐",
                "达成结局 1，不循环（回合失败语义）。",
                new string[] { "final_result_1" }, false, 100, true),

            new EventDef(MusicEvent.Result2, "final_result_2", "结局2音乐",
                "达成结局 2，不循环（回合胜利语义）。",
                new string[] { "final_result_2" }, false, 100, true),

            new EventDef(MusicEvent.Result3, "final_result_3", "结局3/MVP音乐",
                "达成结局 3 或 MVP，不循环；存在 final_result_3_1 / _2 时随机取一段。",
                new string[] { "final_result_3" }, false, 100, true)
        };

        public static EventDef Get(MusicEvent id)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id == id) return All[i];
            }
            return null;
        }

        // ---- 原版音轨名（用于识别 MusicManager.PlaySong 的事件语义） ----
        public const string ClipFreedom = "freedom";
        public const string ClipEndLabDrone = "endlabdrone";
        public const string ClipBiopod = "biopod";
        public const string ClipWhatHaveIDone = "whathaveidone";
        public const string ClipThornbackDefeat = "thornbackDefeat";
        public const string ClipTutorial = "THETUTORIAL";
    }
}