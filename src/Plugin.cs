using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace UCMB
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(CUCoreGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class MusicBoxPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.everywhere.unknowncasualties.musicbox";
        public const string PluginName = "UnknownCasualtiesMusicBox";
        public const string PluginVersion = "1.3.1";
        public const string CUCoreGuid = "net.cucorelib";

        internal const string ModIdPrefix = "ucmb.";
        internal const string DataFolderName = PluginName;

        internal static MusicBoxPlugin Instance;

        private ConfigFile _cfg;
        private ConfigEntry<string> _cfgBox;
        private readonly Dictionary<MusicEvent, ConfigEntry<int>> _cfgVolume =
            new Dictionary<MusicEvent, ConfigEntry<int>>();

        internal BoxLibrary Library;
        internal AudioEngine Engine;
        internal MusicBoxHost Host;

        private Harmony _harmony;

        /// <summary>设置变更后置位，下一帧强制重播当前场景音乐（即时生效，无需重启）。</summary>
        internal bool ForceRetrigger;

        /// <summary>Settings.ApplyAll 执行期间为 true：批量应用并不代表用户操作，不应回写配置。</summary>
        internal static bool InApplyAll;

        /// <summary>Settings.LoadSettings 执行期间为 true：加载期间的值来自磁盘，不是用户操作。</summary>
        internal static bool InLoadSettings;

        // ---- 运行时值 ----
        private string _box;
        private readonly Dictionary<MusicEvent, int> _volumes = new Dictionary<MusicEvent, int>();

        private MusicEvent? _currentEvent;
        private bool _currentIsOneShot;

        // ---- 状态去抖 ----
        private MusicEvent? _stableDesired;
        private MusicEvent? _desiredCandidate;
        private float _desiredCandidateSince;

        /// <summary>
        /// 把原始判定结果稳定化：新状态需要持续一小段时间才被采纳；
        /// 退出（回到 null）需要更久，避免“淡出还没结束又重新播放”。
        /// </summary>
        private MusicEvent? Stabilize(MusicEvent? raw)
        {
            if (raw == _stableDesired)
            {
                _desiredCandidate = null;
                return _stableDesired;
            }
            if (raw != _desiredCandidate)
            {
                _desiredCandidate = raw;
                _desiredCandidateSince = Time.unscaledTime;
                return _stableDesired;
            }
            // 进入：0.35 秒；退出：1.2 秒（比 3 秒淡出短，但足以滤掉单帧抖动）
            float need = raw.HasValue ? 0.25f : 1.2f;
            if (Time.unscaledTime - _desiredCandidateSince < need) return _stableDesired;
            _stableDesired = raw;
            _desiredCandidate = null;
            return _stableDesired;
        }

        private float _nextVolumeRefresh;
        private float _nextSettingsPoll;
        private float _inApplyAllSince;
        private float _lastUiApplyAt = -999f;
        private MusicEvent _lastOneShotEvent;
        private string _lastOneShotFile;
        private float _lastOneShotAt = -999f;

        internal static void NoteApplyAllStart()
        {
            InApplyAll = true;
            MusicBoxPlugin p = Instance;
            if (p != null) p._inApplyAllSince = Time.unscaledTime;
        }
        /// <summary>设置系统对账完成的时刻；此前不做 UI 轮询，避免用未对账的旧值覆盖 cfg。</summary>
        private float _settingsSyncedAt = float.MaxValue;

        internal void MarkSettingsSynced()
        {
            _settingsSyncedAt = Time.unscaledTime + 0.3f;
        }

        private bool _applyingSettings;

        /// <summary>关闭设置页前做最后一次 UI → 配置同步，确保快速操作不丢失。</summary>
        internal void FlushSettingsFromUi()
        {
            if (_applyingSettings) return;
            // 刚通过控件操作生效过（apply 已经写进运行时与配置），不要再用可能过期的实例覆盖
            if (Time.unscaledTime - _lastUiApplyAt < 1.5f) return;
            _applyingSettings = true;
            try
            {
                // 只用“被 UI 绑定的那一组实例”，避免 Find 到另一份幽灵实例后把值改回去
                SettingDropdown sd = SettingsUi.LiveBox;
                if (sd != null && Library != null && sd.choices != null && sd.choices.Length > 0)
                {
                    string wanted = Library.BoxAtChoiceIndex(Mathf.Clamp(sd.value, 0, sd.choices.Length - 1));
                    if (!string.Equals(wanted, _box, StringComparison.Ordinal))
                    {
                        Log.Info("[UI] 关闭设置页时补应用：音乐盒 " + (wanted ?? "游戏默认"));
                        SetMusicBox(wanted, true);
                    }
                }
                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    MusicEvent ev = EventDef.All[i].Id;
                    SettingFloat sf = SettingsUi.LiveVolume(ev);
                    if (sf == null) continue;
                    int ui = ClampVolume(Mathf.RoundToInt(sf.value));
                    if (ui != VolumeOf(ev)) SetVolumePercent(ev, ui, true);
                }
            }
            catch (Exception ex) { Log.Error("保存设置页改动失败：", ex); }
            finally { _applyingSettings = false; }
        }

        // ---------------------------------------------------------------
        // 设置控件即时生效（由 Setting.apply 回调驱动，等价于原版设置项的响应路径）
        // ---------------------------------------------------------------
        /// <summary>
        /// 用户点选音乐盒。uiIndex 来自**被点击的那个 SettingDropdown 自身**，
        /// 不再回头 Find 列表（列表可能在 LoadSettings 后被替换，Find 会拿到另一份实例）。
        /// </summary>
        internal void ApplyBoxFromUi(int uiIndex)
        {
            if (_applyingSettings) return;
            if (!UserInteractionAllowed("音乐盒")) return;
            _lastUiApplyAt = Time.unscaledTime;
            _applyingSettings = true;
            try
            {
                if (Library == null)
                {
                    Log.Warn("[UI] 应用音乐盒失败：Library 未初始化");
                    return;
                }
                if (uiIndex < 0 || uiIndex > Library.Boxes.Count)
                {
                    Log.Warn("[UI] 音乐盒下拉索引越界：" + uiIndex + "（共 " + (Library.Boxes.Count + 1) + " 项），已忽略");
                    return;
                }
                string wanted = Library.BoxAtChoiceIndex(uiIndex);
                Log.Info("[UI] 下拉索引 " + uiIndex + " -> " + (wanted ?? "游戏默认")
                    + "（切换前=" + (_box ?? "游戏默认") + "）");
                SetMusicBox(wanted, true);
                Log.Info("[UI] 切换后运行时=" + (_box ?? "游戏默认"));
            }
            catch (Exception ex) { Log.Error("应用音乐盒选择失败：", ex); }
            finally { _applyingSettings = false; }
        }

        internal void ApplyVolumeFromUi(MusicEvent ev, float uiValue)
        {
            if (_applyingSettings) return;
            if (!UserInteractionAllowed("音量")) return;
            _lastUiApplyAt = Time.unscaledTime;
            _applyingSettings = true;
            try
            {
                int pct = ClampVolume(Mathf.RoundToInt(uiValue));
                SetVolumePercent(ev, pct, true);
                Log.Info("[UI] 音量 " + ev + " -> " + pct + "%（运行时=" + VolumeOf(ev) + "%）");
            }
            catch (Exception ex) { Log.Error("应用事件音量失败：", ex); }
            finally { _applyingSettings = false; }
        }

        /// <summary>
        /// 判断这次 apply 是否真的来自用户操作：启动加载、批量应用、刚完成配置对账
        /// 这几个阶段里控件值并不代表用户选择，必须忽略，否则会出现音乐盒选择反复跳变。
        /// </summary>
        private bool UserInteractionAllowed(string what)
        {
            if (InApplyAll || InLoadSettings)
            {
                Log.Info("[UI] 忽略 " + what + " 变更：批量应用/加载中（InApplyAll="
                    + InApplyAll + " InLoadSettings=" + InLoadSettings + "）");
                return false;
            }
            if (SettingsMenu.instance == null)
            {
                Log.Info("[UI] 忽略 " + what + " 变更：设置页未打开");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 从 Setting 对象反向同步到运行值与配置（用于“重置为默认”等由游戏本体发起的批量变更）。
        /// </summary>
        internal void SyncFromSettings()
        {
            if (_applyingSettings) return;
            _applyingSettings = true;
            try
            {
                SettingDropdown sd = SettingsUi.FindBoxSetting();
                if (sd != null && Library != null)
                {
                    int idx = Mathf.Clamp(sd.value, 0, sd.choices.Length - 1);
                    SetMusicBox(Library.BoxAtChoiceIndex(idx), true);
                }
                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    MusicEvent ev = EventDef.All[i].Id;
                    SettingFloat sf = SettingsUi.FindVolumeSetting(ev);
                    if (sf == null) continue;
                    SetVolumePercent(ev, ClampVolume(Mathf.RoundToInt(sf.value)), true);
                }
            }
            catch (Exception ex) { Log.Error("从设置项反向同步失败：", ex); }
            finally { _applyingSettings = false; }
        }

        internal float LastDamageTime = -999f;
        internal bool Mp3Active;
        private float _mp3ActiveAt;

        internal void MarkMp3Active(bool active)
        {
            Mp3Active = active;
            if (active) _mp3ActiveAt = Time.unscaledTime;
        }

        /// <summary>
        /// MP3 播放器是否仍在占用。加 20 分钟上限：万一 Exit 没被调用（切场景、异常退出），
        /// 不能让它永久压制 in_map / waiting_map。
        /// </summary>
        internal bool Mp3Busy
        {
            get { return Mp3Active && Time.unscaledTime - _mp3ActiveAt < 1200f; }
        }
        internal bool PatchingFailed;
        internal bool FallbackMuteMode;

        // ---- 原版音乐淡出（接管时不能硬 Stop，否则会出现“原版与音乐盒叠在一起/没有淡出”） ----
        private float _vanillaFadeLeft;

        internal bool VanillaFading { get { return _vanillaFadeLeft > 0f; } }

        internal void FadeOutVanillaMusic()
        {
            _vanillaFadeLeft = AudioEngine.FadeSeconds;
        }

        /// <summary>返回当前淡出比例（1→0）。归零表示应当真正停止原版音源。</summary>
        internal float StepVanillaFade()
        {
            if (_vanillaFadeLeft <= 0f) return 0f;
            _vanillaFadeLeft -= Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            float m = Mathf.Clamp01(_vanillaFadeLeft / AudioEngine.FadeSeconds);
            if (_vanillaFadeLeft <= 0f) _vanillaFadeLeft = 0f;
            return m;
        }

        private float _painSpikeUntil = -999f;
        private float _lastPain = -1f;
        private float _lastPainCheck;

        private readonly Dictionary<int, bool> _inMapTaken = new Dictionary<int, bool>();
        private float _inMapFailBackoff = -999f;

        /// <summary>
        /// 由 BepInEx 的 Manager 组件驱动（比自建 MonoBehaviour 的 Update 可靠：
        /// 之前宿主对象被创建但 Update 从未被 Unity 调用，导致状态机完全不运行）。
        /// </summary>
        private int _lastTickFrame = -1;

        /// <summary>
        /// 每帧驱动。由游戏自身的 MonoBehaviour（PreRunScript.Update / PlayerCamera.Update）
        /// 的 Harmony 后置补丁调用，而不是依赖自建 MonoBehaviour 的 Update ——
        /// 实测在本环境里自建组件的 Update 不会被调用，会导致状态机完全不运行。
        /// </summary>
        internal void Tick()
        {
            if (_lastTickFrame == Time.frameCount) return;
            _lastTickFrame = Time.frameCount;
            try
            {
                if (!_loggedPluginTick)
                {
                    _loggedPluginTick = true;
                    Log.Info("[DIAG] 状态机已接管驱动（source=" + _tickSource + "，Engine=" + (Engine != null) + "）");
                }
                if (Engine != null) Engine.Tick(Time.unscaledDeltaTime);
                if (Engine != null) Engine.Duck = ComputeDuckFactor();
                if (Engine != null) Engine.CutoffCap = InWorld() ? ComputeVanillaCutoff() : 0f;
                Poll();
                SettingsUi.Tick();
                Diag.Tick();
                MusicBoxSelector.Tick();
#if SELFTEST
                SelfTest.Tick(this);
#endif
            }
            catch (Exception ex) { Log.Error("Tick 异常：", ex); }
        }

        private static bool _loggedPluginTick;
        private static string _tickSource = "?";

        internal void TickFrom(string source)
        {
            _tickSource = source;
            Tick();
        }

        private void Awake()
        {
            Instance = this;
            Log.Source = Logger;
            Log.Info("正在加载 " + PluginName + " v" + PluginVersion + " …");
            for (int i = 0; i < EventDef.All.Length; i++) _volumes[EventDef.All[i].Id] = 100;

            try
            {
                SetupConfig();
                string dataRoot = Path.Combine(Path.Combine(Paths.PluginPath, DataFolderName), "data");
                Library = new BoxLibrary(dataRoot);
                Library.Refresh();
                ValidateSavedBox();
            }
            catch (Exception ex)
            {
                Log.Error("初始化 data/配置失败：", ex);
                if (Library == null) Library = new BoxLibrary(Paths.PluginPath);
            }

            EnsureHost();

            try
            {
                SettingsUi.EnsureInjected(this);
                SettingsUi.ReconcileFromCfg(this);
            }
            catch (Exception ex) { Log.Error("注入设置项失败（音乐功能仍可用）：", ex); }

            try
            {
                _harmony = new Harmony(PluginGuid);
                Patches.ApplyAll(_harmony);
            }
            catch (Exception ex)
            {
                PatchingFailed = true;
                FallbackMuteMode = true;
                Log.Error("Harmony 补丁应用失败，已切换到“音量压制”回退模式：", ex);
            }

            Log.Info("加载完成。data 目录：" + (Library == null ? "(未初始化)" : Library.Root));
            Log.Info("启动状态：cfg 音乐盒=“" + (_cfgBox == null ? "?" : _cfgBox.Value) + "”"
                + "，运行时音乐盒=“" + (_box ?? "游戏默认") + "”"
                + "，扫描到 " + (Library == null ? 0 : Library.Boxes.Count) + " 个音乐盒");

#if SELFTEST
            try { SelfTest.Install(_harmony); }
            catch (Exception ex) { Log.Error("[SELFTEST] 安装自检钩子异常：", ex); }
#endif
        }

        // ===============================================================
        // 配置
        // ===============================================================
        private void SetupConfig()
        {
            _cfg = new ConfigFile(Path.Combine(Paths.ConfigPath, PluginGuid + ".cfg"), true);
            _cfgBox = _cfg.Bind("General", "MusicBox", string.Empty,
                "全局音乐盒：填写 data 下的音乐盒文件夹名称；留空表示“游戏默认”（不接管任何音乐）。");

            for (int i = 0; i < EventDef.All.Length; i++)
            {
                EventDef def = EventDef.All[i];
                _cfgVolume[def.Id] = _cfg.Bind("Volume", def.Key, 100,
                    "「" + def.Display + "」音量（0-100 整数百分比，1% 步进；0 表示静音但保留所选音乐盒）。");
                _volumes[def.Id] = ClampVolume(_cfgVolume[def.Id].Value);
            }

            MigrateLegacyConfig();
            _cfg.Save();
            _box = string.IsNullOrEmpty(_cfgBox.Value) || _cfgBox.Value == BoxLibrary.DefaultChoiceKey
                ? null : _cfgBox.Value;
        }

        /// <summary>清理 v1.0.0（每事件含 Selection）与 v1.1.0（单一 Volume）遗留键。</summary>
        private void MigrateLegacyConfig()
        {
            try
            {
                string migratedBox = null;
                bool migratedVolume = false;
                List<ConfigDefinition> dead = new List<ConfigDefinition>();

                foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> kv in _cfg)
                {
                    string section = kv.Key.Section;
                    string key = kv.Key.Key;
                    if (section == "General" && key == "Volume")
                    {
                        // v1.1.0 的全局音量 → 作为所有事件的初始音量
                        object v = kv.Value.BoxedValue;
                        if (v is int && (int)v != 100) migratedVolume = true;
                        dead.Add(kv.Key);
                        continue;
                    }
                    if (key != "Selection") continue;
                    string sel = kv.Value.BoxedValue as string;
                    if (migratedBox == null && !string.IsNullOrEmpty(sel)) migratedBox = sel;
                    dead.Add(kv.Key);
                }

                if (!string.IsNullOrEmpty(migratedBox) && string.IsNullOrEmpty(_cfgBox.Value))
                {
                    _cfgBox.Value = migratedBox;
                    Log.Info("已从旧版配置迁移音乐盒选择：" + migratedBox);
                }
                if (migratedVolume)
                {
                    for (int i = 0; i < EventDef.All.Length; i++) _volumes[EventDef.All[i].Id] = 100;
                    Log.Info("旧版全局音量已废弃，各事件音量重置为默认 100%。");
                }
                for (int i = 0; i < dead.Count; i++) _cfg.Remove(dead[i]);
                if (dead.Count > 0) Log.Info("已清理旧版配置项 " + dead.Count + " 条。");
            }
            catch (Exception ex)
            {
                Log.Warn("旧配置迁移失败（不影响使用）：" + ex.Message);
            }
        }

        private void ValidateSavedBox()
        {
            if (string.IsNullOrEmpty(_box)) return;
            if (Library == null) return;
            string real = Library.ResolveBoxName(_box);
            if (real != null)
            {
                if (!string.Equals(real, _box, StringComparison.Ordinal))
                {
                    Log.Info("已保存的音乐盒名称已按磁盘实际名称校正：“" + _box + "” -> “" + real + "”");
                    _box = real;
                    try { _cfgBox.Value = real; } catch { }
                }
                return;
            }
            // 注意：这里**不清空** _box。
            // 之前清空会导致“设置里仍显示音乐盒、实际却按游戏默认播放”的不一致；
            // 现在保留用户选择，播放时按事件逐个回退原版并打日志（LogMissingOnce 去重）。
            Log.Warn("已保存的音乐盒在当前 data 目录中不存在：“" + _box + "”。"
                + "本次运行会逐事件回退游戏默认；把文件夹放回 data 后无需任何操作即可恢复。");
        }

        internal static int ClampVolume(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }

        internal string MusicBox { get { return _box; } }

        internal int VolumeOf(MusicEvent ev)
        {
            int v;
            return _volumes.TryGetValue(ev, out v) ? v : 100;
        }

        internal void SetMusicBox(string boxName, bool writeCfg)
        {
            if (boxName != null && Library != null)
            {
                string real = Library.ResolveBoxName(boxName);
                if (real == null)
                {
                    Log.Warn("选择的音乐盒在 data 中不存在：“" + boxName + "”，已视为“游戏默认”。");
                    boxName = null;
                }
                else boxName = real;
            }
            string old = _box;
            bool same = string.Equals(old, boxName, StringComparison.Ordinal);
            _box = boxName;
            // 先置重播标志，再写配置：写盘异常不应该让“切换”失效
            ForceRetrigger = true;
            if (same)
                Log.Info("音乐盒未变化（" + (boxName == null ? "游戏默认" : boxName)
                    + "），按用户操作重播当前音乐 <- " + Caller());
            else
                Log.Info("音乐盒选择：" + (boxName == null ? "游戏默认" : boxName)
                    + "（原=" + (old ?? "游戏默认") + "） <- " + Caller());
            if (writeCfg)
            {
                try { _cfgBox.Value = boxName == null ? string.Empty : boxName; }
                catch (Exception ex) { Log.Warn("写入音乐盒配置失败（本次选择仍生效）：" + ex.Message); }
            }
            if (boxName == null) RestoreVanillaInWorld();

            // 立刻重播，不等下一帧轮询：用户点完马上就能听到结果
            LogBoxCoverage(boxName);
            ForceReplayNow("设置变更（立即重播）");
        }

        /// <summary>切换音乐盒时打印一次文件覆盖情况，缺哪个事件一眼可见。</summary>
        private void LogBoxCoverage(string boxName)
        {
            if (string.IsNullOrEmpty(boxName) || Library == null) return;
            try
            {
                List<string> missing = new List<string>();
                int ok = 0, total = 0;
                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    EventDef def = EventDef.All[i];
                    total++;
                    string why;
                    string path = Library.ResolveFile(boxName, def.Files[0], out why);
                    if (path != null) ok++;
                    else missing.Add(def.Files[0] + "(" + why + ")");
                }
                if (missing.Count == 0)
                    Log.Info("音乐盒“" + boxName + "”文件检查：" + ok + "/" + total + " 个事件可用");
                else
                    Log.Warn("音乐盒“" + boxName + "”文件检查：" + ok + "/" + total
                        + " 个事件可用；缺失：" + string.Join("、", missing.ToArray()));
            }
            catch (Exception ex) { Log.Warn("音乐盒文件检查失败：" + ex.Message); }
        }

        /// <summary>立刻按当前状态重播（切换音乐盒/音量后调用）。</summary>
        private MusicEvent? _pendingEvent;
        private int _pendingVariant;
        private float _pendingAt;
        private string _pendingReason;

        /// <summary>延迟触发事件：用于“按钮已按下但目标界面还没出现”的场合。</summary>
        internal void PlayEventDelayed(MusicEvent ev, int variant, float delay, string reason)
        {
            _pendingEvent = ev;
            _pendingVariant = variant;
            _pendingAt = Time.unscaledTime + Mathf.Max(0f, delay);
            _pendingReason = reason;
            Log.Info("已安排延迟播放 " + ev + "（" + delay.ToString("F2") + "s 后，" + reason + "）");
        }

        private void TickPendingEvent()
        {
            if (!_pendingEvent.HasValue) return;
            if (Time.unscaledTime < _pendingAt) return;
            MusicEvent ev = _pendingEvent.Value;
            int variant = _pendingVariant;
            string reason = _pendingReason;
            _pendingEvent = null;
            _pendingReason = null;
            PlayEvent(ev, variant, reason);
        }

        internal void ForceReplayNow(string reason)
        {
            try
            {
                if (Engine == null || Library == null) return;
                MusicEvent? target = ResolveDesired();
                if (!target.HasValue) target = _currentEvent;
                ForceRetrigger = false;
                if (!target.HasValue)
                {
                    // 当前没有任何事件在放（世界内且无战斗/濒死等）：只淡出旧声音
                    Engine.FadeOutAll(null);
                    _currentEvent = null;
                    return;
                }
                SwitchTo(target, reason);
            }
            catch (Exception ex) { Log.Error("立即重播失败：", ex); }
        }

        /// <summary>切回“游戏默认”时，让原版层 BGM 重新开始（否则要等到中场间隔结束）。</summary>
        private static void RestoreVanillaInWorld()
        {
            try
            {
                if (!InWorld()) return;
                MusicManager mm = MusicManager.main;
                if (mm == null) return;
                MethodInfo mi = AccessTools.Method(typeof(MusicManager), "PlayLayerSong");
                if (mi == null) return;
                mi.Invoke(mm, null);
            }
            catch (Exception ex) { Log.Warn("恢复原版层音乐失败（不影响游戏）：" + ex.Message); }
        }

        private static string Caller()
        {
            try
            {
                System.Diagnostics.StackTrace st = new System.Diagnostics.StackTrace();
                for (int i = 2; i < st.FrameCount && i < 6; i++)
                {
                    System.Diagnostics.StackFrame fr = st.GetFrame(i);
                    if (fr == null) continue;
                    System.Reflection.MethodBase m = fr.GetMethod();
                    if (m == null) continue;
                    if (m.DeclaringType == typeof(MusicBoxPlugin)) continue;
                    return (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;
                }
            }
            catch { }
            return "?";
        }

        internal void SetVolumePercent(MusicEvent ev, int percent, bool writeCfg)
        {
            percent = ClampVolume(percent);
            int old = VolumeOf(ev);
            _volumes[ev] = percent;
            if (writeCfg)
            {
                ConfigEntry<int> ce;
                if (_cfgVolume.TryGetValue(ev, out ce)) ce.Value = percent;
            }
            if (old == percent) return;
            if (Engine != null && _currentEvent.HasValue && _currentEvent.Value == ev)
                Engine.SetVolumeForTag(TagOf(ev), ComputeVolume(ev));
        }

        private float GameMusicVolume()
        {
            try
            {
                SettingFloat f = Settings.Get<SettingFloat>("musicvolume");
                if (f != null) return Mathf.Clamp01(f.value);
            }
            catch { }
            return 1f;
        }

        internal float ComputeVolume(MusicEvent ev)
        {
            float pct = VolumeOf(ev) / 100f;
            if (pct <= 0f) return 0f;
            return Mathf.Clamp01(pct * GameMusicVolume());
        }

        internal static string TagOf(MusicEvent ev)
        {
            EventDef d = EventDef.Get(ev);
            return "ucmb:" + (d == null ? ev.ToString() : d.Key);
        }

        // ===============================================================
        // 宿主 / 混音组
        // ===============================================================
        internal void EnsureHost()
        {
            try
            {
                if (Host != null && Engine != null) return;
                GameObject go = new GameObject("UC-MusicBox-Host");
                UnityEngine.Object.DontDestroyOnLoad(go);
                Host = go.AddComponent<MusicBoxHost>();
                Engine = new AudioEngine(go.transform);
                Host.Engine = Engine;
                Host.Plugin = this;
                Log.Info("音频宿主已创建（" + Host.name + "）");
            }
            catch (Exception ex)
            {
                Log.Error("创建音频宿主失败：", ex);
            }
        }

        internal void EnsureMixerGroup()
        {
            // 故意不把音乐盒挂到游戏的音乐混音组。
            //
            // 原版 mixer 的 "Volume" 参数 = PlayerCamera.muteTimeVolume
            //   = Clamp(-globalMuteTime * 15, -80, 0) dB
            // 交易、对话、过场都会把 globalMuteTime 抬高，从而把整条音乐总线压到 -80dB。
            // 一旦音乐盒也走这个组，就会在交易界面被一起压低甚至静音——这正是
            // “交易时音乐声音被压低”的原因。
            //
            // 音量本身已由 ComputeVolume() 乘上游戏「音乐音量」设置，不需要再依赖 mixer。
        }

        private static System.Reflection.FieldInfo _sourceField;

        internal static AudioSource VanillaMusicSource()
        {
            try
            {
                MusicManager mm = MusicManager.main;
                if (mm == null) return null;
                if (_sourceField == null) _sourceField = AccessTools.Field(typeof(MusicManager), "source");
                if (_sourceField == null) return null;
                return _sourceField.GetValue(mm) as AudioSource;
            }
            catch { return null; }
        }

        // ===============================================================
        // 播放
        // ===============================================================
        private static bool Mp3Suppressible(MusicEvent ev)
        {
            // 只让“加载 / 进入地图”这类场景音乐给 MP3 播放器让位。
            // 战斗、濒死、结局、死亡必须由音乐盒接管——否则玩家用过一次内置 MP3 播放器
            // （Mp3Active 没被复位）就会永远听不到战斗音乐。
            return ev == MusicEvent.InMap || ev == MusicEvent.WaitingMap;
        }

        /// <summary>
        /// 事件驱动的即时触发（例如点击角色进入选择界面）。用于在状态机轮询之外
        /// 立刻把音乐切到对应事件，避免“进界面没声音、点一下才有”的观感。
        /// </summary>
        internal void KickIfNotPlaying(MusicEvent ev, string reason)
        {
            try
            {
                EnsureHost();
                if (Library == null || Engine == null) return;
                if (string.IsNullOrEmpty(_box)) return;
                if (Engine.IsTagPlaying(TagOf(ev))) return;
                PlayEvent(ev, 0, reason);
            }
            catch (Exception ex) { Log.Warn("即时触发 " + ev + " 失败：" + ex.Message); }
        }

        internal bool PlayEvent(MusicEvent ev, int variant, string reason)
        {
            try
            {
                EnsureHost();
                if (Library == null || Engine == null) return false;
                if (string.IsNullOrEmpty(_box)) return false;
                if (Mp3Busy && Mp3Suppressible(ev))
                {
                    Log.Throttled("mp3_busy_" + ev, 5f, "MP3 播放器占用中，跳过事件 " + ev + "（" + reason + "）");
                    return false;
                }

                EventDef def = EventDef.Get(ev);
                if (def == null) return false;

                string fileName = def.Files[(variant >= 0 && variant < def.Files.Length) ? variant : 0];
                // 一次性音乐正在播放时，忽略重复触发。
                // 原版在结局分支里会反复 PlaySong(同一 clip)，仅靠 3 秒窗口不够，
                // 会表现为“结局音乐播完又从头上”——听感就是循环。
                if (def.OneShot && _currentEvent.HasValue && _currentEvent.Value == ev
                    && Engine != null && Engine.IsTagPlaying(TagOf(ev)))
                {
                    return true;   // 视为已接管，保持当前播放
                }
                // 一次性音乐的防重：同事件同文件在 3 秒内被再次触发时不再重播。
                // 例如第一层加载可能同时被 StartRun 与 RegenerateWorld 触发。
                if (def.OneShot && _lastOneShotEvent == ev && _lastOneShotFile == fileName
                    && Time.unscaledTime - _lastOneShotAt < 3f)
                {
                    Log.Throttled("dup_" + ev, 5f, "跳过重复的一次性音乐 " + ev + "（" + fileName + "），距上次 "
                        + (Time.unscaledTime - _lastOneShotAt).ToString("F2") + "s");
                    return true;   // 视为已接管，避免原版插进来
                }
                // 多文件事件（如 waiting_map_1..4）从当前层级往下回退：
                // 第 4 层缺 waiting_map_4 时用 _3，再缺用 _2，最后用 _1。
                string why = null;
                string path = null;
                int startVariant = (variant >= 0 && variant < def.Files.Length) ? variant : 0;
                for (int v = startVariant; v >= 0; v--)
                {
                    fileName = def.Files[v];
                    path = Library.ResolveFile(_box, fileName, out why);
                    if (path != null) break;
                }
                if (path == null) { Library.LogMissingOnce(_box, def.Files[0], why); return false; }
                AudioClip clip = Library.LoadClip(path);
                if (clip == null) return false;

                EnsureMixerGroup();
                float vol = ComputeVolume(ev);
                bool wasPlaying = Engine.IsTagPlaying(TagOf(ev));
                Engine.Play(clip, def.Loop, vol, TagOf(ev));
                _currentEvent = ev;
                _currentIsOneShot = def.OneShot;
                if (def.OneShot)
                {
                    _lastOneShotEvent = ev;
                    _lastOneShotFile = fileName;
                    _lastOneShotAt = Time.unscaledTime;
                }

                // 进入地图音乐：只要成功播过一次，就把本层标记为“已被本模组接管”，
                // 这样原版层 BGM 不会再插进来，同时避免同一次进图被 PlaySong / PlayLayerSong
                // 两条路径各播一遍（后者会把前者从头打断）。
                if (ev == MusicEvent.InMap) MarkInMapTaken(CurrentDepth());
                if (ev == MusicEvent.WaitingMap)
                {
                    _lastWaitingAt = Time.unscaledTime;
                    _lastWaitingDepth = CurrentDepth();
                    // 给加载音乐 3 秒独占期：它先响，3 秒后才允许 in_map 接管，
                    // 否则会出现“进入地图音乐先播、随后被加载音乐打断”。
                    _waitingGuardUntil = Time.unscaledTime + 3f;
                }
                if (ev == MusicEvent.Suspend) OnEnterSuspend();

                // 世界内接管：原版音乐 3 秒淡出后停止（不硬切），避免与原版层 BGM 叠加
                if (InWorld()) FadeOutVanillaMusic();

                // 菜单类事件接管后停掉原版菜单音乐，避免叠加
                if (ev == MusicEvent.Hall || ev == MusicEvent.ChoosePart)
                {
                    try
                    {
                        PreRunScript pr = PreRunScript.instance;
                        if (pr != null && pr.menuSource != null && pr.menuSource.isPlaying) pr.menuSource.Stop();
                    }
                    catch { }
                }

                // 每帧都可能被 MusicManager.Update 反复请求同一首（例如 dyingClip），
                // 已在播时不重复打印，避免日志被刷爆。
                if (!wasPlaying)
                    Log.Info("播放 " + ev + "（" + def.Display + "）-> " + Path.GetFileName(path)
                        + " 循环=" + def.Loop + " 音量=" + Mathf.RoundToInt(vol * 100f) + "% ["
                        + reason + "][" + VolumeOf(ev) + "%]");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("播放事件 " + ev + " 失败：", ex);
                return false;
            }
        }

        // ===============================================================
        // 设置值轮询（不依赖 CUCoreLib apply 回调，保证改动一定生效）
        // ===============================================================
        private void PollSettings()
        {
            if (Time.unscaledTime < _nextSettingsPoll) return;
            _nextSettingsPoll = Time.unscaledTime + 0.2f;

            // 防御：任何原因导致 InApplyAll 卡住（批量应用中途抛异常），2 秒后强制复位，
            // 否则设置改动会永久失效（“改了没反应”）。
            if (InApplyAll && Time.unscaledTime - _inApplyAllSince > 2f) InApplyAll = false;

            // 这里**不再**用控件值反向覆盖运行时状态。
            // 原因：原版设置页每次打开都会重建控件，而 Setting 实例可能被 LoadSettings 换成
            // 新的一份；一旦“控件绑定的实例”和这里 Find 到的实例不是同一个，轮询就会每隔
            // 0.2 秒把用户刚选好的音乐盒改回旧值——表现正是“切换无效 / 选完又跳回游戏默认”。
            // 真正生效只走两条路：① 用户操作控件触发的 Setting.apply；② 关闭设置页时的 Flush。
        }

        // ===============================================================
        // 状态机
        // ===============================================================
        internal void Poll()
        {
            if (Engine == null || Library == null)
            {
#if SELFTEST
                DiagBlocked("Engine=" + (Engine == null) + " Library=" + (Library == null));
#endif
                return;
            }
            try
            {
                EnsureHost();
                EnsureMixerGroup();
                PollSettings();
                TickPendingEvent();
                TrackWorldDwell();
                TrackDepthChange();
                TrackLoadingEdge();

#if SELFTEST
                DiagState();
#endif

                if (Time.unscaledTime >= _nextVolumeRefresh)
                {
                    _nextVolumeRefresh = Time.unscaledTime + 1f;
                    if (_currentEvent.HasValue && Engine.IsTagPlaying(TagOf(_currentEvent.Value)))
                        Engine.SetVolumeForTag(TagOf(_currentEvent.Value), ComputeVolume(_currentEvent.Value));
                }

                if (_currentEvent.HasValue && _currentIsOneShot
                    && !Engine.IsTagPlaying(TagOf(_currentEvent.Value)))
                {
                    _currentEvent = null;
                    _currentIsOneShot = false;
                }

                MusicEvent? desired = ResolveDesired();

                // 状态去抖：进入快（0.35s）、退出慢（1.2s）。
                // 原版 threatMusicTime / earthquakeIntensity / 疼痛骤增这类信号会在边界反复抖，
                // 不去抖就会出现“泡一下水→播放→离开水→淡出→还没淡完又重播”。
                desired = Stabilize(desired);

                if (ForceRetrigger)
                {
                    ForceRetrigger = false;
                    // 世界内打开设置时 ResolveDesired 可能为 null（没有正在进行的战斗/濒死等），
                    // 此时要重播“当前正在放的那一首”，否则用户改完音量/换完音乐盒后是一片安静，
                    // 看起来就像“设置没生效”。
                    MusicEvent? target = desired.HasValue ? desired : _currentEvent;
                    SwitchTo(target, "设置变更（即时生效）");
                    return;
                }

                // 一次性音乐（加载 / 进入地图 / 死亡 / 结局）播放期间不被同级或更低优先级的
                // 持续状态覆盖；但更高优先级（例如结局、死亡）必须能打断它，并按 3 秒淡出切换，
                // 否则会出现“结局音乐放不出来”“切换时音乐被硬切”的现象。
                if (_currentEvent.HasValue && _currentIsOneShot
                    && Engine.IsTagPlaying(TagOf(_currentEvent.Value)))
                {
                    EventDef cur = EventDef.Get(_currentEvent.Value);
                    if (!desired.HasValue) return;
                    EventDef next = EventDef.Get(desired.Value);
                    if (cur != null && next != null && next.Priority <= cur.Priority)
                    {
                        // 交易界面是强交互态，进入时要立刻切音乐，不能等 in_map 播完
                        if (!(desired.Value == MusicEvent.ChoosePart && IsTrading())) return;
                    }
                    SwitchTo(desired, "更高优先级打断一次性音乐");
                    return;
                }

                if (desired.HasValue)
                {
                    if (_currentEvent.HasValue && _currentEvent.Value == desired.Value
                        && Engine.IsTagPlaying(TagOf(desired.Value))) return;
                    SwitchTo(desired, "状态机");
                }
                else if (_currentEvent.HasValue && !_currentIsOneShot)
                {
                    Engine.FadeOutAll(null);
                    _currentEvent = null;
                    Log.Info("状态结束，音乐淡出。");
                }
                // 暂停结束时不要硬恢复：标记淡出结束，让 MutePauseSources 把音量淡回来
                if (!_currentEvent.HasValue || _currentEvent.Value != MusicEvent.Suspend)
                    EndSuspend();
            }
            catch (Exception ex)
            {
                Log.Error("状态轮询异常：", ex);
            }
        }

        private float _nextStateDiag;

        private float _nextBlockDiag;

        private void DiagBlocked(string why)
        {
            if (Time.unscaledTime < _nextBlockDiag) return;
            _nextBlockDiag = Time.unscaledTime + 3f;
            Log.Warn("[DIAG] 状态机被跳过：" + why
                + " host=" + (Host == null)
                + " hostEnabled=" + (Host != null && Host.enabled));
        }

        /// <summary>每 5 秒打印一次状态机判定依据，便于定位“该播却没播”。</summary>
        private void DiagState()
        {
            if (Time.unscaledTime < _nextStateDiag) return;
            _nextStateDiag = Time.unscaledTime + 5f;
            string pr = "null", world = "null", cam = "null", body = "null", pause = "?";
            try
            {
                pr = PreRunScript.instance == null ? "null" : "ok";
                world = WorldGeneration.world == null ? "null" : "ok";
                cam = PlayerCamera.main == null ? "null" : "ok";
                Body b = LocalBody();
                body = b == null ? "null" : ("alive=" + b.alive);
                pause = IsPaused() + "/" + (PauseHandler.main == null ? "noHandler" : PauseHandler.main.isPaused.ToString());
            }
            catch (Exception ex) { Log.Warn("[DIAG] 状态读取失败：" + ex.Message); }

            MusicEvent? d = null;
            try { d = ResolveDesired(); } catch { }
            Log.Info("[DIAG] t=" + Time.unscaledTime.ToString("F1")
                + " pr=" + pr + " world=" + world + " cam=" + cam + " body=" + body
                + " pause=" + pause
                + " box=" + (_box ?? "游戏默认")
                + " cur=" + (_currentEvent.HasValue ? _currentEvent.Value.ToString() : "null")
                + " voices=" + (Engine == null ? -1 : Engine.VoiceCount)
                + " desired=" + (d.HasValue ? d.Value.ToString() : "null"));
        }

        private void SwitchTo(MusicEvent? desired, string reason)
        {
            if (!desired.HasValue)
            {
                Engine.FadeOutAll(null);
                _currentEvent = null;
                _currentIsOneShot = false;
                RestoreVanillaMenuMusic();
                return;
            }
            if (PlayEvent(desired.Value, 0, reason)) return;

            // 播放失败（例如缺文件）：给 3 秒退避，避免每帧重试；
            // 成功路径不设退避，否则会与状态去抖互相锁死（一直判定 InMap 但永远不被采纳）。
            if (desired.Value == MusicEvent.InMap) _inMapFailBackoff = Time.unscaledTime;

            if (_currentEvent.HasValue && !_currentIsOneShot)
            {
                Engine.FadeOutAll(null);
                _currentEvent = null;
            }
            RestoreVanillaMenuMusic();
        }

        private static void RestoreVanillaMenuMusic()
        {
            try
            {
                PreRunScript pr = PreRunScript.instance;
                if (pr == null || pr.menuSource == null) return;
                if (pr.menuSource.clip != null && !pr.menuSource.isPlaying) pr.menuSource.Play();
            }
            catch { }
        }

        private MusicEvent? ResolveDesired()
        {
            if (IsPaused()) return MusicEvent.Suspend;

            if (InWorld())
            {
                Body b = LocalBody();
                if (b != null && !b.alive) return null;      // 死亡由一次性音乐处理
                if (IsArticulosActive()) return MusicEvent.Articulos;
                if (IsAttackHurtActive()) return MusicEvent.AttackHurt;
                // 交易界面：原版在交易时也会切音乐主题，这里对应“角色选择界面音乐”这一项。
                if (IsTrading()) return MusicEvent.ChoosePart;
                // 进图兜底：原版只在特定协程里调 PlayLayerSong，若那次调用没发生/被跳过，
                // 这里补一次“进入地图音乐”。PlayEvent 成功后会立刻标记本层已接管，
                // 因此只会播一次，不会反复重播。
                int depth = CurrentDepth();
                // 注意：这里**不能**因为 waiting_map 还在播就放弃。
                // 加载界面音乐是不循环的整首（十几秒），如果等它播完，进图后要空十几秒
                // 才听到 in_map，玩家会以为“进图没有音乐”。进图窗口一出现就该切换。
                // 注意：不在这里“一次性置位”。去抖可能丢弃本次判定，
                // 若提前置位就会永久丢掉这一层的 in_map（表现为“进图没有音乐”）。
                // 改为 2 秒重试闸门，真正播放成功后由 PlayEvent 里的 MarkInMapTaken 收口。
                if (!IsInMapTaken(depth) && !string.IsNullOrEmpty(_box)
                    && Time.unscaledTime - _inMapFailBackoff > 3f)
                {
                    // 加载音乐独占期内不抢：先让 waiting_map 响，再换成 in_map
                    if (WaitingGuardActive) return null;
                    DiagInMapAttempt(depth);
                    // 等“第一层窗口”（层标题）出现后再播；地图仍在生成时不播。
                    if (IsBiomeIntroReady())
                    {
                        return MusicEvent.InMap;
                    }
                    return null;
                }
                return null;
            }

            PreRunScript pr = PreRunScript.instance;
            if (pr != null)
            {
                GameObject rs = pr.runSettingsScreen;
                bool choosing = rs != null && rs.activeSelf;
                LogMenuState(pr, rs, choosing);
                if (choosing) return MusicEvent.ChoosePart;
                return MusicEvent.Hall;
            }
            return null;
        }

        private string _lastMenuState;

        /// <summary>菜单态切换时打一行日志，便于定位“进了角色选择却没切音乐”这类问题。</summary>
        private void LogMenuState(PreRunScript pr, GameObject rs, bool choosing)
        {
            string now = (choosing ? "ChoosePart" : "Hall")
                + " rs=" + (rs == null ? "null" : (rs.activeSelf ? "active" : "inactive"));
            if (now == _lastMenuState) return;
            _lastMenuState = now;
            string detail = "";
            try
            {
                detail = " runSettingsScreen='"
                    + (rs == null ? "(null)" : rs.name) + "'"
                    + " menuSourcePlaying=" + (pr.menuSource != null && pr.menuSource.isPlaying);
            }
            catch { }
            Log.Info("菜单状态：" + now + detail);
        }

        private static bool IsPaused()
        {
            try
            {
                if (PauseHandler.main == null) return false;
                if (WorldGeneration.world == null) return false;   // 菜单场景无真暂停
                // 必须真的进入暂停（淡入完成），避免暂停动画中途或状态残留导致暂停音乐误播
                if (PauseHandler.main.isPaused && PauseHandler.main.pauseProgress > 0.5f) return true;

                // 打开设置页时，暂停本身没被解除；这里要让“暂停音乐”继续播，
                // 否则一进设置页就会判定成“不在暂停”，suspend 被淡出（表现为点设置后音乐停）。
                MusicBoxPlugin p = Instance;
                if (SettingsMenu.instance != null && p != null) return p.IsPauseMusicActive();
                return false;
            }
            catch { return false; }
        }

        /// <summary>暂停音乐是否正在播放（用于设置页遮挡暂停界面时保持状态）。</summary>
        internal bool IsPauseMusicActive()
        {
            return _currentEvent.HasValue && _currentEvent.Value == MusicEvent.Suspend
                && Engine != null && Engine.IsTagPlaying(TagOf(MusicEvent.Suspend));
        }

        /// <summary>暂停音乐正在播：此时主音源也必须压到 0，避免与原版残留 BGM 叠加。</summary>
        internal bool IsSuspendMusicPlaying()
        {
            return _currentEvent.HasValue && _currentEvent.Value == MusicEvent.Suspend
                && Engine != null && Engine.IsTagPlaying(TagOf(MusicEvent.Suspend));
        }

        /// <summary>
        /// 暂停时压制“不在 drone 数组里”的原版环境音源。
        /// 猩红之地这类场景会挂 caveAudio / underWaterSource / genPodSource 等独立音源，
        /// 它们不受 DuckVanillaDrones 管，暂停时会和 suspend 叠在一起。
        /// </summary>
        internal void MuteLooseVanillaSources()
        {
            try
            {
                WorldGeneration w = WorldGeneration.world;
                if (w != null)
                {
                    if (w.caveAudio != null) w.caveAudio.volume = 0f;
                    if (w.genPodSource != null) w.genPodSource.volume = 0f;
                }
                PlayerCamera pc = PlayerCamera.main;
                if (pc != null && pc.underWaterSource != null) pc.underWaterSource.volume = 0f;
            }
            catch (Exception ex) { Log.Warn("暂停压制环境音失败：" + ex.Message); }
        }

        // ---- 暂停期间的“全量长音源”压制 ----
        // 有些原版音乐/环境音既不在 MusicManager.source，也不在 droneSources，
        // 也不在 caveAudio 这些固定字段上（例如 Sound.Play 生成的音源）。
        // 进入暂停时把场景里所有“非本模组、且时长>5秒”的音源缓存下来，逐帧压 0，
        // 短音效（UI 点击等）不受影响。
        private List<AudioSource> _pauseSources;
        private float _pauseRescanAt;
        private bool _pauseActive;
        private float _pauseFade = 1f;
        private bool _lastLoadingVisible;

        /// <summary>
        /// 加载界面关闭的那一刻：重置本层的 in_map 标记，确保“进入地图音乐”一定播一次。
        /// </summary>
        private void TrackLoadingEdge()
        {
            try
            {
                bool loading = IsLoadingScreenVisible();
                if (_lastLoadingVisible && !loading)
                {
                    int d = CurrentDepth();
                    _inMapTaken.Remove(d);
                    _inMapFailBackoff = -999f;
                    Log.Info("加载界面已关闭：允许 biomeDepth=" + d + " 播放进入地图音乐");
                }
                _lastLoadingVisible = loading;
            }
            catch { }
        }

        internal void OnEnterSuspend()
        {
            CollectPauseSources(true);
            _pauseActive = true;
            _pauseFade = 1f;                    // 从当前音量开始淡出，避免硬切
            _pauseRescanAt = Time.unscaledTime + 0.5f;
        }

        /// <summary>解除暂停：不要立刻恢复原音量，先淡回来。</summary>
        internal void EndSuspend()
        {
            _pauseActive = false;
        }

        private void CollectPauseSources(bool log)
        {
            try
            {
                _pauseSources = new List<AudioSource>();
                AudioSource[] all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
                System.Text.StringBuilder sb = log ? new System.Text.StringBuilder() : null;
                for (int i = 0; i < all.Length; i++)
                {
                    AudioSource s = all[i];
                    if (s == null || s.clip == null) continue;
                    if (s.clip.length < 1.5f) continue;    // 过滤枪声/点击等短音效
                    if (IsOurSource(s)) continue;
                    _pauseSources.Add(s);
                    if (sb != null && _pauseSources.Count <= 10)
                        sb.Append("\n    ").Append(PathOf(s.transform))
                          .Append(" len=").Append(s.clip.length.ToString("F1"))
                          .Append(" loop=").Append(s.loop)
                          .Append(" vol=").Append(s.volume.ToString("F2"))
                          .Append(" play=").Append(s.isPlaying);
                }
                if (log)
                    Log.Info("暂停：已锁定 " + _pauseSources.Count + " 条原版长音源" + (sb == null ? "" : sb.ToString()));
            }
            catch (Exception ex) { Log.Warn("收集暂停音源失败：" + ex.Message); }
        }

        private static string PathOf(Transform t)
        {
            try
            {
                string p = t.name;
                Transform cur = t.parent;
                int guard = 0;
                while (cur != null && guard++ < 6) { p = cur.name + "/" + p; cur = cur.parent; }
                return p;
            }
            catch { return "?"; }
        }

        internal void MutePauseSources()
        {
            if (_pauseSources == null) return;
            float dt = Time.unscaledDeltaTime;
            float target = _pauseActive ? 0f : 1f;
            // 1.2 秒淡出 / 1.2 秒淡入，和音乐盒自身的 1.5 秒交叉过渡接近
            _pauseFade = Mathf.MoveTowards(_pauseFade, target, dt / 1.2f);
            float f = Mathf.Clamp01(_pauseFade);

            for (int i = 0; i < _pauseSources.Count; i++)
            {
                AudioSource s = _pauseSources[i];
                if (s == null) continue;
                if (f <= 0.002f)
                {
                    s.mute = true;                 // 已经完全淡出，用 mute 兜底防止被推回
                    continue;
                }
                s.mute = false;
                // 原版每帧会重设音量，这里做“乘系数”，所以不会累积衰减
                if (s.isPlaying) s.volume = s.volume * f;
            }
            if (!_pauseActive && f >= 0.999f) { ClearPauseSources(); return; }

            // 暂停期间可能有新音源被创建（过场/环境），定期重扫一次
            if (Time.unscaledTime >= _pauseRescanAt)
            {
                _pauseRescanAt = Time.unscaledTime + 1f;
                CollectPauseSources(false);
            }
        }

        internal void ClearPauseSources()
        {
            // 解除暂停时必须恢复 mute，否则原版音乐会被永久静音
            if (_pauseSources != null)
            {
                for (int i = 0; i < _pauseSources.Count; i++)
                {
                    AudioSource s = _pauseSources[i];
                    if (s != null) s.mute = false;
                }
            }
            _pauseSources = null;
        }

        private static bool IsOurSource(AudioSource s)
        {
            Transform t = s.transform;
            int guard = 0;
            while (t != null && guard++ < 16)
            {
                if (t.name.StartsWith("UC-MusicBox", StringComparison.Ordinal)) return true;
                t = t.parent;
            }
            return false;
        }

        private static bool InWorld()
        {
            try { return WorldGeneration.world != null && PlayerCamera.main != null; }
            catch { return false; }
        }

        private static Body LocalBody()
        {
            try { if (PlayerCamera.main != null) return PlayerCamera.main.body; }
            catch { }
            return null;
        }

        private bool IsAttackHurtActive()
        {
            Body b = LocalBody();
            if (b == null || WorldGeneration.world == null) return false;
            try
            {
                // 世界仍在生成（加载界面阶段）时不做战斗判定：
                // 此时身体/敌人刚初始化，threatMusicTime、疼痛值都会出现瞬时跳变，
                // 会把刚响起的加载音乐当成“低优先级一次性音乐”直接打断。
                if (WorldGeneration.world.generatingWorld) return false;
                // 只认“真的挨打/真的被怪盯上”，避免长期高疼痛值或残留辐射读数导致
                // 没有战斗也在放战斗音乐（例如靠近尸体时的环境音）。
                if (Time.unscaledTime - LastDamageTime < 4f) return true;       // 刚受伤
                if (PlayerCamera.main != null && PlayerCamera.main.threatMusicTime > 0f) return true; // 原版威胁音乐
                if (PainSpikeDetected(b)) return true;                          // 疼痛突发（挨打/中机关）
                if (WorldGeneration.world.earthquakeIntensity > 0.05f) return true; // 地震
            }
            catch (Exception ex) { Log.Error("攻击/受伤状态判定异常：", ex); }
            return false;
        }

        private static bool IsTrading()
        {
            try
            {
                PlayerCamera pc = PlayerCamera.main;
                return pc != null && pc.tradeMenu != null && pc.tradeMenu.activeSelf;
            }
            catch { return false; }
        }

        private float _worldSince = -1f;
        private int _lastDepth = -1;
        private float _lastWaitingAt = -999f;
        private int _lastWaitingDepth = int.MinValue;
        private float _waitingGuardUntil = -999f;

        /// <summary>加载音乐刚开始播时给它一段独占期，避免 in_map 立刻抢走。</summary>
        internal bool WaitingGuardActive
        {
            get { return Time.unscaledTime < _waitingGuardUntil; }
        }

        /// <summary>同一层在短时间内只播一次加载音乐（RegenerateWorld 与层变化检测互斥）。</summary>
        internal bool ShouldSkipWaiting(int depth)
        {
            // 只挡“同一次切换被两条路径各播一遍”，窗口不宜过长，
            // 否则快速连续跳层会误判成重复。
            return _lastWaitingDepth == depth && Time.unscaledTime - _lastWaitingAt < 2.5f;
        }

        /// <summary>
        /// 兜底层级切换检测：指令传送/开发者跳层不一定走 RegenerateWorld，
        /// 这里直接盯 biomeDepth 变化，确保每换一层都有“加载地图界面音乐”。
        /// </summary>
        private void TrackDepthChange()
        {
            try
            {
                // 用 world 是否存在判断，而不是 InWorld()（后者还要求 PlayerCamera，
                // 传送瞬间可能为空，导致漏掉一次层变化）。
                if (WorldGeneration.world == null) { _lastDepth = -1; return; }
                int d = CurrentDepth();

                // 重建世界（指令传送/读档）后 _lastDepth 会归 -1：
                // 这时也要按“刚进入该层”处理，否则深处会一直没有加载音乐。
                if (_lastDepth < 0)
                {
                    if (d > 1)
                    {
                        _inMapTaken.Remove(d);
                        _inMapFailBackoff = -999f;
                        if (!ShouldSkipWaiting(d))
                        {
                            int vv = Mathf.Clamp(d - 1, 1, EventDef.Get(MusicEvent.WaitingMap).Files.Length - 1);
                            PlayEvent(MusicEvent.WaitingMap, vv, "进入已有世界（biomeDepth=" + d + "）");
                        }
                    }
                    _lastDepth = d;
                    return;
                }

                if (_lastDepth >= 0 && d != _lastDepth)
                {
                    // 换层（含指令传送）时允许这一层重新播一次“进入地图音乐”
                    _inMapTaken.Remove(d);
                    _inMapFailBackoff = -999f;
                    if (!ShouldSkipWaiting(d))
                    {
                        int variant = d <= 1 ? 0
                            : Mathf.Clamp(d - 1, 1, EventDef.Get(MusicEvent.WaitingMap).Files.Length - 1);
                        PlayEvent(MusicEvent.WaitingMap, variant, "层变化（biomeDepth=" + d + "）");
                    }
                    Log.Info("检测到层级变化：" + _lastDepth + " -> " + d);
                }
                _lastDepth = d;
            }
            catch (Exception ex) { Log.Warn("层级变化检测失败：" + ex.Message); }
        }
        private float _nextInMapDiag;
        private static FieldInfo _loadingObjectField;

        /// <summary>in_map 迟迟不播时，每 3 秒打印一次卡在哪一步，便于一次定位。</summary>
        private void DiagInMapAttempt(int depth)
        {
            if (Time.unscaledTime < _nextInMapDiag) return;
            _nextInMapDiag = Time.unscaledTime + 3f;
            try
            {
                WorldGeneration w = WorldGeneration.world;
                Log.Info("[DIAG] in_map 等待中：depth=" + depth
                    + " generating=" + (w != null && w.generatingWorld)
                    + " loading=" + IsLoadingScreenVisible()
                    + " sinceFinished=" + (w == null ? -1f : w.timeSinceFinishedGeneration).ToString("F1")
                    + " worldSince=" + (_worldSince > 0f ? (Time.unscaledTime - _worldSince) : -1f).ToString("F1")
                    + " introReady=" + IsBiomeIntroReady()
                    + " cur=" + (_currentEvent.HasValue ? _currentEvent.Value.ToString() : "null"));
            }
            catch { }
        }

        internal bool IsLoadingScreenVisible()
        {
            try
            {
                WorldGeneration w = WorldGeneration.world;
                if (w == null) return false;
                if (_loadingObjectField == null)
                    _loadingObjectField = AccessTools.Field(typeof(WorldGeneration), "loadingObject");
                if (_loadingObjectField == null) return false;
                GameObject lo = _loadingObjectField.GetValue(w) as GameObject;
                return lo != null && lo.activeInHierarchy;
            }
            catch { return false; }
        }

        /// <summary>
        /// “第一层窗口”是否已经出现。三个信号任一成立即可：
        ///   1. 层标题（biomeTitle）已激活 —— 这就是玩家看到的那个窗口；
        ///   2. 生成完成且过了 1.5 秒；
        ///   3. 进入世界超过 4 秒（兜底：generatingWorld 这个标志在某些流程里可能一直是 true）。
        /// </summary>
        private bool IsBiomeIntroReady()
        {
            try
            {
                WorldGeneration w = WorldGeneration.world;
                if (w == null) return false;

                // ① 世界还在生成：绝不播 in_map
                if (w.generatingWorld) return false;

                // ② 加载界面还开着：视为还没进入地图。
                //    之前这里用“进世界 N 秒”强制放行，结果 in_map 在等待界面就响了，
                //    随后被 waiting_map 打断，进图后反而不再播。
                if (IsLoadingScreenVisible())
                {
                    // 仅在明显卡住（生成早已结束、加载界面 >10 秒还没关）时才放行兜底
                    bool stuck = w.timeSinceFinishedGeneration > 0f
                        && w.timeSinceFinishedGeneration < 999f
                        && _worldSince > 0f
                        && Time.unscaledTime - _worldSince > 10f;
                    return stuck;
                }

                if (w.biomeTitle != null && w.biomeTitle.gameObject.activeInHierarchy) return true;
                // timeSinceFinishedGeneration 初始占位值是 1000，必须排除
                if (w.timeSinceFinishedGeneration > 2f && w.timeSinceFinishedGeneration < 999f) return true;
                if (_worldSince > 0f && Time.unscaledTime - _worldSince > 4f) return true;
            }
            catch { }
            return false;
        }

        /// <summary>每帧维护“进入世界”的起始时间，供兜底判断使用。</summary>
        private void TrackWorldDwell()
        {
            if (InWorld())
            {
                if (_worldSince < 0f) _worldSince = Time.unscaledTime;
            }
            else _worldSince = -1f;
        }

        private bool IsArticulosActive()
        {
            Body b = LocalBody();
            if (b == null || WorldGeneration.world == null) return false;
            try
            {
                if (WorldGeneration.world.generatingWorld) return false;
                if (b.brainDying) return true;
                if (b.isDying) return true;                      // 濒临死亡
                if (RadiationLine.line != null && RadiationLine.line.active)
                {
                    float dy = Mathf.Abs(RadiationLine.line.transform.position.y - b.transform.position.y);
                    if (dy < 75f) return true;
                }
            }
            catch (Exception ex) { Log.Error("濒死状态判定异常：", ex); }
            return false;
        }

        private bool PainSpikeDetected(Body b)
        {
            float now = Time.unscaledTime;
            if (now - _lastPainCheck < 0.25f) return now < _painSpikeUntil;
            _lastPainCheck = now;
            float pain = b.averagePain;
            // 只在已经有明显疼痛时才算“突发”，避免 0→5 这种噪音级波动触发战斗音乐
            if (_lastPain >= 0f && pain > 25f && pain - _lastPain > 8f) _painSpikeUntil = now + 4f;
            _lastPain = pain;
            return now < _painSpikeUntil;
        }

        // ===============================================================
        // 回退压制 / 层记录
        // ===============================================================
        // ===============================================================
        // 压制原版“战斗威胁音轨”（drone）
        // 虫子/守卫/蜘蛛等触发的是 MusicManager 的 droneSources，而不是主 source，
        // 只拦截 PlaySong 压不住它们，会听到原版战斗音乐叠在音乐盒上。
        // ===============================================================
        private static FieldInfo _droneSourcesField;

        /// <summary>
        /// 统一处理原版 drone 音轨：
        /// 只压“原版音乐”——战斗主题（currentThreatTheme）与交易主题（索引 16）。
        /// 环境/身体状态类音轨不碰，交还原版自己控制（用户要的是“音乐变闷”，不是环境音）。
        /// </summary>
        /// <summary>过场/结局类原版音轨：即使选了音乐盒也放行（剧情演出需要）。</summary>
        internal static bool IsCutsceneClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("surfacebreach", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("escapeend", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("levelup", StringComparison.OrdinalIgnoreCase);
        }

        internal void DuckVanillaDrones()
        {
            try
            {
                if (!_currentEvent.HasValue) return;      // 没在播我们的音乐，交给原版自己管
                MusicManager mm = MusicManager.main;
                PlayerCamera pc = PlayerCamera.main;
                if (mm == null) return;
                if (_droneSourcesField == null)
                    _droneSourcesField = AccessTools.Field(typeof(MusicManager), "droneSources");
                if (_droneSourcesField == null) return;
                List<AudioSource> drones = _droneSourcesField.GetValue(mm) as List<AudioSource>;
                if (drones == null) return;

                int threat = pc != null ? pc.currentThreatTheme : -1;
                for (int i = 0; i < drones.Count; i++)
                {
                    AudioSource s = drones[i];
                    if (s == null || !s.isPlaying) continue;
                    bool isVanillaMusic = i == threat || i == TradeDroneIndex || i == EarthquakeDroneIndex;

                    // 疼痛(0)/濒死(1)/辐射(14) 这几条是“原版情绪音乐”，
                    // 在对应事件下必须让位，否则会与音乐盒叠在一起（受伤时最明显）。
                    if (_currentEvent == MusicEvent.AttackHurt && (i == 0 || i == 1)) isVanillaMusic = true;
                    if (_currentEvent == MusicEvent.Articulos && (i == 0 || i == 1 || i == 14)) isVanillaMusic = true;
                    if (_currentEvent == MusicEvent.Die && (i == 0 || i == 1)) isVanillaMusic = true;

                    // 暂停：整屏都被暂停界面盖住，此时只应听到 suspend，
                    // 所有原版 drone（疼痛/水下/辐射/体力…）一律压掉。
                    if (_currentEvent == MusicEvent.Suspend) isVanillaMusic = true;

                    if (isVanillaMusic) s.volume = 0f;
                }
            }
            catch (Exception ex) { Log.Error("处理原版 drone 音轨失败：", ex); }
        }

        /// <summary>
        /// 原版的“闷音”程度：PlayerCamera.muteTimeVolume = Clamp(-globalMuteTime*15, -80, 0) dB。
        /// 对话/交易/过场会把它拉低。这里映射成 0.45~1 的系数：
        /// 音乐会明显变闷、变小，但不会像原版那样被压到 -80dB 直接消失。
        /// </summary>
        private static FieldInfo _lowCutoffField;

        /// <summary>
        /// 读取原版音乐总线的低通截止频率（MusicManager.lowCutoff）。
        /// 这是原版真正用来表现“闷”的变量：活着时它一路降到约 300Hz，
        /// 进图后 BGM 会明显变闷；死亡时才回到 30000Hz。
        /// 我们把它同步到音乐盒的低通滤波器上，听感就与原版一致。
        /// </summary>
        internal float ComputeVanillaCutoff()
        {
            try
            {
                MusicManager mm = MusicManager.main;
                if (mm == null) return 0f;
                if (_lowCutoffField == null)
                    _lowCutoffField = AccessTools.Field(typeof(MusicManager), "lowCutoff");
                if (_lowCutoffField == null) return 0f;
                object raw = _lowCutoffField.GetValue(mm);
                if (!(raw is float)) return 0f;
                float v = (float)raw;
                if (v < 120f || v > 21000f) return 0f;
                // 原版活着时它会降到 250~300，如果完全照搬会把音乐压成只剩低频，
                // 这里设一个 700Hz 下限：保留明显的“闷”，但仍听得清旋律。
                if (v < 700f) v = 700f;
                return v;
            }
            catch { return 0f; }
        }

        /// <summary>交易界面固定的闷音系数（越小越闷、越小声）。0.55 ≈ 音量 −5dB。</summary>
        private const float TradeDuck = 0.55f;

        internal float ComputeDuckFactor()
        {
            float duck = 1f;
            try
            {
                PlayerCamera pc = PlayerCamera.main;
                if (pc != null)
                {
                    // 注意：muteTimeVolume 只有“声音炮/致聋”这类效果才会把 globalMuteTime 抬高，
                    // 交易与对话并不会改它（globalMuteTime 平时只是 MoveTowards 回 0）。
                    // 所以交易必须单独给一个固定值，否则永远 duck=1，听不出闷。
                    float db = pc.muteTimeVolume;
                    if (db < -0.5f) duck = Mathf.Clamp(Mathf.Pow(10f, db / 20f), 0.25f, 1f);
                }
            }
            catch { }
            if (IsTrading()) duck = Mathf.Min(duck, TradeDuck);
            // 刚进入世界的前 3 秒给一点“闷”的过渡（原版进图时也是从闷到通透），
            // 让 in_map 淡入时不至于太亮、太突然。
            if (_worldSince > 0f)
            {
                float t = Time.unscaledTime - _worldSince;
                if (t < 3f) duck = Mathf.Min(duck, Mathf.Lerp(0.8f, 1f, t / 3f));
            }
            return duck;
        }

        /// <summary>
        /// 原版交易界面的 drone 索引（MusicManager.Update 里 tradeMenu.activeSelf 时激活的那条）。
        /// 不压它就会听到原版交易音乐混在 choose_part 上。
        /// </summary>
        private const int TradeDroneIndex = 16;
        /// <summary>地震氛围音轨（earthquakeIntensity > 0 时激活的那条）。</summary>
        private const int EarthquakeDroneIndex = 13;


        internal bool ShouldMuteVanilla()
        {
            if (!FallbackMuteMode && !PatchingFailed) return false;
            if (!_currentEvent.HasValue) return false;
            return Engine != null && Engine.IsTagPlaying(TagOf(_currentEvent.Value));
        }

        internal void MarkInMapTaken(int depth) { _inMapTaken[depth] = true; }
        internal bool IsInMapTaken(int depth) { return _inMapTaken.ContainsKey(depth); }
        internal void ClearInMapTaken()
        {
            _inMapTaken.Clear();
            _inMapFailBackoff = -999f;
            RegenCount = 0;
            FirstLoadingPlayed = false;
        }

        /// <summary>本局世界生成的次数（第一层 = 1，第二层 = 2 …）。</summary>
        internal int RegenCount;

        /// <summary>第一层的“加载地图音乐”是否已经播过（由 StartRun 负责）。</summary>
        internal bool FirstLoadingPlayed;

        internal static int CurrentDepth()
        {
            try { return WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : 0; }
            catch { return 0; }
        }

        /// <summary>
        /// 原版 StopSong 是否应该连带停掉本模组的音乐。
        /// 一次性音乐（加载/进入地图/死亡/结局）必须独立播完：原版在进图协程里会多次
        /// StopSong，如果跟着停，就会出现“刚进地图音乐响一下就没了，而且之后不再重播”。
        /// </summary>
        internal bool ShouldStopWithVanilla()
        {
            if (!_currentEvent.HasValue) return true;
            return !_currentIsOneShot;
        }

        // ===============================================================
        // 原版音轨 -> 事件
        // ===============================================================
        internal static MusicEvent? EventForClip(AudioClip clip)
        {
            if (clip == null) return null;
            try
            {
                MusicManager mm = MusicManager.main;
                if (mm != null)
                {
                    if (clip == mm.dyingClip) return MusicEvent.Articulos;
                    if (clip == mm.deadClip) return MusicEvent.Die;
                }
                string n = clip.name;
                if (string.IsNullOrEmpty(n)) return null;
                if (string.Equals(n, EventDef.ClipFreedom, StringComparison.OrdinalIgnoreCase)) return MusicEvent.Result3;
                if (string.Equals(n, EventDef.ClipEndLabDrone, StringComparison.OrdinalIgnoreCase)) return MusicEvent.Result2;
                if (string.Equals(n, EventDef.ClipBiopod, StringComparison.OrdinalIgnoreCase)) return MusicEvent.Result1;
                if (string.Equals(n, EventDef.ClipWhatHaveIDone, StringComparison.OrdinalIgnoreCase)) return MusicEvent.Die;
                if (string.Equals(n, EventDef.ClipThornbackDefeat, StringComparison.OrdinalIgnoreCase)) return MusicEvent.Result2;
                if (n.IndexOf(EventDef.ClipTutorial, StringComparison.OrdinalIgnoreCase) >= 0) return MusicEvent.InMap;
            }
            catch (Exception ex) { Log.Error("音频剪辑识别异常：", ex); }
            return null;
        }
    }

    /// <summary>每帧驱动音频引擎与状态机。</summary>
    public sealed class MusicBoxHost : MonoBehaviour
    {
        internal AudioEngine Engine;
        internal MusicBoxPlugin Plugin;
        // 说明：驱动逻辑已挪到 MusicBoxPlugin.Update（BepInEx Manager 组件必定收到
        // Unity 消息）。这个宿主只作为 AudioEngine 的父节点与音量源容器存在，
        // 不再承担每帧驱动，避免“宿主被创建但 Update 不被调用”导致状态机静默失效。
    }
}