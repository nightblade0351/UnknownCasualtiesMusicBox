using System;
using System.Collections.Generic;
using System.Reflection;
using CUCoreLib.Registries;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UCMB
{
    /// <summary>
    /// 原生设置集成（不经过 CUCoreLib 的 ModOptionsRegistry）：
    ///
    /// 直接向游戏自己的 <c>Settings.settings</c> 注入原生 SettingDropdown / SettingFloat，
    /// 全部落在原版【音频】页，因此：
    ///   * 复用原版控件、字体、间距、悬停与拖动反馈（零自绘 UI）；
    ///   * 不产生额外的自定义分类标签 / 侧栏；
    ///   * 不被其它模组的 ConfigEntry 同步机制反向覆盖。
    ///
    /// 每次 Settings.LoadSettings 重建列表后都会重新注入（见 Patches.Settings_Load_Postfix）。
    /// </summary>
    internal static class SettingsUi
    {
        internal const string IdBox = MusicBoxPlugin.ModIdPrefix + "box";
        internal const string IdVolumePrefix = MusicBoxPlugin.ModIdPrefix + "vol.";

        private const Setting.SettingCategory Page = Setting.SettingCategory.Audio;
        private const string LocPrefix = "gameset";

        private static readonly Dictionary<string, Setting> _cache = new Dictionary<string, Setting>();

        // 当前真正被原版设置页渲染、绑定回调的那一组实例。
        // 只认它们，避免“列表被重建后 Find 到另一份幽灵实例”，导致读到旧的 value。
        internal static SettingDropdown LiveBox;
        private static readonly Dictionary<MusicEvent, SettingFloat> LiveVolumes =
            new Dictionary<MusicEvent, SettingFloat>();

        internal static SettingFloat LiveVolume(MusicEvent ev)
        {
            SettingFloat sf;
            return LiveVolumes.TryGetValue(ev, out sf) ? sf : null;
        }

        internal static string VolumeId(MusicEvent ev)
        {
            return IdVolumePrefix + EventDef.Get(ev).Key;
        }

        internal static void ClearCache()
        {
            _cache.Clear();
        }

        /// <summary>列表即将被原版重建：此时旧实例（含 UI 绑定）全部作废。</summary>
        internal static void InvalidateLive()
        {
            _cache.Clear();
            LiveBox = null;
            LiveVolumes.Clear();
        }

        // ---------------------------------------------------------------
        // 注入
        // ---------------------------------------------------------------
        internal static void EnsureInjected(MusicBoxPlugin plugin)
        {
            try
            {
                List<Setting> list = Settings.GetAllSettings();
                if (list == null) return;
                if (AppendTo(list, plugin))
                {
                    ClearCache();
                    Log.Info("已向原版“音频”页注入 " + (EventDef.All.Length + 1) + " 个音乐盒设置项。");
                }
            }
            catch (Exception ex)
            {
                Log.Error("注入设置项失败：", ex);
            }
        }

        /// <summary>
        /// 把 12 个设置项追加到给定列表（幂等）。
        /// 由 Settings.DefaultSettings 的 postfix 调用，使 LoadSettings / ResetToDefault 自动携带本模组设置项。
        /// </summary>
        internal static bool AppendTo(List<Setting> list, MusicBoxPlugin plugin)
        {
            if (list == null || plugin == null) return false;
            bool changed = false;
            if (IndexOf(list, IdBox) < 0)
            {
                list.Add(BuildBox(plugin));
                changed = true;
            }
            for (int i = 0; i < EventDef.All.Length; i++)
            {
                EventDef def = EventDef.All[i];
                if (IndexOf(list, VolumeId(def.Id)) < 0)
                {
                    list.Add(BuildVolume(plugin, def));
                    changed = true;
                }
            }
            if (changed)
            {
                ClearCache();
                RegisterLocale(plugin);
            }
            return changed;
        }

        private static int IndexOf(List<Setting> list, string name)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != null && list[i].name == name) return i;
            }
            return -1;
        }

        private static SettingDropdown BuildBox(MusicBoxPlugin plugin)
        {
            List<string> keys = new List<string>();
            keys.Add(BoxLibrary.DefaultChoiceKey);
            if (plugin.Library != null)
            {
                IList<string> boxes = plugin.Library.Boxes;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i] == BoxLibrary.DefaultChoiceKey) continue;
                    keys.Add(boxes[i]);
                }
            }

            SettingDropdown sd = new SettingDropdown();
            sd.name = IdBox;
            sd.category = Page;
            sd.choices = keys.ToArray();
            // 注入时一律用“游戏默认”作为初始值；真实保存值由 SettingsUi.ReconcileFromCfg 在
            // LoadSettings 之后填入。这样游戏自带的“重置为默认”才会真的回到默认。
            sd.value = 0;
            // 原版在用户操作控件时执行：Setting.SetValue(...) -> Setting.Apply()。
            // 因此把“即时生效”挂在这里最可靠；Settings.ApplyAll（启动加载 / 重置为默认）
            // 会批量调用 apply，用 InApplyAll 标志区分，避免把加载值误当成用户操作。
            //
            // 注意：这里**直接读取被点击的那个 Setting 自身**的 value，绝不回头去找
            // “当前列表里的同 name 对象”。列表被 LoadSettings 重建后，旧控件绑定的仍是
            // 旧实例，用 Find 会读到新实例的默认值（0），表现为“选了又跳回游戏默认”。
            sd.apply = delegate
            {
                int ui = sd.value;
                Log.Info("[UI] 音乐盒下拉框 apply 触发，值=" + ui);
                plugin.ApplyBoxFromUi(ui);
            };
            LiveBox = sd;
            return sd;
        }

        private static SettingFloat BuildVolume(MusicBoxPlugin plugin, EventDef def)
        {
            MusicEvent captured = def.Id;
            SettingFloat sf = new SettingFloat();
            sf.name = VolumeId(def.Id);
            sf.category = Page;
            sf.min = 0f;
            sf.max = 100f;
            sf.value = 100f;   // 默认 100%；真实值由 ReconcileFromCfg 填入
            sf.formatValue = delegate (float v) { return Mathf.RoundToInt(v).ToString() + "%"; };
            sf.apply = delegate
            {
                plugin.ApplyVolumeFromUi(captured, sf.value);
            };
            LiveVolumes[captured] = sf;
            return sf;
        }

        // ---------------------------------------------------------------
        // 本地化（沿用 CUCoreLib 的 LocaleRegistry，仅写字典、不涉及 Setting）
        // ---------------------------------------------------------------
        private static void RegisterLocale(MusicBoxPlugin plugin)
        {
            try
            {
                Reg(IdBox, "音乐盒");
                Reg(IdBox + "dsc",
                    "选择 data 下的音乐盒文件夹：一个文件夹同时提供全部事件音乐。选“游戏默认”则完全不接管音乐、保持原版行为。");

                Reg(IdBox + BoxLibrary.DefaultChoiceKey, BoxLibrary.DefaultChoiceLabel);
                if (plugin.Library != null)
                {
                    IList<string> boxes = plugin.Library.Boxes;
                    for (int i = 0; i < boxes.Count; i++) Reg(IdBox + boxes[i], boxes[i]);
                }

                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    EventDef def = EventDef.All[i];
                    string id = VolumeId(def.Id);
                    Reg(id, def.Display + "音量");
                    Reg(id + "dsc", def.Desc + " 音量 0%-100%，步进 1%；设为 0% 为静音但仍保留所选音乐盒。");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("注册本地化失败（将回退显示原始键名）：" + ex.Message);
            }
        }

        private static void Reg(string id, string text)
        {
            try { LocaleRegistry.Register(LocaleRegistry.LocaleCategory.Other, LocPrefix + id, text); }
            catch { }
        }

        // ---------------------------------------------------------------
        // 查找
        // ---------------------------------------------------------------
        internal static Setting FindSetting(string name)
        {
            Setting cached;
            if (_cache.TryGetValue(name, out cached) && cached != null) return cached;
            List<Setting> all = Settings.GetAllSettings();
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].name == name)
                {
                    _cache[name] = all[i];
                    return all[i];
                }
            }
            return null;
        }

        internal static SettingDropdown FindBoxSetting()
        {
            return FindSetting(IdBox) as SettingDropdown;
        }

        internal static SettingFloat FindVolumeSetting(MusicEvent ev)
        {
            return FindSetting(VolumeId(ev)) as SettingFloat;
        }

        // ---------------------------------------------------------------
        // cfg -> Setting（cfg 为权威）
        // ---------------------------------------------------------------
        internal static void ReconcileFromCfg(MusicBoxPlugin plugin)
        {
            try
            {
                ClearCache();
                SettingDropdown sd = FindBoxSetting();
                if (sd != null)
                {
                    int idx = 0;
                    if (!string.IsNullOrEmpty(plugin.MusicBox) && plugin.Library != null)
                    {
                        int real = plugin.Library.IndexOf(plugin.MusicBox);
                        if (real > 0) idx = real;
                    }
                    sd.SetValue(idx);
                }
                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    MusicEvent ev = EventDef.All[i].Id;
                    SettingFloat sf = FindVolumeSetting(ev);
                    if (sf != null) sf.SetValue((float)plugin.VolumeOf(ev));
                }
                plugin.MarkSettingsSynced();
            }
            catch (Exception ex)
            {
                Log.Error("配置对账失败：", ex);
            }
        }

        // ---------------------------------------------------------------
        // 打开设置页：1% 步进 + 显示值与内存对齐
        // ---------------------------------------------------------------
        internal static void SyncOpenMenu(MusicBoxPlugin plugin, SettingsMenu menu, Setting.SettingCategory category)
        {
            try
            {
                if (menu == null || plugin == null) return;
                if (category != Page) return;

                ScrollToTop(menu);

                FieldInfo fi = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings");
                if (fi == null) return;
                List<GameObject> rows = fi.GetValue(menu) as List<GameObject>;
                if (rows == null) return;

                List<Setting> all = Settings.GetAllSettings();
                if (all == null) return;
                List<Setting> filtered = new List<Setting>();
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].category == category) filtered.Add(all[i]);
                }
                if (filtered.Count != rows.Count) return;   // 其它模组同帧追加，下一帧再试

                for (int i = 0; i < filtered.Count; i++)
                {
                    Setting s = filtered[i];
                    if (s == null || s.name == null) continue;
                    if (!s.name.StartsWith(MusicBoxPlugin.ModIdPrefix, StringComparison.Ordinal)) continue;
                    GameObject row = rows[i];
                    if (row == null || row.transform.childCount < 2) continue;
                    Transform t1 = row.transform.GetChild(1);

                    if (s.name == IdBox)
                    {
                        TMP_Dropdown dd = t1.GetComponent<TMP_Dropdown>();
                        if (dd == null) continue;
                        if (dd.GetComponent<MusicBoxDropdownTag>() == null)
                            dd.gameObject.AddComponent<MusicBoxDropdownTag>();
                        EnsureDropdownMaskFixer(dd);
                        SettingDropdown sd = s as SettingDropdown;
                        int idx = 0;
                        if (!string.IsNullOrEmpty(plugin.MusicBox) && plugin.Library != null)
                        {
                            int real = plugin.Library.IndexOf(plugin.MusicBox);
                            if (real > 0) idx = real;
                        }
                        // 数据与显示必须一起校正：
                        // 若只改下拉框显示、不改 Setting.value，用户再点“当前显示的那一项”
                        // 时 TMP_Dropdown 认为值没变、不会触发 onValueChanged，表现为“点了没反应”。
                        if (sd != null && sd.value != idx) sd.value = idx;
                        if (dd.value != idx) dd.SetValueWithoutNotify(idx);
                    }
                    else if (s.name.StartsWith(IdVolumePrefix, StringComparison.Ordinal))
                    {
                        Slider slider = t1.GetComponent<Slider>();
                        if (slider == null) continue;
                        slider.wholeNumbers = true;   // 1% 步进，外观保持原版
                        MusicEvent? ev = EventOfVolumeId(s.name);
                        if (!ev.HasValue) continue;
                        float v = plugin.VolumeOf(ev.Value);
                        SettingFloat sf = s as SettingFloat;
                        if (sf != null && Mathf.Abs(sf.value - v) > 0.001f) sf.value = v;
                        if (Mathf.Abs(slider.value - v) > 0.001f) slider.SetValueWithoutNotify(v);
                        if (row.transform.childCount > 2)
                        {
                            TextMeshProUGUI txt = row.transform.GetChild(2).GetComponent<TextMeshProUGUI>();
                            if (txt != null) txt.text = Mathf.RoundToInt(v).ToString() + "%";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("设置页同步失败：", ex);
            }
        }

        private static int _scrollTopFrames = -1;

        /// <summary>
        /// 给“音乐盒”下拉的 Template 挂上解遮罩组件，并适度加高列表，
        /// 让音乐盒变多时可以在列表内部直接滚动，而不是先滚整个设置页。
        /// </summary>
        private static void EnsureDropdownMaskFixer(TMP_Dropdown dd)
        {
            try
            {
                RectTransform tpl = dd.template;
                if (tpl == null) return;
                if (tpl.GetComponent<DropdownMaskFixer>() == null)
                {
                    DropdownMaskFixer fixer = tpl.gameObject.AddComponent<DropdownMaskFixer>();
                    fixer.CaptureOrigin();
                }
                // 列表高度上限：原版模板通常较矮，装不下十几个音乐盒。
                // 这里给一个较宽松的高度（不超过屏幕的 60%），内容再多就靠列表自身滚动。
                float maxH = Mathf.Min(560f, Screen.height * 0.6f);
                if (tpl.sizeDelta.y > 0f && tpl.sizeDelta.y < maxH)
                    tpl.sizeDelta = new Vector2(tpl.sizeDelta.x, maxH);

                EnsureDropdownScrollRelay(tpl);
            }
            catch (Exception ex) { Log.Warn("下拉列表适配失败：" + ex.Message); }
        }

        /// <summary>让滚轮作用于列表本身：把转发组件挂在 Viewport（比 Template 更内层）。</summary>
        internal static void EnsureDropdownScrollRelay(RectTransform tpl)
        {
            try
            {
                if (tpl == null) return;
                ScrollRect sr = tpl.GetComponent<ScrollRect>();
                if (sr == null) sr = tpl.GetComponentInChildren<ScrollRect>(true);
                if (sr == null) return;
                sr.scrollSensitivity = 40f;
                Transform vp = sr.viewport != null ? sr.viewport : tpl;
                DropdownScrollRelay relay = vp.GetComponent<DropdownScrollRelay>();
                if (relay == null) relay = vp.gameObject.AddComponent<DropdownScrollRelay>();
                relay.Target = sr;
            }
            catch { }
        }

        /// <summary>
        /// 强制修正下拉列表的滚动区域。
        /// TMP 的 Template 只按预设高度显示，选项一多（例如 22 个音乐盒）就只露出十几项；
        /// 若 Content 高度没被撑开，ScrollRect 也滚不动。这里按实际子项总高度重设 Content，
        /// 并把 Template 高度夹到屏幕可容纳范围内。
        /// </summary>
        internal static void FixDropdownScrollArea(TMP_Dropdown dd, RectTransform tpl)
        {
            try
            {
                if (dd == null || tpl == null) return;

                Transform vp = tpl.Find("Viewport");
                Transform ct = vp != null ? vp.Find("Content") : null;
                ScrollRect sr = tpl.GetComponent<ScrollRect>();
                if (sr == null && vp != null) sr = vp.GetComponent<ScrollRect>();

                RectTransform content = (sr != null && sr.content != null) ? sr.content : (ct as RectTransform);
                float total = 0f;
                int shown = 0;
                if (content != null)
                {
                    for (int i = 0; i < content.childCount; i++)
                    {
                        RectTransform c = content.GetChild(i) as RectTransform;
                        if (c == null || !c.gameObject.activeSelf) continue;
                        total += c.rect.height;
                        shown++;
                    }
                }

                float maxH = Mathf.Min(520f, Screen.height * 0.55f);
                float tplH = Mathf.Min(Mathf.Max(total, 60f), maxH);

                if (content != null && total > 0f && Mathf.Abs(content.sizeDelta.y - total) > 1f)
                    content.sizeDelta = new Vector2(content.sizeDelta.x, total);
                if (Mathf.Abs(tpl.sizeDelta.y - tplH) > 1f)
                    tpl.sizeDelta = new Vector2(tpl.sizeDelta.x, tplH);

                // Viewport 通常是“铺满父级”的 stretch 节点：
                // 对 stretch 而言 sizeDelta 是“增量”，直接设成 tplH 会让高度变成 2 倍
                // （日志里 列表高=620 而 视口高=1240 就是这个问题，视口比内容还高 → 滚不动）。
                RectTransform vprt = vp as RectTransform;
                if (vprt != null)
                {
                    bool stretchY = vprt.anchorMin.y <= 0.001f && vprt.anchorMax.y >= 0.999f;
                    float wantY = stretchY ? 0f : tplH;
                    if (Mathf.Abs(vprt.sizeDelta.y - wantY) > 1f)
                        vprt.sizeDelta = new Vector2(vprt.sizeDelta.x, wantY);
                }

                // 列表展开后可能超出屏幕（TMP 是按旧高度算的位置），这里把它拉回来
                try
                {
                    Canvas.ForceUpdateCanvases();
                    Vector3[] corners = new Vector3[4];
                    tpl.GetWorldCorners(corners);
                    float top = corners[2].y, bottom = corners[0].y;
                    if (top > Screen.height) tpl.position -= new Vector3(0f, top - Screen.height, 0f);
                    else if (bottom < 0f) tpl.position += new Vector3(0f, -bottom, 0f);
                }
                catch { }

                // 同步回模板：TMP 下次展开是复制模板尺寸，不同步的话高度会跳回原值
                if (dd.template != null && tpl != dd.template
                    && Mathf.Abs(dd.template.sizeDelta.y - tplH) > 1f)
                    dd.template.sizeDelta = new Vector2(dd.template.sizeDelta.x, tplH);

                // 滚轮转发挂到“实际显示的那个列表”的 Viewport 上
                if (sr != null)
                {
                    sr.scrollSensitivity = 40f;
                    Transform relayHost = vp != null ? vp : tpl;
                    DropdownScrollRelay relay = relayHost.GetComponent<DropdownScrollRelay>();
                    if (relay == null) relay = relayHost.gameObject.AddComponent<DropdownScrollRelay>();
                    relay.Target = sr;
                }

                Log.Info("[DIAG] 下拉展开：选项=" + dd.options.Count
                    + " 子项=" + shown
                    + " 总高=" + total.ToString("F0")
                    + " 列表高=" + tpl.sizeDelta.y.ToString("F0")
                    + " 视口高=" + (vp == null ? -1f : (vp as RectTransform).rect.height).ToString("F0")
                    + " content=" + (content == null ? "null" : content.sizeDelta.y.ToString("F0"))
                    + " sr=" + (sr == null ? "null" : "ok")
                    + " 结构=" + Describe(tpl));
            }
            catch (Exception ex) { Log.Warn("下拉滚动区域修正失败：" + ex.Message); }
        }

        /// <summary>
        /// 取 TMP 当前“真正显示”的下拉列表对象。
        /// Show() 会把 m_Template 克隆成 m_Dropdown 挂到 Canvas 上，
        /// 真正承载条目和滚动的是这个克隆体，而不是 dd.template。
        /// </summary>
        internal static RectTransform GetShownDropdownList(TMP_Dropdown dd)
        {
            try
            {
                if (dd == null) return null;
                if (_dropDownField == null)
                    _dropDownField = HarmonyLib.AccessTools.Field(typeof(TMP_Dropdown), "m_Dropdown");
                if (_dropDownField == null) return null;
                GameObject go = _dropDownField.GetValue(dd) as GameObject;
                if (go == null) return null;
                return go.transform as RectTransform;
            }
            catch { return null; }
        }

        private static System.Reflection.FieldInfo _dropDownField;

        private static string Describe(Transform t)
        {
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < t.childCount; i++)
                {
                    Transform c = t.GetChild(i);
                    sb.Append(c.name);
                    if (c.childCount > 0)
                    {
                        sb.Append('(');
                        for (int k = 0; k < c.childCount && k < 4; k++)
                        {
                            if (k > 0) sb.Append(',');
                            sb.Append(c.GetChild(k).name);
                            sb.Append('×').Append(c.GetChild(k).childCount);
                        }
                        sb.Append(')');
                    }
                    sb.Append(" | ");
                }
                return sb.ToString();
            }
            catch { return "?"; }
        }


        /// <summary>
        /// 切到音频页后把列表滚到顶部：否则列表沿用上一次的位置（常常停在底部），
        /// 用户看到的就只有后半段音量项，“音乐盒”和前面的音量项像是被删掉了。
        /// 必须延后几帧执行，等原版布局重建（content.sizeDelta）完成后再定位。
        /// </summary>
        internal static void ScrollToTop(SettingsMenu menu)
        {
            if (menu == null) return;
            _scrollTopFrames = 3;
        }

        internal static void ResetScrollTop()
        {
            _scrollTopFrames = -1;
        }

        internal static void Tick()
        {
            if (_scrollTopFrames < 0) return;
            _scrollTopFrames--;
            if (_scrollTopFrames > 0) return;
            _scrollTopFrames = -1;
            try
            {
                SettingsMenu menu = SettingsMenu.instance;
                if (menu == null) return;
                ScrollRect[] all = menu.GetComponentsInChildren<ScrollRect>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    ScrollRect s = all[i];
                    if (s == null || s.name != "Scroll") continue;   // 只处理设置列表本体
                    s.StopMovement();
                    s.verticalNormalizedPosition = 1f;
                }
            }
            catch (Exception ex) { Log.Warn("设置列表滚动复位失败：" + ex.Message); }
        }

        private static MusicEvent? EventOfVolumeId(string id)
        {
            string key = id.Substring(IdVolumePrefix.Length);
            for (int i = 0; i < EventDef.All.Length; i++)
            {
                if (EventDef.All[i].Key == key) return EventDef.All[i].Id;
            }
            return null;
        }
    }
}