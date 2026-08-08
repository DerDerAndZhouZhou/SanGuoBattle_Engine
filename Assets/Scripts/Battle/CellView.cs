using UnityEngine;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 单个网格 cell 的视图组件。运行时由正式 Scene3D 布局或 Scene2D 兜底布局生成，
    /// 当前正式战场为 14 行 × 5 列。
    ///
    /// 0 [SerializeField]：row / col 由 Editor 工具脚本一次性写入；运行时只读。
    /// 单一职责：存网格坐标 + 状态 + 切高亮 sprite，**不写业务逻辑**（业务由 Lua 决定）。
    /// </summary>
    public class CellView : MonoBehaviour
    {
        public int Row;
        public int Col;

        public enum HL { None, Yellow, DeepYellow, Green, Red, Grey }
        public HL Highlight;

        SpriteRenderer _sr;
        float _visualCellW;
        float _visualCellH;

        // cell 默认 sprite 位于 Game/resources/art/ui/hud/（CDN 热更目录）。
        const string DefaultCellSpritePath = "resources/art/ui/hud/cell_unlocked.png";
        const string HighlightYellowSpritePath = "resources/art/ui/grid/grid_hl_yellow.png";
        const string HighlightGreenSpritePath = "resources/art/ui/grid/grid_hl_green.png";
        const string HighlightRedSpritePath = "resources/art/ui/grid/grid_hl_red.png";
        static Sprite _defaultSpriteCache;
        static Sprite _highlightYellowCache;
        static Sprite _highlightGreenCache;
        static Sprite _highlightRedCache;

        void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            EnsureDefaultSprite();
            EnsureHighlightSprites();
            EnsureCellScale();
        }

        void EnsureDefaultSprite()
        {
            if (_sr == null) return;
            if (_defaultSpriteCache == null)
            {
                try { _defaultSpriteCache = Engine.Host.ResourceHost.LoadSprite(DefaultCellSpritePath); }
                catch { }
            }
            if (_sr.sprite == null && _defaultSpriteCache != null) _sr.sprite = _defaultSpriteCache;
        }

        void EnsureHighlightSprites()
        {
            if (_highlightYellowCache == null)
            {
                try { _highlightYellowCache = Engine.Host.ResourceHost.LoadSprite(HighlightYellowSpritePath, logMissing: false); }
                catch { }
            }
            if (_highlightGreenCache == null)
            {
                try { _highlightGreenCache = Engine.Host.ResourceHost.LoadSprite(HighlightGreenSpritePath, logMissing: false); }
                catch { }
            }
            if (_highlightRedCache == null)
            {
                try { _highlightRedCache = Engine.Host.ResourceHost.LoadSprite(HighlightRedSpritePath, logMissing: false); }
                catch { }
            }
        }

        // cell 目标世界大小：grid.tab cell_w/cell_h（当前默认 1.08×0.92）。
        // Awake 阶段若 grid.tab 尚未加载，使用 GridMap 静态默认；本缩放仅决定单格视觉大小。
        void EnsureCellScale()
        {
            if (_sr == null || _sr.sprite == null) return;
            var sz = _sr.sprite.bounds.size;
            if (sz.x <= 0 || sz.y <= 0) return;
            float targetW = _visualCellW > 0.0001f ? _visualCellW : GridMap.CellSizeX;
            float targetH = _visualCellH > 0.0001f ? _visualCellH : GridMap.CellSizeY;
            transform.localScale = new Vector3(targetW / sz.x, targetH / sz.y, 1f);
        }

        // 45 度战场透视样板：GridMap 可按行给单格不同的视觉宽度。
        // 逻辑坐标不变，仅影响 SpriteRenderer 的世界显示尺寸。
        public void SetVisualCellSize(float width, float height)
        {
            _visualCellW = Mathf.Max(0f, width);
            _visualCellH = Mathf.Max(0f, height);
            EnsureCellScale();
        }

        public void SetHighlight(HL state)
        {
            Highlight = state;
            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            if (_sr == null) return;
            // 交互高亮覆盖透明基底；SetHighlight(None) 恢复基底。
            // 高亮只显示透明中心的边框 PNG，不再整块半透明填色，避免压脏战场地面。
            EnsureHighlightSprites();
            Sprite target = null;
            switch (state)
            {
                case HL.None:       RefreshBase(); return;
                case HL.Yellow:     target = _highlightYellowCache; break;
                case HL.DeepYellow: target = _highlightGreenCache; break;
                case HL.Green:      target = _highlightGreenCache; break;
                case HL.Red:        target = _highlightRedCache; break;
                case HL.Grey:       target = _highlightRedCache; break;
            }
            if (target != null) _sr.sprite = target;
            _sr.color = Color.white;
            EnsureCellScale();
        }

        // 正式地块由 Scene3D 视觉层绘制，CellView 本身只承载透明交互层。
        public void RefreshBase()
        {
            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            if (_sr == null) return;
            EnsureDefaultSprite();
            if (_defaultSpriteCache != null) _sr.sprite = _defaultSpriteCache;
            EnsureCellScale();
            _sr.color = new Color(1f, 1f, 1f, 0f);
        }

    }
}
