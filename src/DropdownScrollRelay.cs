using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UCMB
{
    /// <summary>
    /// 让鼠标滚轮真正滚“下拉列表本身”，而不是滚动背后的设置页。
    ///
    /// 原因：TMP_Dropdown 的 Template 里虽然有 ScrollRect，但鼠标滚轮事件是
    /// 从指针下的对象向上冒泡、只交给第一个 IScrollHandler。列表项常常先命中
    /// 上层容器（设置页的 Scroll），于是滚轮被设置页吃掉。
    ///
    /// 这里把组件挂在列表的 Viewport 上（比 Template 更靠内），确保滚轮先到这里，
    /// 再显式转发给列表自己的 ScrollRect。
    /// </summary>
    internal sealed class DropdownScrollRelay : MonoBehaviour, IScrollHandler
    {
        internal ScrollRect Target;

        public void OnScroll(PointerEventData eventData)
        {
            try
            {
                // 先交给 ScrollRect 自己处理（正常情况下它就能滚）；
                // 再补一次“手动位移”兜底：某些 TMP 模板里 ScrollRect 的 content
                // 或 viewport 引用不完整，OnScroll 会静默无操作。
                if (Target != null && Target.isActiveAndEnabled) Target.OnScroll(eventData);

                RectTransform content = Target != null ? Target.content : null;
                RectTransform view = (Target != null && Target.viewport != null)
                    ? Target.viewport : (transform as RectTransform);
                if (content == null || view == null) return;

                float contentH = content.rect.height;
                float viewH = view.rect.height;
                float max = Mathf.Max(0f, contentH - viewH);
                if (max <= 0.5f) return;

                float step = eventData.scrollDelta.y * 60f;
                Vector2 p = content.anchoredPosition;
                float y = Mathf.Clamp(p.y - step, 0f, max);
                if (Mathf.Abs(y - p.y) > 0.01f)
                {
                    p.y = y;
                    content.anchoredPosition = p;
                }
            }
            catch { }
        }
    }
}