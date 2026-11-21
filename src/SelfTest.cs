#if SELFTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UCMB
{
    /// <summary>开发期自检（仅 /define:SELFTEST 编译）。</summary>
    internal static class SelfTest
    {
        private static bool _ran;

        internal static void Install(Harmony harmony)
        {
            try
            {
                harmony.Patch(AccessTools.Method(typeof(PreRunScript), "Start"),
                    null, new HarmonyMethod(AccessTools.Method(typeof(SelfTest), "PreRun_Start_Postfix")));
                Log.Info("[SELFTEST] 已安装自检钩子。");
            }
            catch (Exception ex) { Log.Error("[SELFTEST] 安装失败：", ex); }
        }

        private static void PreRun_Start_Postfix()
        {
            MusicBoxPlugin p = MusicBoxPlugin.Instance;
            if (p == null) { Log.Info("[SELFTEST] plugin=null"); return; }
            p.EnsureHost();
            if (p.Host == null) { Log.Info("[SELFTEST] host=null"); return; }
            if (_ran) return;
            _ran = true;
            try { p.Host.StartCoroutine(Run(p)); }
            catch (Exception ex) { Log.Error("[SELFTEST] 启动协程失败：", ex); }
        }

        internal static void Tick(MusicBoxPlugin p) { }

        private static IEnumerator Run(MusicBoxPlugin p)
        {
            yield return new WaitForSecondsRealtime(2.0f);
            Log.Info("[SELFTEST] ====== 开始自检 ======");

            // 1) 设置项统计
            List<Setting> all = Settings.GetAllSettings();
            int ours = 0, onAudio = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Setting s = all[i];
                if (s == null || s.name == null) continue;
                if (!s.name.StartsWith(MusicBoxPlugin.ModIdPrefix, StringComparison.Ordinal)) continue;
                ours++;
                if (s.category == Setting.SettingCategory.Audio) onAudio++;
                Log.Info("[SELFTEST] 设置项 " + s.name + " type=" + s.GetType().Name + " cat=" + (int)s.category);
            }
            Log.Info("[SELFTEST] 本模组设置项 = " + ours + "（预期 12），其中在音频页 = " + onAudio);
            Log.Info("[SELFTEST] 本地化 gamesetucmb.box = " + Locale.GetOther("gamesetucmb.box"));
            Log.Info("[SELFTEST] 本地化 gamesetucmb.vol.hall = " + Locale.GetOther("gamesetucmb.vol.hall"));

            // 2) 标签栏：不应存在 CUCoreLib 自定义标签面板
            bool opened = false;
            try { SettingsMenu.OpenMenu(PreRunScript.instance.transform); opened = true; }
            catch (Exception ex) { Log.Error("[SELFTEST] 打开设置页失败：", ex); }
            if (opened) yield return null;
            yield return new WaitForSecondsRealtime(1.5f);

            SettingsMenu menu = SettingsMenu.instance;
            if (menu != null)
            {
                try
                {
                    FieldInfo fb = AccessTools.Field(typeof(SettingsMenu), "buttons");
                    List<Button> btns = fb == null ? null : fb.GetValue(menu) as List<Button>;
                    Log.Info("[SELFTEST] 原生标签数 = " + (btns == null ? -1 : btns.Count) + "（预期 6，无自定义音乐盒标签）");
                    if (btns != null)
                        for (int i = 0; i < btns.Count; i++)
                        {
                            if (btns[i] == null) continue;
                            TextMeshProUGUI tx = btns[i].GetComponentInChildren<TextMeshProUGUI>();
                            Log.Info("[SELFTEST]   标签" + i + " '" + btns[i].name + "' = '" + (tx == null ? "?" : tx.text) + "'");
                        }

                    GameObject frame = FindByName("CUCoreLibModOptionTabFrame");
                    GameObject tabs = FindByName("CUCoreLibModOptionTabs");
                    Log.Info("[SELFTEST] CUCoreLibTabFrame=" + (frame == null ? "不存在(正确)" : "存在(异常)")
                        + " CUCoreLibTabs=" + (tabs == null ? "不存在(正确)" : "存在(异常)"));
                }
                catch (Exception ex) { Log.Warn("[SELFTEST] 读取标签失败：" + ex.Message); }
            }

            // 3) 音频页内容
            bool sel = false;
            try { menu.SelectTab(Setting.SettingCategory.Audio); sel = true; }
            catch (Exception ex) { Log.Error("[SELFTEST] 切音频页失败：", ex); }
            if (sel) yield return null;
            if (sel)
            {
                yield return new WaitForSecondsRealtime(0.4f);
                try
                {
                    string shot = System.IO.Path.Combine(Application.persistentDataPath, "ucmb_audio_page.png");
                    ScreenCapture.CaptureScreenshot(shot);
                    Log.Info("[SELFTEST] 截图 -> " + shot);
                }
                catch (Exception ex) { Log.Warn("[SELFTEST] 截图失败：" + ex.Message); }

                FieldInfo fi = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings");
                List<GameObject> rows = fi == null ? null : fi.GetValue(menu) as List<GameObject>;
                Log.Info("[SELFTEST] 音频页总行数 = " + (rows == null ? -1 : rows.Count));
                if (rows != null)
                {
                    int shown = 0;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        GameObject row = rows[i];
                        if (row == null || row.transform.childCount < 2) continue;
                        TextMeshProUGUI lab = row.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
                        string lt = lab == null ? "?" : lab.text;
                        if (lt.IndexOf("音乐盒", StringComparison.Ordinal) < 0
                            && lt.IndexOf("音乐音量", StringComparison.Ordinal) >= 0) { }
                        Transform t1 = row.transform.GetChild(1);
                        Slider sl = t1.GetComponent<Slider>();
                        TMP_Dropdown dd = t1.GetComponent<TMP_Dropdown>();
                        if (shown < 16)
                        {
                            if (dd != null)
                                Log.Info("[SELFTEST]   行" + i + " '" + lt + "' Dropdown opt=" + dd.options.Count + " val=" + dd.value);
                            else if (sl != null)
                            {
                                string d = row.transform.childCount > 2 && row.transform.GetChild(2).GetComponent<TextMeshProUGUI>() != null
                                    ? row.transform.GetChild(2).GetComponent<TextMeshProUGUI>().text : "?";
                                Log.Info("[SELFTEST]   行" + i + " '" + lt + "' Slider whole=" + sl.wholeNumbers
                                    + " [" + sl.minValue + "," + sl.maxValue + "] val=" + sl.value + " text='" + d + "'");
                            }
                        }
                        shown++;
                    }
                    Log.Info("[SELFTEST] 已渲染控件行 = " + shown);
                }

                // 截图 2：滚动到底部，确认 11 个音量项全部可见（不是“被删掉了”）
                ScrollRect sr = null;
                try { sr = menu.GetComponentInChildren<ScrollRect>(); }
                catch (Exception ex) { Log.Warn("[SELFTEST] 取 ScrollRect 失败：" + ex.Message); }
                if (sr != null)
                {
                    sr.verticalNormalizedPosition = 0f;
                    yield return null; yield return null;
                    try
                    {
                        string shot2 = System.IO.Path.Combine(Application.persistentDataPath, "ucmb_audio_bottom.png");
                        ScreenCapture.CaptureScreenshot(shot2);
                        Log.Info("[SELFTEST] 截图(底部) -> " + shot2);
                    }
                    catch (Exception ex) { Log.Warn("[SELFTEST] 底部截图失败：" + ex.Message); }
                }

                // 验证原版回调路径：SetValue + Apply（等价于用户操作控件）
                try
                {
                    SettingFloat hf = SettingsUi.FindVolumeSetting(MusicEvent.Hall);
                    if (hf != null)
                    {
                        hf.SetValue(37f);
                        hf.Apply();
                        Log.Info("[SELFTEST] apply 路径：hall 音量 -> runtime=" + p.VolumeOf(MusicEvent.Hall));
                        hf.SetValue(100f);
                        hf.Apply();
                        Log.Info("[SELFTEST] apply 路径：hall 音量恢复 -> runtime=" + p.VolumeOf(MusicEvent.Hall));
                    }
                }
                catch (Exception ex) { Log.Error("[SELFTEST] apply 路径验证失败：", ex); }

                try { menu.SelectTab(Setting.SettingCategory.Audio); } catch { }

                // 真实 UI 路径：直接操作下拉框组件（等价于玩家点选），确认即时生效
                yield return null;
                yield return null;
                yield return new WaitForSecondsRealtime(0.4f);
                try
                {
                    TMP_Dropdown real = null;
                    FieldInfo rf = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings");
                    List<GameObject> rows2 = rf == null ? null : rf.GetValue(menu) as List<GameObject>;
                    if (rows2 != null)
                        for (int i = 0; i < rows2.Count; i++)
                        {
                            if (rows2[i] == null || rows2[i].transform.childCount < 2) continue;
                            TMP_Dropdown d = rows2[i].transform.GetChild(1).GetComponent<TMP_Dropdown>();
                            if (d != null) { real = d; break; }
                        }
                    if (real != null)
                    {
                        Log.Info("[SELFTEST] UI 下拉框 options=" + real.options.Count + " value=" + real.value);
                        if (real.options.Count > 1)
                        {
                            real.value = 1;                 // 触发 onValueChanged -> SetValue + Apply
                        }
                    }
                    else Log.Warn("[SELFTEST] 未找到音乐盒下拉框组件");
                }
                catch (Exception ex) { Log.Error("[SELFTEST] UI 下拉框验证失败：", ex); }
                yield return null;
                Log.Info("[SELFTEST] 点选后 box=" + (p.MusicBox ?? "游戏默认") + " voices=" + p.Engine.VoiceCount);
                try
                {
                    TMP_Dropdown back = null;
                    List<GameObject> rows3 = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings")
                        .GetValue(menu) as List<GameObject>;
                    if (rows3 != null)
                        for (int i = 0; i < rows3.Count; i++)
                        {
                            if (rows3[i] == null || rows3[i].transform.childCount < 2) continue;
                            TMP_Dropdown d = rows3[i].transform.GetChild(1).GetComponent<TMP_Dropdown>();
                            if (d != null) { back = d; break; }
                        }
                    if (back != null) back.value = 0;
                }
                catch (Exception ex) { Log.Warn("[SELFTEST] 点回默认失败：" + ex.Message); }
                yield return null;
                Log.Info("[SELFTEST] 点回默认后 box=" + (p.MusicBox ?? "游戏默认") + " voices=" + p.Engine.VoiceCount);

                // 滚动位置：切到音频页后应停在顶部（音乐盒可见）
                try
                {
                    ScrollRect sr2 = null;
                    ScrollRect[] srs = menu.GetComponentsInChildren<ScrollRect>(true);
                    for (int i = 0; i < srs.Length; i++) if (srs[i].name == "Scroll") { sr2 = srs[i]; break; }
                    if (sr2 != null)
                        Log.Info("[SELFTEST] 音频页滚动位置 = " + sr2.verticalNormalizedPosition.ToString("F2") + "（预期 1.00）");
                }
                catch (Exception ex) { Log.Warn("[SELFTEST] 滚动位置读取失败：" + ex.Message); }
                yield return new WaitForSecondsRealtime(0.6f);
                try
                {
                    string shotTop = System.IO.Path.Combine(Application.persistentDataPath, "ucmb_audio_top.png");
                    ScreenCapture.CaptureScreenshot(shotTop);
                    Log.Info("[SELFTEST] 截图(顶部) -> " + shotTop);
                }
                catch (Exception ex) { Log.Warn("[SELFTEST] 顶部截图失败：" + ex.Message); }
            }

            // 4) 即时生效验证：直接改 Setting 值，靠轮询生效（模拟 UI 拖动）
            string box = p.Library.Boxes.Count > 0 ? p.Library.Boxes[0] : null;
            Log.Info("[SELFTEST] 测试音乐盒 = " + (box == null ? "(无)" : box));
            if (box != null)
            {
                // 通过 UI 值路径选盒
                SettingDropdown sd = SettingsUi.FindBoxSetting();
                int idx = p.Library.IndexOf(box);
                if (sd != null && idx > 0)
                {
                    sd.SetValue(idx);   // 只改值，不 Apply —— 验证轮询兜底
                    Log.Info("[SELFTEST] 已把 Setting.value 改为 " + idx + "（不调用 Apply）");
                }
                float wait = Time.unscaledTime;
                while (Time.unscaledTime - wait < 0.6f) yield return null;
                Log.Info("[SELFTEST] 0.6s 后（轮询）box=" + (p.MusicBox ?? "游戏默认")
                    + " voices=" + p.Engine.VoiceCount + " -> " + p.Engine.DescribeVoices());

                // 音量：把 hall 音量改成 40，验证即时作用于当前音乐
                SettingFloat vf = SettingsUi.FindVolumeSetting(MusicEvent.Hall);
                if (vf != null)
                {
                    vf.SetValue(40f);
                    wait = Time.unscaledTime;
                    while (Time.unscaledTime - wait < 0.6f) yield return null;
                    Log.Info("[SELFTEST] hall 音量改 40 后 runtime=" + p.VolumeOf(MusicEvent.Hall)
                        + " -> " + p.Engine.DescribeVoices());
                    vf.SetValue(100f);
                    wait = Time.unscaledTime;
                    while (Time.unscaledTime - wait < 0.6f) yield return null;
                }

                // 角色选择切换
                GameObject rs = PreRunScript.instance.runSettingsScreen;
                if (rs != null)
                {
                    bool old = rs.activeSelf;
                    rs.SetActive(true);
                    yield return null; yield return null;
                    Log.Info("[SELFTEST] 进入角色选择 voices=" + p.Engine.VoiceCount + " fading=" + p.Engine.FadingCount
                        + " -> " + p.Engine.DescribeVoices());
                    rs.SetActive(old);
                    yield return null; yield return null;
                    Log.Info("[SELFTEST] 退出角色选择 voices=" + p.Engine.VoiceCount + " -> " + p.Engine.DescribeVoices());
                }

                // 切回游戏默认
                SettingDropdown sd2 = SettingsUi.FindBoxSetting();
                if (sd2 != null) sd2.SetValue(0);
                wait = Time.unscaledTime;
                while (Time.unscaledTime - wait < 0.6f) yield return null;
                bool menuPlaying = false;
                try { menuPlaying = PreRunScript.instance.menuSource != null && PreRunScript.instance.menuSource.isPlaying; } catch { }
                Log.Info("[SELFTEST] 切回默认 voices=" + p.Engine.VoiceCount + " fading=" + p.Engine.FadingCount
                    + " menuPlaying=" + menuPlaying);
                wait = Time.unscaledTime;
                while (Time.unscaledTime - wait < 3.6f) yield return null;
                Log.Info("[SELFTEST] 3.6s 后 voices=" + p.Engine.VoiceCount + " fading=" + p.Engine.FadingCount);
            }

            // 5) 文件解析
            if (box != null)
            {
                for (int i = 0; i < EventDef.All.Length; i++)
                {
                    EventDef def = EventDef.All[i];
                    for (int f = 0; f < def.Files.Length; f++)
                    {
                        string why;
                        string path = p.Library.ResolveFile(box, def.Files[f], out why);
                        Log.Info("[SELFTEST] 解析 " + def.Files[f] + " -> "
                            + (path == null ? ("MISS: " + why) : System.IO.Path.GetFileName(path)));
                    }
                }
            }

            Log.Info("[SELFTEST] ====== 自检结束 ======");
            try { if (SettingsMenu.instance != null) SettingsMenu.instance.SelectTab(Setting.SettingCategory.Audio); }
            catch { }

            // 关闭设置页，截一张主菜单图，确认没有“多出来的侧栏”
            try { if (SettingsMenu.instance != null) SettingsMenu.instance.Close(); }
            catch (Exception ex) { Log.Warn("[SELFTEST] 关闭设置页失败：" + ex.Message); }
            yield return null;
            yield return null;
            try
            {
                string shot3 = System.IO.Path.Combine(Application.persistentDataPath, "ucmb_mainmenu.png");
                ScreenCapture.CaptureScreenshot(shot3);
                Log.Info("[SELFTEST] 截图(主菜单) -> " + shot3);
            }
            catch (Exception ex) { Log.Warn("[SELFTEST] 主菜单截图失败：" + ex.Message); }

            float hold = Time.unscaledTime;
            while (Time.unscaledTime - hold < 120f) yield return null;
        }

        private static GameObject FindByName(string name)
        {
            try
            {
                UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(typeof(GameObject));
                for (int i = 0; i < objs.Length; i++)
                {
                    GameObject go = objs[i] as GameObject;
                    if (go != null && go.name == name && go.activeInHierarchy) return go;
                }
            }
            catch { }
            return null;
        }
    }
}
#endif