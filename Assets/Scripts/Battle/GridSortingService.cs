using UnityEngine;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 2.5D 伪立体感的 Y 轴排序服务（Producer 2026-05-11 拍板：纯 2D + Y 排序）。
    ///
    /// 规则：sortingOrder = Mathf.RoundToInt(-worldY * 100)
    ///   - worldY 越下（越负）→ sortingOrder 越大 → 渲染越在前
    ///   - 与 GridMap.CellToWorld 配套（row 越大 → worldY 越负）
    ///
    /// 性能：
    ///   - lastY 缓存防抖（CLAUDE.md R-V11）：worldY 变化 &lt; 0.01 时跳过 set
    ///   - 静态方法，无单例分配
    /// </summary>
    public static class GridSortingService
    {
        private const float MIN_DELTA = 0.01f;

        /// <summary>worldY → sortingOrder（×100，便于亚像素精度）。</summary>
        public static int CalcSortingOrder(float worldY)
        {
            return Mathf.RoundToInt(-worldY * 100f);
        }

        /// <summary>
        /// 仅在 Y 显著变化时更新 SpriteRenderer.sortingOrder（性能优化）。
        /// 调用方传 lastY ref，本方法回写新值（让调用方下次接续判断）。
        /// </summary>
        public static void UpdateIfChanged(SpriteRenderer sr, ref float lastY, float curY)
        {
            if (sr == null) return;
            if (Mathf.Abs(curY - lastY) < MIN_DELTA) return;
            sr.sortingOrder = CalcSortingOrder(curY);
            lastY = curY;
        }

        /// <summary>同上，但接受 Renderer 基类（兼容 MeshRenderer / SpriteRenderer 等）。</summary>
        public static void UpdateIfChanged(Renderer r, ref float lastY, float curY)
        {
            if (r == null) return;
            if (Mathf.Abs(curY - lastY) < MIN_DELTA) return;
            r.sortingOrder = CalcSortingOrder(curY);
            lastY = curY;
        }
    }
}
