using System.Collections;
using UnityEngine;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 单个网格 cell 的视图组件。挂在 GameScene/GridContainer 下每个 cell GameObject 上。
    /// 编辑器中预先 spawn 112 个（8×14）保存进场景，运行时 GridMap.InitFromScene 读取。
    ///
    /// 0 [SerializeField]：row / col 由 Editor 工具脚本一次性写入；运行时只读。
    /// 单一职责：存网格坐标 + 状态 + 切高亮 sprite，**不写业务逻辑**（业务由 Lua 决定）。
    /// </summary>
    public class CellView : MonoBehaviour
    {
        public int Row;
        public int Col;
        public bool IsCamp;          // (5,1)(5,2)(6,1)(6,2) 营帐 4 格

        // T202 (2026-05-21)：玩家可见性样式，由 gameplay_mode.cell_visual_style 配置（R1d 后逐格解锁已删，仅留视觉样式）
        //   "transparent"    = mode 1 普通模式 cell 全透明（locked/unlocked 都看不见）
        //   "unlocked_shown" = mode 2/3/4 unlocked alpha=0.35 显出，locked 透明，unlocking 0→0.35 渐变
        public string VisualStyle = "transparent";
        const float UnlockedAlpha = 0.35f;

        public enum HL { None, Yellow, DeepYellow, Green, Red, Grey }
        public HL Highlight;

        SpriteRenderer _sr;
        float _visualCellW;
        float _visualCellH;

        // v4 2026-05-14：cell 默认 sprite 在 Game/resources/art/ui/hud/cell_unlocked.png（CDN 热更目录），
        // 2026-05-20 路径随 UI 子目录重组迁入 hud/。
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

        // cell 目标世界大小：grid.tab cell_w/cell_h（默认 1.28×0.96），R1a 经 GridMap.CellSizeX/Y 配置化。
        // 注：Awake 阶段若 grid.tab 尚未加载，GridMap 静态默认即 1.28×0.96（真实值），不会落到 0；
        // 战斗实际 cell 是编辑器预摆，位置真相走 transform.position，本缩放仅决定单格视觉大小。
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
            // 拖拽高亮覆盖 base（拖拽结束后 SetHighlight(None) 会回 RefreshBase）。
            // 高亮只显示透明中心的边框 PNG，不再整块半透明填色，避免压脏战场地面。
            EnsureHighlightSprites();
            Sprite target = null;
            switch (state)
            {
                case HL.None:       RefreshBase(); return;
                case HL.Yellow:     target = _highlightYellowCache; break;
                case HL.DeepYellow: target = _highlightGreenCache; break;  // 块1.5 D1.5：升级提示并入绿（升级小图标留 R4 art）
                case HL.Green:      target = _highlightGreenCache; break;
                case HL.Red:        target = _highlightRedCache; break;
                case HL.Grey:       target = _highlightRedCache; break;
            }
            if (target != null) _sr.sprite = target;
            _sr.color = Color.white;
            EnsureCellScale();
        }

        // T202：根据 VisualStyle + state 决定底图 alpha
        //   transparent：永远 alpha=0
        //   unlocked_shown：locked=0 / unlocked=0.35 / unlocking 由 StartUnlockAnim 接管
        public void RefreshBase()
        {
            if (_sr == null) _sr = GetComponent<SpriteRenderer>();
            if (_sr == null) return;
            EnsureDefaultSprite();
            if (_defaultSpriteCache != null) _sr.sprite = _defaultSpriteCache;
            EnsureCellScale();
            if (IsCamp) { _sr.color = new Color(1f, 1f, 1f, 0f); return; }
            // 2026-06-11 用户验收否决 F5 三区常驻淡色：任何势力区域 cell 都不显底图 → 回退 VisualStyle-only。
            // 区域辨识只靠拖拽时 bbox 绿/红 + 移动路径黄（块1.5 D1.5）。
            float a = (VisualStyle == "unlocked_shown") ? UnlockedAlpha : 0f;
            _sr.color = new Color(1f, 1f, 1f, a);
        }

        // T202：玩法模式切视觉样式，由 Battle_SetGridVisualStyle 批量推
        public void SetVisualStyle(string style)
        {
            VisualStyle = string.IsNullOrEmpty(style) ? "transparent" : style;
            if (Highlight == HL.None) RefreshBase();   // 拖拽中不打断高亮显示
        }

    }
}
