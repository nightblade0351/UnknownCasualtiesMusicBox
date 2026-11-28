using System;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace UCMB
{
    /// <summary>
    /// 全部 Harmony 补丁集中管理：每个补丁单独 try/catch，失败不影响其它补丁与游戏。
    /// 只跳过“音频播放”本身，不改变游戏逻辑。
    /// </summary>
    internal static class Patches
    {
        internal static void ApplyAll(Harmony harmony)
        {
            // ---- 场景生命周期 ----
            Patch(harmony, typeof(WorldGeneration), "Awake", null, "World_Awake_Postfix");
            // ---- 每帧驱动（不依赖自建 MonoBehaviour 的 Update）----
            Patch(harmony, typeof(PreRunScript), "Update", null, "PreRun_Update_Postfix");
            Patch(harmony, typeof(PlayerCamera), "Update", null, "PlayerCamera_Update_Postfix");

            // ---- 加载界面 ----
            Patch(harmony, typeof(PreRunScript), "StartRun", null, "PreRun_StartRun_Postfix");
            Patch(harmony, typeof(WorldGeneration), "RegenerateWorld", null, "World_RegenerateWorld_Postfix");
            Patch(harmony, typeof(PreRunScript), "SelectCharacter", null, "PreRun_SelectCharacter_Postfix",
                new Type[] { typeof(int) });

            // ---- 音乐管理器 ----
            // 击杀长老：原版走 Sound.Play("music/thornbackDefeat")，不经过 MusicManager，
            // 只能在这里拦截（只认这个名字，其它音效完全放行）。
            Patch(harmony, typeof(Sound), "Play", "Sound_PlayThornback_Prefix", null,
                new Type[] { typeof(string), typeof(Vector2), typeof(bool), typeof(bool),
                             typeof(Transform), typeof(float), typeof(float), typeof(bool), typeof(bool) });
            Patch(harmony, typeof(MusicManager), "PlaySong", "Music_PlaySong_Prefix", null, new Type[] { typeof(AudioClip) });
            Patch(harmony, typeof(MusicManager), "PlayLayerSong", "Music_PlayLayerSong_Prefix", null);
            Patch(harmony, typeof(MusicManager), "StopSong", "Music_StopSong_Prefix", null);
            Patch(harmony, typeof(MusicManager), "Update", null, "Music_Update_Postfix");

            // ---- 受伤反馈 ----
            Patch(harmony, typeof(HearDamageSound), "Update", null, "HearDamage_Update_Postfix");

            // ---- MP3 播放器 ----
            Patch(harmony, typeof(MP3Menu), "Play", null, "Mp3_Play_Postfix");
            Patch(harmony, typeof(MP3Menu), "Exit", null, "Mp3_Exit_Postfix");

            // ---- 设置 ----
            // 音乐盒下拉：拦截展开，改用自建选择面板（TMP 的克隆列表布局不可控）
            Patch(harmony, typeof(TMP_Dropdown), "Show", "Dropdown_Show_Prefix", null);
            Patch(harmony, typeof(Settings), "DefaultSettings", null, "Settings_DefaultSettings_Postfix");
            Patch(harmony, typeof(Settings), "ApplyAll", "Settings_ApplyAll_Prefix", "Settings_ApplyAll_Postfix");
            Patch(harmony, typeof(Settings), "LoadSettings", "Settings_Load_Prefix", "Settings_Load_Postfix");
            Patch(harmony, typeof(SettingsMenu), "ResetToDefault", null, "SettingsMenu_Reset_Postfix");
            Patch(harmony, typeof(SettingsMenu), "SelectTab", null, "SettingsMenu_SelectTab_Postfix",
                new Type[] { typeof(Setting.SettingCategory) });
            Patch(harmony, typeof(SettingsMenu), "Close", "SettingsMenu_Close_Prefix", null);
        }

        private static void Patch(Harmony harmony, Type type, string method, string prefix, string postfix, Type[] args = null)
        {
            try
            {
                MethodInfo target = args == null ? AccessTools.Method(type, method) : AccessTools.Method(type, method, args);
                if (target == null)
                {
                    Log.Warn("未找到目标方法 " + type.Name + "." + method + "，跳过该补丁。");
                    return;
                }
                HarmonyMethod pre = null;
                HarmonyMethod post = null;
                if (prefix != null) pre = new HarmonyMethod(AccessTools.Method(typeof(Patches), prefix));
                if (postfix != null) post = new HarmonyMethod(AccessTools.Method(typeof(Patches), postfix));
                if (pre == null && post == null) return;
                harmony.Patch(target, pre, post);
                Log.Info("已应用补丁 " + type.Name + "." + method);
            }
            catch (Exception ex)
            {
                Log.Error("补丁失败 " + type.Name + "." + method + "（已跳过，可能回退到音量压制模式）：", ex);
            }
        }

        private static MusicBoxPlugin P
        {
            get { return MusicBoxPlugin.Instance; }
        }

        // ---------------- 场景 ----------------
        private static void PreRun_Update_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null) p.TickFrom("PreRunScript.Update");
            }
            catch (Exception ex) { Log.Error("PreRunScript.Update 后处理异常：", ex); }
        }

        private static void PlayerCamera_Update_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null)
                {
                    p.TickFrom("PlayerCamera.Update");
                    // 某些音源音量由 PlayerCamera.Update 设置，晚于 MusicManager.Update，
                    // 这里再压一次，确保暂停期间不会被它们覆盖回原音量。
                    p.MutePauseSources();
                }
            }
            catch (Exception ex) { Log.Error("PlayerCamera.Update 后处理异常：", ex); }
        }

        private static void World_Awake_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null) p.ClearInMapTaken();
            }
            catch (Exception ex) { Log.Error("WorldGeneration.Awake 后处理异常：", ex); }
        }

        // ---------------- 加载界面 ----------------
        private static void PreRun_StartRun_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                p.ForceRetrigger = false;
                p.FirstLoadingPlayed = true;
                // 这里**不再**播加载音乐：
                // 联机模组未开主机时 StartRun 也会被调用（然后被拦下），
                // 只要在这条路径上安排播放，就会出现“没进去却响了”。
                // 现在统一由“加载界面出现”的边沿触发（Plugin.TrackLoadingEdge）。
            }
            catch (Exception ex) { Log.Error("StartRun 后处理异常：", ex); }
        }

        // 点击角色（进入/操作角色选择界面）时立刻切到角色选择音乐
        private static void PreRun_SelectCharacter_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null) p.KickIfNotPlaying(MusicEvent.ChoosePart, "选择角色（界面点击）");
            }
            catch (Exception ex) { Log.Error("SelectCharacter 后处理异常：", ex); }
        }

        private static void World_RegenerateWorld_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                // 不能再用 biomeDepth 判断第几层：RegenerateWorld 后置补丁执行时
                // biomeDepth 往往还是上一层的值（这正是“第二层不播”的原因）。
                // 改为按“本局第几次生成世界”计数。
                p.RegenCount++;
                int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : -1;

                // 指令跳层会重建 WorldGeneration，RegenCount 会被清零；
                // 此时必须靠 biomeDepth 判断，否则深处也会播“第一层/行动开始”那首。
                bool shallow = depth <= 1;
                if (shallow)
                {
                    // 第一层：StartRun 已经播过就跳过（否则会出现两次）；
                    // 读档 / 直接进图这类没走 StartRun 的情况，这里补播第一层音乐。
                    if (p.FirstLoadingPlayed)
                    {
                        Log.Info("RegenerateWorld：第 1 层，加载音乐已由 StartRun 播放，跳过"
                            + "（biomeDepth=" + depth + "）");
                        return;
                    }
                    p.FirstLoadingPlayed = true;
                    p.PlayEvent(MusicEvent.WaitingMap, 0, "第 1 层加载（未经过 StartRun，biomeDepth=" + depth + "）");
                    return;
                }

                if (!p.ShouldSkipWaiting(depth))
                {
                    // 第 2 层及以后：由 PlayEvent 在 waiting_map_2…12 里随机取一首
                    p.PlayEvent(MusicEvent.WaitingMap, -1,
                        "第 2 层及以后（biomeDepth=" + depth + "，第 " + p.RegenCount + " 次生成）");
                }
                else
                {
                    Log.Info("RegenerateWorld：本层加载音乐刚播过，跳过（biomeDepth=" + depth + "）");
                }
            }
            catch (Exception ex) { Log.Error("RegenerateWorld 后处理异常：", ex); }
        }

        // ---------------- 音乐管理器 ----------------
        /// <summary>
        /// 击杀长老（ElderThornbackBehaviour.OnDestroy 里的 Sound.Play("music/thornbackDefeat")）。
        /// 只精确匹配这一个音效名，其余枪声/脚步等一律放行。
        /// </summary>
        private static bool Sound_PlayThornback_Prefix(string __0)
        {
            try
            {
                if (__0 != "music/thornbackDefeat") return true;
                MusicBoxPlugin p = P;
                if (p == null) return true;
                if (string.IsNullOrEmpty(p.MusicBox)) return true;   // 游戏默认：保持原版
                if (p.PlayEvent(MusicEvent.Result2, 0, "击杀长老（thornbackDefeat）"))
                {
                    p.FadeOutVanillaMusic();
                    Log.Info("击杀长老：跳过原版 thornbackDefeat，改用音乐盒的 final_result_2");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Sound.Play 前处理异常（放行原版）：", ex);
                return true;
            }
        }

        private static bool Music_PlaySong_Prefix(AudioClip song)
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return true;
                if (p.Mp3Busy) return true;                 // MP3 播放器优先，不干预
                string name = song != null ? song.name : "?";
                MusicEvent? ev = MusicBoxPlugin.EventForClip(song);
                if (!ev.HasValue)
                {
                    if (string.IsNullOrEmpty(p.MusicBox))
                    {
                        Log.Throttled("unmapped_" + name, 10f, "原版音轨未映射（放行原版）：" + name);
                        return true;   // 选“游戏默认”，不干预
                    }
                    // 选了音乐盒：只有过场/结局白名单放行，其余一律压制，
                    // 避免“猩红之地”这类场景漏出原版音乐。
                    if (MusicBoxPlugin.IsCutsceneClip(name))
                    {
                        Log.Throttled("unmapped_" + name, 10f, "过场音轨放行：" + name);
                        return true;
                    }
                    Log.Throttled("blocked_" + name, 10f, "压制未映射原版音乐：" + name);
                    return false;
                }
                // 本层已经播过“进入地图”音乐：不要再从 PlaySong 这条路径播第二遍
                if (ev.Value == MusicEvent.InMap && p.IsInMapTaken(MusicBoxPlugin.CurrentDepth()))
                {
                    Log.Throttled("inmap_taken", 5f, "原版音轨 " + name + " -> in_map 本层已接管，压制原版");
                    return false;
                }
                if (p.PlayEvent(ev.Value, -1, "原版音轨 " + name))
                {
                    p.FadeOutVanillaMusic();   // 原版不硬停，3 秒淡出，避免叠加与硬切
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("PlaySong 前处理异常（放行原版）：", ex);
                return true;
            }
        }

        private static bool Music_PlayLayerSong_Prefix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return true;
                if (string.IsNullOrEmpty(p.MusicBox)) return true;      // 游戏默认
                if (p.Mp3Busy) return true;

                // 世界还在生成时不播：此时“第一层窗口”还没出现，
                // 提前播会让 in_map 在加载画面上就响。压制原版，交给状态机在窗口出现后播。
                if (WorldGeneration.world != null && WorldGeneration.world.generatingWorld)
                {
                    Log.Throttled("layer_generating", 5f, "PlayLayerSong：世界生成中，暂不播放进入地图音乐");
                    return false;
                }

                int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0;
                if (!p.IsInMapTaken(depth))
                {
                    if (p.PlayEvent(MusicEvent.InMap, 0, "进入地图（biomeDepth=" + depth + "）"))
                    {
                        p.MarkInMapTaken(depth);
                        p.FadeOutVanillaMusic();
                    }
                    else
                    {
                        // 播放失败（缺文件/加载失败）也不能回落到原版层 BGM ——
                        // 否则“音乐曲目中场时间”一到，原版就会在中途插一首歌。
                        Log.Throttled("inmap_fail", 10f,
                            "in_map 播放失败，本层保持静音（已选择音乐盒，不回落原版层 BGM）");
                    }
                }
                // 只要选了音乐盒，原版层 BGM 一律压制（含中场间隔触发的那一首）
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("PlayLayerSong 前处理异常（放行原版）：", ex);
                return true;
            }
        }

        private static bool Music_StopSong_Prefix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null || p.Engine == null) return true;
                if (p.ShouldStopWithVanilla())
                {
                    p.Engine.FadeOutAll(null);   // 循环音乐：跟随原版一起淡出
                }
                else
                {
                    Log.Info("原版 StopSong：当前是一次性音乐，保持播放（避免刚进图就被打断）");
                }
            }
            catch (Exception ex) { Log.Error("StopSong 前处理异常：", ex); }
            return true;
        }

        private static void Music_Update_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                AudioSource src = MusicBoxPlugin.VanillaMusicSource();
                if (src == null) return;
                if (p.VanillaFading)
                {
                    float m = p.StepVanillaFade();
                    if (m <= 0f) { try { src.Stop(); } catch { } }
                    else if (src.isPlaying) src.volume = src.volume * m;
                }
                if (p.ShouldMuteVanilla()) src.volume = 0f;
                // 暂停时只留 suspend：原版主音源直接压到 0（暂停界面盖住全屏，不需要残留 BGM）
                if (p.IsSuspendMusicPlaying())
                {
                    src.volume = 0f;
                    p.MuteLooseVanillaSources();   // caveAudio / underWaterSource / genPodSource
                    p.MutePauseSources();          // 其它不在固定字段上的长音源
                }
                // 兜底：选音乐盒时，原版“循环层 BGM”一律淡出停止。
                // 第 10/11 层等特殊场景不走 PlayLayerSong，原版可能残留一首循环 BGM，
                // 这里按 loop 判定（过场音乐通常 loop=false，不会被误伤）。
                // 注意：只在“尚未开始淡出”时发起一次，否则每帧重置计时器会导致原版永远淡不完
                if (src.isPlaying && src.loop && p.Engine != null && p.Engine.VoiceCount > 0
                    && !p.VanillaFading)
                    p.FadeOutVanillaMusic();
                // 原版音乐（战斗/交易主题）走 drone 音轨，不在主 source 上：
                // 这里把它们压掉，其余环境 drone 只做限幅（保留压低的环境效果）。
                p.DuckVanillaDrones();
            }
            catch (Exception ex) { Log.Error("MusicManager.Update 后处理异常：", ex); }
        }

        // ---------------- 受伤 ----------------
        private static FieldInfo _hearDamageSourceField;
        private static bool _hearDamageResolved;
        private static bool _hearDamageWasPlaying;

        private static void HearDamage_Update_Postfix(HearDamageSound __instance)
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null || __instance == null) return;
                if (!_hearDamageResolved)
                {
                    _hearDamageResolved = true;
                    _hearDamageSourceField = AccessTools.Field(typeof(HearDamageSound), "source");
                }
                if (_hearDamageSourceField == null) return;
                AudioSource src = _hearDamageSourceField.GetValue(__instance) as AudioSource;
                if (src == null) return;
                bool playing = src.isPlaying;
                if (playing && !_hearDamageWasPlaying) p.LastDamageTime = Time.unscaledTime;
                _hearDamageWasPlaying = playing;
            }
            catch (Exception ex) { Log.Error("受伤音后处理异常：", ex); }
        }

        // ---------------- MP3 播放器 ----------------
        private static void Mp3_Play_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null) p.MarkMp3Active(true);
            }
            catch (Exception ex) { Log.Error("MP3 Play 后处理异常：", ex); }
        }

        private static void Mp3_Exit_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null)
                {
                    p.MarkMp3Active(false);
                    p.ForceRetrigger = true;   // 退出播放器后立即恢复场景音乐
                }
            }
            catch (Exception ex) { Log.Error("MP3 Exit 后处理异常：", ex); }
        }

        // ---------------- 设置 ----------------
        /// <summary>
        /// 音乐盒下拉：拦截 TMP 自带的展开，改为弹出自建选择面板。
        /// 只对挂了 <see cref="MusicBoxDropdownTag"/> 的下拉生效，语言选择等原版下拉不受影响。
        /// </summary>
        private static bool Dropdown_Show_Prefix(TMP_Dropdown __instance)
        {
            try
            {
                if (__instance == null) return true;
                if (__instance.GetComponent<MusicBoxDropdownTag>() == null) return true;
                MusicBoxSelector.Open(__instance);
                return false;   // 不再展开 TMP 的原生列表
            }
            catch (Exception ex)
            {
                Log.Error("音乐盒选择面板打开失败（回退原生下拉）：", ex);
                return true;
            }
        }

        private static void Settings_Load_Prefix()
        {
            // Settings.LoadSettings 会重建 settings 列表，缓存的 Setting 引用随之失效
            MusicBoxPlugin.InLoadSettings = true;
            try { SettingsUi.InvalidateLive(); }
            catch (Exception ex) { Log.Error("设置加载前处理异常：", ex); }
        }

        // 让本模组的设置项随 DefaultSettings 一起生成，LoadSettings / ResetToDefault 都会自动包含
        private static void Settings_DefaultSettings_Postfix(System.Collections.Generic.List<Setting> __result)
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null || __result == null) return;
                SettingsUi.AppendTo(__result, p);
            }
            catch (Exception ex) { Log.Error("DefaultSettings 后处理异常：", ex); }
        }

        // 批量 Apply（启动加载 / 重置为默认）不是用户操作，期间忽略本模组的 apply 回调
        private static void Settings_ApplyAll_Prefix()
        {
            MusicBoxPlugin.NoteApplyAllStart();
        }

        private static void Settings_ApplyAll_Postfix()
        {
            MusicBoxPlugin.InApplyAll = false;
        }

        private static void Settings_Load_Postfix()
        {
            MusicBoxPlugin.InLoadSettings = false;
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                // LoadSettings 会用 DefaultSettings() 重建列表，必须重新注入本模组的设置项
                SettingsUi.EnsureInjected(p);
                SettingsUi.ReconcileFromCfg(p);
            }
            catch (Exception ex) { Log.Error("设置加载后注入/对账异常：", ex); }
        }

        // “重置为默认”之后，把新生成的默认值写回本模组配置，避免下次启动又被旧配置覆盖
        private static void SettingsMenu_Reset_Postfix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                SettingsUi.InvalidateLive();
                SettingsUi.EnsureInjected(p);
                // 重置后 Setting 已经是默认值，直接把默认值写回 cfg（不可再拿旧 cfg 反向覆盖）
                p.SyncFromSettings();
                p.ForceRetrigger = true;
                Log.Info("已重置音乐盒设置为默认（游戏默认 + 全部音量 100%）。");
            }
            catch (Exception ex) { Log.Error("重置默认值后同步异常：", ex); }
        }

        private static void SettingsMenu_SelectTab_Postfix(SettingsMenu __instance, Setting.SettingCategory __0)
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p == null) return;
                SettingsUi.SyncOpenMenu(p, __instance, __0);
                if ((int)__0 == (int)Setting.SettingCategory.Audio)
                    Log.Info("[UI] 已切到音频页，音乐盒当前=" + (p.MusicBox ?? "游戏默认"));
#if SELFTEST
                // 布局自检只在开发版启用，正式版不往日志里刷几十行
                if ((int)__0 == (int)Setting.SettingCategory.Audio) Diag.Request();
#endif
            }
            catch (Exception ex) { Log.Error("设置页后处理异常：", ex); }
        }

        private static void SettingsMenu_Close_Prefix()
        {
            try
            {
                MusicBoxPlugin p = P;
                if (p != null) p.FlushSettingsFromUi();
                SettingsUi.ResetScrollTop();
            }
            catch (Exception ex) { Log.Error("关闭设置页前同步异常：", ex); }
        }
    }
}