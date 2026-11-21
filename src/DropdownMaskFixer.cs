using UnityEngine;

namespace UCMB
{
    /// <summary>
    /// 修复“下拉列表被设置页 Mask 裁剪”的问题。
    ///
    /// TMP_Dropdown 展开时只是把 Template 激活，它仍然是 Dropdown 的子物体，
    /// 于是会被设置列表 Scroll→Viewport 的 Mask 裁掉：音乐盒一多，展开后只能看到
    /// 可视区内的几项，想滚动列表还得先滚整个设置页。
    ///
    /// 这里在展开的瞬间把 Template 临时挂到根 Canvas（保持世界坐标），
    /// 收起时再挂回原父级，从而不受遮罩影响、列表可以独立滚动。
    /// 只挂在本模组自己的下拉上，不影响语言选择等原版下拉。
    /// </summary>
    internal sealed class DropdownMaskFixer : MonoBehaviour
    {
        private Transform _originalParent;
        private int _originalSibling = -1;

        internal void CaptureOrigin()
        {
            if (_originalParent != null) return;
            _originalParent = transform.parent;
            _originalSibling = transform.GetSiblingIndex();
        }

        internal void Detach()
        {
            try
            {
                CaptureOrigin();
                Canvas canvas = GetComponentInParent<Canvas>();
                if (canvas == null) return;
                Transform root = canvas.rootCanvas.transform;
                if (transform.parent == root) return;
                transform.SetParent(root, true);
                transform.SetAsLastSibling();
            }
            catch { }
        }

        internal void Reattach()
        {
            try
            {
                if (_originalParent == null) return;
                if (transform.parent == _originalParent) return;
                transform.SetParent(_originalParent, true);
                if (_originalSibling >= 0) transform.SetSiblingIndex(_originalSibling);
            }
            catch { }
        }
    }
}