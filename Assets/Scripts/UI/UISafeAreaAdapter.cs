using UnityEngine;

namespace HeroDefense.UI
{
    /// <summary>
    /// cycle 4 step C2：刘海屏 + Home Indicator 自动适配。
    /// 挂在 mainWindow（RectTransform full-stretch 子）上，运行时按 Screen.safeArea 调整 anchorMin/Max
    /// 把 mainWindow 整体 padding 到安全区域，所有子节点（MainTopBar / TabPanel / TabBar / RightSideBar）
    /// 通过 anchor 系统自动跟随。BG 在 World 空间不受影响，仍全屏渲染（露在刘海下方做装饰）。
    /// 监测 safeArea/screen 变化（旋转 / 模拟器机型切换），变化时重算。
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class UISafeAreaAdapter : MonoBehaviour
    {
        RectTransform _rt;
        Rect _lastSafeArea;
        Vector2Int _lastScreen;
        ScreenOrientation _lastOrientation;

        void Awake()
        {
            _rt = GetComponent<RectTransform>();
            Apply();
        }

        void Update()
        {
            if (Screen.safeArea != _lastSafeArea
                || new Vector2Int(Screen.width, Screen.height) != _lastScreen
                || Screen.orientation != _lastOrientation)
            {
                Apply();
            }
        }

        void Apply()
        {
            if (Screen.width <= 0 || Screen.height <= 0) return;
            var sa = Screen.safeArea;
            _lastSafeArea = sa;
            _lastScreen = new Vector2Int(Screen.width, Screen.height);
            _lastOrientation = Screen.orientation;

            Vector2 anchorMin = new Vector2(sa.x / Screen.width, sa.y / Screen.height);
            Vector2 anchorMax = new Vector2((sa.x + sa.width) / Screen.width, (sa.y + sa.height) / Screen.height);
            _rt.anchorMin = anchorMin;
            _rt.anchorMax = anchorMax;
            _rt.offsetMin = Vector2.zero;
            _rt.offsetMax = Vector2.zero;
        }
    }
}
