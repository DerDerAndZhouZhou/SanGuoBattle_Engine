using UnityEngine;

namespace HeroDefense.Utils
{
    /// <summary>
    /// cycle 5 step 12：UI px ↔ World 米 换算工具（PPU=100）。
    /// 设计基准 720×1280 屏，相机视野 9:16 aspect。
    /// 控件位置按 UI px 设计（左上锚 / sizeDelta），用本工具换算到 World 米单位（屏幕中心为原点）。
    /// </summary>
    public static class PxToWorld
    {
        public const float PPU = 100f;
        public const float REF_SCREEN_WIDTH = 720f;
        public const float REF_SCREEN_HEIGHT = 1280f;

        public static float Px2M(float px) => px / PPU;
        public static float M2Px(float meter) => meter * PPU;

        /// <summary>
        /// UI 屏幕坐标（左下原点，px）→ World 坐标（屏幕中心原点，米）。
        /// 用于把 UI 锚点设计的位置转 World transform.position。
        /// </summary>
        public static Vector2 ScreenPxToWorld(float px_x, float px_y)
        {
            return new Vector2(
                (px_x - REF_SCREEN_WIDTH * 0.5f) / PPU,
                (px_y - REF_SCREEN_HEIGHT * 0.5f) / PPU
            );
        }

        /// <summary>
        /// 屏幕区域中心（左上锚定 + size + offset）→ World 坐标。
        /// 例：GridWindow 设计 sizeDelta=(176, 240), 距左 20, 距顶 120
        ///     → 屏幕左下原点中心 (20+88, 1280-120-120) = (108, 1040)
        ///     → World (-2.52, 4.0)
        /// </summary>
        public static Vector2 TopLeftAnchoredCenterToWorld(float pxOffsetX, float pxOffsetY, float sizeW, float sizeH)
        {
            float screenCenterX = pxOffsetX + sizeW * 0.5f;
            float screenCenterY = REF_SCREEN_HEIGHT - pxOffsetY - sizeH * 0.5f;
            return ScreenPxToWorld(screenCenterX, screenCenterY);
        }
    }
}
