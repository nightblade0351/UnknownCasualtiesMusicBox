using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UCMB
{
    /// <summary>
    /// 一次性布局自检：首次打开原版“音频”页时，把设置界面的真实层级、滚动区尺寸
    /// 与每一行的可见性写入 BepInEx 日志。用于确认：
    ///   * 12 个音乐盒设置项是否真实渲染并落在可视区内（而不是被裁掉）；
    ///   * 是否出现了非原版的面板 / 侧栏（由其它模组或 CUCoreLib 轨道造成）。
    /// 全部只读，不修改任何 UI。
    /// </summary>
    internal static class Diag
    {
        private static bool _done;
        private static bool _requested;
        private static float _dumpAt = -1f;

        /// <summary>请求在 0.5s 后（布局稳定后）做一次性自检。</summary>
        internal static void Request()
        {
            if (_done || _requested) return;
            _requested = true;
            _dumpAt = Time.unscaledTime + 0.5f;
        }

        internal static void Tick()
        {
            if (!_requested || _done) return;
            if (Time.unscaledTime < _dumpAt) return;
            DumpAudioPage(SettingsMenu.instance);
        }

        internal static void DumpAudioPage(SettingsMenu menu)
        {
            if (_done || menu == null) return;
            _done = true;
            try
            {
                Log.Info("[DIAG] ===== 设置界面布局自检（音频页）=====");
                Log.Info("[DIAG] Screen=" + Screen.width + "x" + Screen.height
                    + " dpi=" + Screen.dpi + " fullscreen=" + Screen.fullScreen
                    + " 行高参考=" + RowHeight(menu));
                DumpRootSiblings(menu.transform);
                DumpHierarchy(menu.transform, 0, 3);
                DumpScrollAreas(menu.transform);
                DumpRows(menu);
                DumpTabs(menu);
                DumpCounts();
                Log.Info("[DIAG] ===== 自检结束 =====");
            }
            catch (Exception ex)
            {
                Log.Error("[DIAG] 布局自检异常：", ex);
            }
        }

        /// <summary>列出设置菜单所在 Canvas 根节点下的全部一级对象，用于发现“多出来的侧栏/面板”。</summary>
        private static void DumpRootSiblings(Transform menuTransform)
        {
            try
            {
                Canvas canvas = menuTransform.GetComponentInParent<Canvas>();
                Transform root = canvas == null ? menuTransform.root : canvas.rootCanvas.transform;
                Log.Info("[DIAG] Canvas 根=" + root.name + " 一级子对象 " + root.childCount + " 个：");
                for (int i = 0; i < root.childCount; i++)
                {
                    Transform c = root.GetChild(i);
                    RectTransform rt = c as RectTransform;
                    Log.Info("[DIAG]   " + (i + 1) + ". " + c.name
                        + (c.gameObject.activeSelf ? "" : "(inactive)")
                        + (rt == null ? "" : " size=" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0")));
                }
                Transform p = menuTransform.parent;
                if (p != null)
                {
                    Log.Info("[DIAG] 设置菜单父级=" + p.name + " 子对象 " + p.childCount + " 个：");
                    for (int i = 0; i < p.childCount; i++)
                        Log.Info("[DIAG]   - " + p.GetChild(i).name + (p.GetChild(i).gameObject.activeSelf ? "" : "(inactive)"));
                }
            }
            catch (Exception ex) { Log.Warn("[DIAG] 根节点枚举失败：" + ex.Message); }
        }

        private static void DumpHierarchy(Transform t, int depth, int maxDepth)
        {
            if (t == null) return;
            StringBuilder sb = new StringBuilder();
            sb.Append("[DIAG] ");
            sb.Append(' ', depth * 2);
            sb.Append(t.name);
            sb.Append(t.gameObject.activeSelf ? "" : "(inactive)");
            RectTransform rt = t as RectTransform;
            if (rt != null)
            {
                Vector3[] c = new Vector3[4];
                rt.GetWorldCorners(c);
                sb.Append(" size=").Append(rt.rect.width.ToString("F0")).Append('x').Append(rt.rect.height.ToString("F0"));
                sb.Append(" local=").Append(rt.anchoredPosition.x.ToString("F0")).Append(',').Append(rt.anchoredPosition.y.ToString("F0"));
                sb.Append(" world=(").Append(c[0].x.ToString("F0")).Append(',').Append(c[0].y.ToString("F0"))
                  .Append(")-(").Append(c[2].x.ToString("F0")).Append(',').Append(c[2].y.ToString("F0")).Append(')');
            }
            List<string> comps = new List<string>();
            Component[] all = t.GetComponents<Component>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                string n = all[i].GetType().Name;
                if (n == "Transform" || n == "RectTransform") continue;
                if (all[i] is TextMeshProUGUI)
                {
                    string txt = ((TextMeshProUGUI)all[i]).text;
                    if (txt != null && txt.Length > 24) txt = txt.Substring(0, 24) + "…";
                    n += "(\"" + txt + "\")";
                }
                comps.Add(n);
            }
            sb.Append(" [").Append(string.Join(", ", comps.ToArray())).Append(']');
            Log.Info(sb.ToString());

            if (depth >= maxDepth) return;
            for (int i = 0; i < t.childCount; i++) DumpHierarchy(t.GetChild(i), depth + 1, maxDepth);
        }

        private static void DumpScrollAreas(Transform root)
        {
            ScrollRect[] scrolls = root.GetComponentsInChildren<ScrollRect>(true);
            Log.Info("[DIAG] ScrollRect 数量 = " + scrolls.Length);
            for (int i = 0; i < scrolls.Length; i++)
            {
                ScrollRect s = scrolls[i];
                string path = PathOf(s.transform, root);
                string viewport = s.viewport == null ? "(null)" : RectOf(s.viewport);
                string content = s.content == null ? "(null)" : RectOf(s.content);
                string vbar = s.verticalScrollbar == null ? "(null)" : s.verticalScrollbar.name
                    + (s.verticalScrollbar.gameObject.activeSelf ? "[显示]" : "[隐藏]");
                Log.Info("[DIAG]   ScrollRect " + path + " viewport=" + viewport + " content=" + content
                    + " verticalScrollbar=" + vbar + " vertical=" + s.vertical);
            }
            Mask[] masks = root.GetComponentsInChildren<Mask>(true);
            RectMask2D[] rmasks = root.GetComponentsInChildren<RectMask2D>(true);
            Log.Info("[DIAG] Mask=" + masks.Length + " RectMask2D=" + rmasks.Length);
        }

        private static void DumpRows(SettingsMenu menu)
        {
            FieldInfo fi = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings");
            if (fi == null) { Log.Warn("[DIAG] 未找到 spawnedSettings 字段"); return; }
            List<GameObject> rows = fi.GetValue(menu) as List<GameObject>;
            if (rows == null) { Log.Info("[DIAG] spawnedSettings = null"); return; }

            // 找到设置内容所在的可视矩形（ScrollRect.viewport 或 content 的父级 Mask）
            Rect view = new Rect(-100000f, -100000f, 200000f, 200000f);
            ScrollRect[] scrolls = menu.GetComponentsInChildren<ScrollRect>(true);
            for (int i = 0; i < scrolls.Length; i++)
            {
                if (scrolls[i].viewport == null) continue;
                if (scrolls[i].name == "RailViewport" || scrolls[i].name.IndexOf("Rail", StringComparison.Ordinal) >= 0) continue;
                view = WorldRect(scrolls[i].viewport);
                break;
            }

            Log.Info("[DIAG] 行数 = " + rows.Count + " 可视区=" + view.yMin.ToString("F0") + ".." + view.yMax.ToString("F0"));
            for (int i = 0; i < rows.Count; i++)
            {
                GameObject go = rows[i];
                if (go == null) { Log.Info("[DIAG]   行" + i + " = (null)"); continue; }
                RectTransform rt = go.transform as RectTransform;
                Rect r = rt != null ? WorldRect(rt) : new Rect();
                bool inside = r.yMin >= view.yMin - 1f && r.yMax <= view.yMax + 1f;
                Log.Info("[DIAG]   行" + i + " " + go.name
                    + (go.activeSelf ? "" : "(inactive)")
                    + " y=" + r.yMin.ToString("F0") + ".." + r.yMax.ToString("F0")
                    + " 可视=" + (inside ? "是" : "否"));
            }
        }

        private static void DumpTabs(SettingsMenu menu)
        {
            FieldInfo fi = AccessTools.Field(typeof(SettingsMenu), "buttons");
            if (fi == null) return;
            List<Button> buttons = fi.GetValue(menu) as List<Button>;
            if (buttons == null) { Log.Info("[DIAG] buttons = null"); return; }
            Log.Info("[DIAG] 标签按钮数 = " + buttons.Count);
            for (int i = 0; i < buttons.Count; i++)
            {
                Button b = buttons[i];
                if (b == null) { Log.Info("[DIAG]   标签" + i + " = (null)"); continue; }
                TextMeshProUGUI txt = b.GetComponentInChildren<TextMeshProUGUI>(true);
                Log.Info("[DIAG]   标签" + i + " '" + b.name + "' = '" + (txt == null ? "?" : txt.text) + "'");
            }
        }

        private static void DumpCounts()
        {
            List<Setting> all = Settings.GetAllSettings();
            if (all == null) return;
            Dictionary<int, int> per = new Dictionary<int, int>();
            int mine = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Setting s = all[i];
                if (s == null) continue;
                int c = (int)s.category;
                if (!per.ContainsKey(c)) per[c] = 0;
                per[c] = per[c] + 1;
                if (s.name != null && s.name.StartsWith(MusicBoxPlugin.ModIdPrefix, StringComparison.Ordinal)) mine++;
            }
            StringBuilder sb = new StringBuilder("[DIAG] 设置总数 = " + all.Count + "，本模组 = " + mine + "，按页：");
            foreach (KeyValuePair<int, int> kv in per) sb.Append(" [" + kv.Key + "]=" + kv.Value);
            Log.Info(sb.ToString());

            // 重复项检查：同名设置项若出现多次，会出现“两个音乐盒”之类的现象
            Dictionary<string, int> seen = new Dictionary<string, int>();
            for (int i = 0; i < all.Count; i++)
            {
                Setting s = all[i];
                if (s == null || s.name == null) continue;
                if (!seen.ContainsKey(s.name)) seen[s.name] = 0;
                seen[s.name] = seen[s.name] + 1;
            }
            int dup = 0;
            foreach (KeyValuePair<string, int> kv in seen)
            {
                if (kv.Value <= 1) continue;
                dup++;
                Log.Warn("[DIAG] 重复设置项：" + kv.Key + " ×" + kv.Value);
            }
            Log.Info("[DIAG] 重复设置项数量 = " + dup + "（音乐盒应为 1 个：ucmb.box）");
        }

        private static Rect WorldRect(RectTransform rt)
        {
            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);
            float xMin = Mathf.Min(Mathf.Min(c[0].x, c[1].x), Mathf.Min(c[2].x, c[3].x));
            float xMax = Mathf.Max(Mathf.Max(c[0].x, c[1].x), Mathf.Max(c[2].x, c[3].x));
            float yMin = Mathf.Min(Mathf.Min(c[0].y, c[1].y), Mathf.Min(c[2].y, c[3].y));
            float yMax = Mathf.Max(Mathf.Max(c[0].y, c[1].y), Mathf.Max(c[2].y, c[3].y));
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static string RowHeight(SettingsMenu menu)
        {
            try
            {
                List<GameObject> rows = AccessTools.Field(typeof(SettingsMenu), "spawnedSettings")
                    .GetValue(menu) as List<GameObject>;
                if (rows == null || rows.Count == 0 || rows[0] == null) return "?";
                RectTransform rt = rows[0].transform as RectTransform;
                return rt == null ? "?" : rt.sizeDelta.y.ToString("F1");
            }
            catch { return "?"; }
        }

        private static string RectOf(RectTransform rt)
        {
            Rect r = WorldRect(rt);
            return r.xMin.ToString("F0") + "," + r.yMin.ToString("F0") + " " + r.width.ToString("F0") + "x" + r.height.ToString("F0");
        }

        private static string PathOf(Transform t, Transform root)
        {
            if (t == null) return "?";
            string p = t.name;
            Transform cur = t.parent;
            int guard = 0;
            while (cur != null && cur != root && guard++ < 12)
            {
                p = cur.name + "/" + p;
                cur = cur.parent;
            }
            return p;
        }
    }
}