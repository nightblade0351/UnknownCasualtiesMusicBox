using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UCMB
{
    /// <summary>
    /// 自建“音乐盒选择面板”。
    ///
    /// 为什么不用 TMP_Dropdown 的展开列表：
    /// 它展开时会克隆模板、按模板高度定位，条目一多就只露出固定行数，
    /// 而且 Mask/Viewport/ScrollRect 的尺寸互相牵制，外部改高度会被布局覆盖，
    /// 结果是“看不到全部、也滚不动”。
    ///
    /// 这里直接构建：全屏遮罩 + 居中面板 + VerticalLayoutGroup + ScrollRect，
    /// 高度与滚动范围完全由我们决定，条目用原版字体，视觉上仍是原生风格。
    /// </summary>
    internal static class MusicBoxSelector
    {
        private static GameObject _root;

        internal static bool IsOpen { get { return _root != null; } }

        internal static void Open(TMP_Dropdown dd)
        {
            Close();
            if (dd == null) return;
            try
            {
                Canvas canvas = dd.GetComponentInParent<Canvas>();
                if (canvas == null) return;
                canvas = canvas.rootCanvas;
                _root = Build(canvas, dd);
                Log.Info("已打开音乐盒选择面板（选项 " + dd.options.Count + " 项）");
            }
            catch (Exception ex)
            {
                Log.Error("打开音乐盒选择面板失败：", ex);
                Close();
            }
        }

        internal static void Close()
        {
            if (_root != null)
            {
                try { UnityEngine.Object.Destroy(_root); } catch { }
            }
            _root = null;
        }

        internal static void Tick()
        {
            // 关闭方式：点击背景 / 选中条目。
            // 刻意不读 Input，避免额外引入 UnityEngine.InputLegacyModule 依赖。
        }

        // -----------------------------------------------------------------
        private static GameObject Build(Canvas canvas, TMP_Dropdown dd)
        {
            TMP_FontAsset font = dd.captionText != null && dd.captionText.font != null
                ? dd.captionText.font : TMP_Settings.defaultFontAsset;

            GameObject root = NewRect("UC-MB-Selector", canvas.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // 全屏遮罩（点击关闭；同时吞掉滚轮，避免带动背后设置页）
            GameObject backdrop = NewRect("Backdrop", root.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            Image bgImg = backdrop.AddComponent<Image>();
            bgImg.color = new Color(0f, 0f, 0f, 0.72f);
            Button bgBtn = backdrop.AddComponent<Button>();
            bgBtn.transition = Selectable.Transition.None;
            bgBtn.onClick.AddListener(Close);
            backdrop.AddComponent<EmptyScrollSink>();

            // 居中面板
            float panelW = Mathf.Min(760f, Screen.width * 0.48f);
            float panelH = Mathf.Min(760f, Screen.height * 0.78f);
            GameObject panel = NewRect("Panel", root.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(panelW, panelH), Vector2.zero);
            Image panelImg = panel.AddComponent<Image>();
            panelImg.color = new Color(0.07f, 0.07f, 0.10f, 0.98f);

            // 标题：贴顶、水平铺满
            GameObject title = NewRect("Title", panel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                Vector2.zero, Vector2.zero);
            RectTransform titleRt = title.GetComponent<RectTransform>();
            titleRt.offsetMin = new Vector2(16f, -56f);
            titleRt.offsetMax = new Vector2(-16f, 0f);
            TextMeshProUGUI titleTxt = title.AddComponent<TextMeshProUGUI>();
            if (font != null) titleTxt.font = font;
            titleTxt.text = "音乐盒";
            titleTxt.fontSize = 32;
            titleTxt.alignment = TextAlignmentOptions.Center;
            titleTxt.color = Color.white;

            // 滚动区：铺满标题以下的部分（用 offset 而非 sizeDelta，避免 stretch 节点位置偏移）
            GameObject viewport = NewRect("Viewport", panel.transform, Vector2.zero, Vector2.one,
                Vector2.zero, Vector2.zero);
            RectTransform vpRt = viewport.GetComponent<RectTransform>();
            vpRt.offsetMin = new Vector2(16f, 16f);
            vpRt.offsetMax = new Vector2(-16f, -64f);
            RectMask2D mask = viewport.AddComponent<RectMask2D>();
            Image vpImg = viewport.AddComponent<Image>();
            vpImg.color = new Color(0f, 0f, 0f, 0.01f);   // 需要 Graphic 才能接收拖拽

            GameObject content = NewRect("Content", viewport.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), Vector2.zero);
            RectTransform contentRt = content.GetComponent<RectTransform>();
            contentRt.pivot = new Vector2(0.5f, 1f);   // 从顶部开始往下排，ScrollRect 滚动方向才正确
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.offsetMin = Vector2.zero;
            contentRt.offsetMax = Vector2.zero;
            VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 2f;
            vlg.padding = new RectOffset(6, 6, 6, 6);
            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            ScrollRect sr = panel.AddComponent<ScrollRect>();
            sr.viewport = vpRt;
            sr.content = contentRt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 45f;
            sr.inertia = false;

            // 条目
            int current = Mathf.Clamp(dd.value, 0, Mathf.Max(0, dd.options.Count - 1));
            for (int i = 0; i < dd.options.Count; i++)
            {
                BuildItem(content.transform, dd, i, i == current, font);
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRt);
            return root;
        }

        private static void BuildItem(Transform parent, TMP_Dropdown dd, int index, bool selected, TMP_FontAsset font)
        {
            GameObject item = NewRect("Item" + index, parent, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 52f), Vector2.zero);

            LayoutElement le = item.AddComponent<LayoutElement>();
            le.minHeight = 52f;
            le.preferredHeight = 52f;
            le.flexibleHeight = 0f;

            Image img = item.AddComponent<Image>();
            img.color = selected ? new Color(1f, 0.85f, 0.35f, 0.16f) : new Color(1f, 1f, 1f, 0.03f);

            Button btn = item.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.ColorTint;
            ColorBlock cb = btn.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1f, 1f, 1f, 0.9f);
            cb.pressedColor = new Color(0.9f, 0.9f, 0.9f, 0.9f);
            cb.colorMultiplier = 1f;
            btn.colors = cb;

            int captured = index;
            btn.onClick.AddListener(delegate { Pick(dd, captured); });

            GameObject label = NewRect("Label", item.transform, Vector2.zero, Vector2.one,
                new Vector2(-24f, 0f), new Vector2(16f, 0f));
            TextMeshProUGUI txt = label.AddComponent<TextMeshProUGUI>();
            if (font != null) txt.font = font;
            txt.text = dd.options[index].text;
            txt.fontSize = 26;
            txt.alignment = TextAlignmentOptions.MidlineLeft;
            txt.enableWordWrapping = false;
            txt.overflowMode = TextOverflowModes.Ellipsis;   // 长名字不撑破面板
            txt.color = selected ? new Color(1f, 0.88f, 0.45f) : Color.white;
            txt.raycastTarget = false;
        }

        private static void Pick(TMP_Dropdown dd, int index)
        {
            try
            {
                if (dd != null && index >= 0 && index < dd.options.Count) dd.value = index;
            }
            catch (Exception ex) { Log.Error("应用音乐盒选择失败：", ex); }
            finally { Close(); }
        }

        private static GameObject NewRect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 sizeDelta, Vector2 anchoredPos)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = anchoredPos;
            return go;
        }
    }

    /// <summary>吞掉滚轮/点击，避免操作选择面板时带动背后的设置页。</summary>
    internal sealed class EmptyScrollSink : MonoBehaviour, IScrollHandler
    {
        public void OnScroll(PointerEventData eventData) { }
    }

    /// <summary>标记：这是“音乐盒”下拉，展开时改用自建面板。</summary>
    internal sealed class MusicBoxDropdownTag : MonoBehaviour { }
}