using UnityEngine;
using HeroDefense.Engine.Host;
using HeroDefense.Config;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 基地城墙显示控制（挂在 GameScene/CampVisual 上）。v6 双基地重构（2026-06-02）。
    ///
    /// 左基地（玩家城墙）：本节点的 SpriteRenderer。占满最左列(LeftBaseCol)的 8 个网格高度，
    ///   按 Lua Camp_State.hp/max_hp 切 4 张 sprite（full/mid/low/destroyed）。点击 → Panel_CampDetail。
    /// 右基地（敌方城墙）：运行时创建的兄弟节点 RightBaseWall。占满最右列(RightBaseCol)的 8 个网格高度，
    ///   仅占位 + 视觉（无 HP）；显隐由 Lua 全局 Battle_ShowRightBase 控制（PvE 隐 / PVP 显）。
    ///
    /// 定位/缩放：从 GridMap.CellToWorld 取该列 row1↔rowN 世界坐标算中心 + 高度，把 sprite 拉伸成
    ///   "camp_wall_width_cols 列宽 × N 行高" 的贴地城墙（等 GridMap.Cells 就绪后惰性执行一次，sprite 切换时重算缩放）。
    ///
    /// 0 [SerializeField]：sprite 运行时从 resources/art/camp/ 按文件名加载；位置/缩放由网格坐标算（不读场景摆位）。
    /// </summary>
    public class CampVisualController : MonoBehaviour
    {
        SpriteRenderer _sr;            // 左基地（玩家城墙）
        GameObject _rightGo;           // 右基地（敌方城墙）容器
        SpriteRenderer _rightSr;
        Sprite _spriteFull, _spriteMid, _spriteLow, _spriteDestroyed;
        string _curKey;
        string _curKeyRight;          // R1e：右城墙独立 hp% 切帧 key
        bool _placed;                  // 双基地是否已按网格定位
        bool _rightVisible;            // 右基地当前显隐（poll Battle_ShowRightBase）
        float _leftBaseX, _rightBaseX; // v7 双基地世界 X（网格外左/右侧）
        WallPlacement _leftPlacement;
        WallPlacement _rightPlacement;
        float _pollAccum;
        const float POLL_INTERVAL = 0.3f;

        struct WallPlacement
        {
            public bool fromXml;
            public bool visible;
            public float x;
            public float y;
            public float z;
            public float width;
            public float height;
            public string sortingLayer;
            public int sortingOrder;
            public bool flipX;
        }

        void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            _spriteFull = ResourceHost.LoadSprite("resources/art/camp/camp_full_hp.png");
            _spriteMid = ResourceHost.LoadSprite("resources/art/camp/camp_mid_hp.png");
            _spriteLow = ResourceHost.LoadSprite("resources/art/camp/camp_low_hp.png");
            _spriteDestroyed = ResourceHost.LoadSprite("resources/art/camp/camp_destroyed.png");

            // 进局左基地一定满血 → 立即应用 full（避免首帧闪默认占位）
            if (_sr != null && _spriteFull != null)
            {
                _sr.sprite = _spriteFull;
                _curKey = "full";
                _sr.enabled = false; // 等 GridMap 定位完成后再显示，避免首帧在场景旧位置闪一下。
            }

            // 点击 → Panel_CampDetail。GameScene 的 CampVisual 无 Collider2D → 运行时补一个。
            // size 取 sprite.bounds（local），随 transform.localScale 缩放后自动覆盖整列城墙。
            var col = GetComponent<Collider2D>();
            if (col == null)
            {
                var box = gameObject.AddComponent<BoxCollider2D>();
                if (_sr != null && _sr.sprite != null)
                {
                    var b = _sr.sprite.bounds;
                    box.size = new Vector2(b.size.x, b.size.y);
                    box.offset = new Vector2(b.center.x, b.center.y);
                }
                else
                {
                    box.size = new Vector2(2f, 2f);
                }
                box.isTrigger = true;
            }
        }

        void OnMouseUpAsButton()
        {
            // 已迁热更 UI：点击基地 → 调 Lua CampDetail_Open（wnd_camp_detail.lua 懒加载+刷新+显示），
            // 不再激活场景 Panel_CampDetail（该节点由 CampDetailController 惰性后保持 inactive）。
            try { HeroDefense.Engine.Host.LuaHost.CallGlobal("CampDetail_Open"); }
            catch (System.Exception e) { Debug.LogWarning($"[CampVisualController] open CampDetail 失败: {e.Message}"); }
        }

        void Update()
        {
            if (!_placed)
            {
                TryPlaceWalls();
                if (!_placed) return;
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            ApplyByLuaHp();
            ApplyRightBaseVisibility();
        }

        // 等 GridMap.Cells 就绪后把左/右城墙定位到网格外两侧（一次）
        void TryPlaceWalls()
        {
            if (!GridMap.IsInitialized || GridMap.Cells == null) return;
            // v8：透视网格下，近端底部行会比远端更宽。城墙必须按所有行里最外侧边界摆放，
            // 否则用第 1 行窄行计算会压住最后两行的第一列/最后一列。
            var metrics = ComputeWallMetrics();
            _leftPlacement = BuildDefaultPlacement(metrics, false, transform.position.z);
            _rightPlacement = BuildDefaultPlacement(metrics, true, transform.position.z);

            Battlefield2DLayoutBridge.CampWallLayout xml;
            if (Battlefield3DLayoutBridge.TryGetCampWallLayout("left", out xml) || Battlefield2DLayoutBridge.TryGetCampWallLayout("left", out xml))
                ApplyXmlLayout(ref _leftPlacement, xml);
            if (Battlefield3DLayoutBridge.TryGetCampWallLayout("right", out xml) || Battlefield2DLayoutBridge.TryGetCampWallLayout("right", out xml))
                ApplyXmlLayout(ref _rightPlacement, xml);

            _leftBaseX = _leftPlacement.x;
            _rightBaseX = _rightPlacement.x;

            PlaceWall(transform, _leftPlacement);
            if (_sr != null) _sr.enabled = _leftPlacement.visible;

            if (_rightGo == null)
            {
                _rightGo = new GameObject("RightBaseWall");
                // 兄弟节点（不挂在本 transform 下，避免继承左基地缩放）
                _rightGo.transform.SetParent(transform.parent, false);
                _rightSr = _rightGo.AddComponent<SpriteRenderer>();
                _rightSr.sprite = _spriteFull;
                _rightSr.flipX = true;                                  // 镜像朝向（面向战场）
                _rightSr.color = new Color(1f, 0.82f, 0.80f, 1f);        // 略红区分敌方（占位，待美术）
                _rightSr.enabled = false;
            }
            PlaceWall(_rightGo.transform, _rightPlacement);
            if (_rightSr != null) _rightSr.enabled = _rightPlacement.visible;
            _rightGo.SetActive(_rightVisible && _rightPlacement.visible);
            _placed = true;
            Debug.Log($"[CampVisualController] v9 城墙定位 left=({_leftPlacement.x:F2},{_leftPlacement.y:F2}) right=({_rightPlacement.x:F2},{_rightPlacement.y:F2}) xml=({_leftPlacement.fromXml},{_rightPlacement.fromXml}) gridEdge=({metrics.leftGridEdge:F2},{metrics.rightGridEdge:F2})");
        }

        WallPlacement BuildDefaultPlacement(WallMetrics metrics, bool right, float z)
        {
            float wallWidth = metrics.colWidth * CampWallWidthCols();
            float outward = metrics.colWidth * CampWallOffsetCols();
            return new WallPlacement
            {
                fromXml = false,
                visible = true,
                x = right ? metrics.rightGridEdge + wallWidth * 0.5f + outward : metrics.leftGridEdge - wallWidth * 0.5f - outward,
                y = metrics.centerY,
                z = z,
                width = wallWidth,
                height = metrics.height,
                sortingLayer = "Castle",
                sortingOrder = -20,
                flipX = right
            };
        }

        void ApplyXmlLayout(ref WallPlacement p, Battlefield2DLayoutBridge.CampWallLayout layout)
        {
            p.fromXml = true;
            p.visible = layout.visible;
            p.x = layout.x;
            p.y = layout.y;
            p.z = layout.z;
            if (layout.width > 0.001f) p.width = layout.width;
            if (layout.height > 0.001f) p.height = layout.height;
            if (!string.IsNullOrEmpty(layout.sortingLayer)) p.sortingLayer = layout.sortingLayer;
            p.sortingOrder = layout.sortingOrder;
            p.flipX = layout.flipX;
        }

        static float CampWallWidthCols()
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return 1f;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", "camp_wall_width_cols");
                if (row == null) return 1f;
                return Mathf.Clamp(cm.GetValue<float>(row, "value", 1f), 0.5f, 2.5f);
            }
            catch { return 1f; }
        }

        static float CampWallOffsetCols()
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return 0f;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", "camp_wall_x_offset_cols");
                if (row == null) return 0f;
                return Mathf.Clamp(cm.GetValue<float>(row, "value", 0f), 0f, 2f);
            }
            catch { return 0f; }
        }

        struct WallMetrics
        {
            public float leftGridEdge;
            public float rightGridEdge;
            public float colWidth;
            public float rowSpacing;
            public float height;
            public float centerY;
        }

        static WallMetrics ComputeWallMetrics()
        {
            float leftEdge = float.MaxValue;
            float rightEdge = float.MinValue;
            float colWidth = 0f;
            float minY = float.MaxValue;
            float maxY = float.MinValue;

            for (int r = 1; r <= GridMap.Rows; r++)
            {
                Vector2 left = GridMap.CellToWorld(r, 1);
                Vector2 leftNext = GridMap.CellToWorld(r, Mathf.Min(2, GridMap.Cols));
                Vector2 right = GridMap.CellToWorld(r, GridMap.Cols);
                Vector2 rightPrev = GridMap.CellToWorld(r, Mathf.Max(1, GridMap.Cols - 1));

                float leftStep = Mathf.Abs(leftNext.x - left.x);
                float rightStep = Mathf.Abs(right.x - rightPrev.x);
                float rowStep = Mathf.Max(leftStep, rightStep);
                if (rowStep < 0.01f) rowStep = GridMap.CellSizeX;

                leftEdge = Mathf.Min(leftEdge, left.x - rowStep * 0.5f);
                rightEdge = Mathf.Max(rightEdge, right.x + rowStep * 0.5f);
                colWidth = Mathf.Max(colWidth, rowStep);
                minY = Mathf.Min(minY, left.y, right.y);
                maxY = Mathf.Max(maxY, left.y, right.y);
            }

            if (colWidth < 0.01f) colWidth = Mathf.Max(0.01f, GridMap.CellSizeX);
            float rowSpacing = 0f;
            if (GridMap.Rows >= 2)
                rowSpacing = Mathf.Abs(GridMap.CellToWorld(1, 1).y - GridMap.CellToWorld(2, 1).y);
            if (rowSpacing < 0.01f) rowSpacing = GridMap.CellSizeY;

            return new WallMetrics
            {
                leftGridEdge = leftEdge == float.MaxValue ? GridMap.CellToWorld(1, 1).x - colWidth * 0.5f : leftEdge,
                rightGridEdge = rightEdge == float.MinValue ? GridMap.CellToWorld(1, GridMap.Cols).x + colWidth * 0.5f : rightEdge,
                colWidth = colWidth,
                rowSpacing = rowSpacing,
                height = Mathf.Max(rowSpacing, maxY - minY + rowSpacing),
                centerY = (maxY + minY) * 0.5f
            };
        }

        // v9：把 sprite 拉伸成 Scene2D/CampWall 指定尺寸；缺 XML 时回退为 "配置列宽 × Rows 行高"。
        void PlaceWall(Transform t, WallPlacement p)
        {
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr == null || sr.sprite == null) return;

            var b = sr.sprite.bounds.size;
            if (b.x > 0.0001f && b.y > 0.0001f)
                t.localScale = new Vector3(p.width / b.x, p.height / b.y, 1f);

            t.position = new Vector3(p.x, p.y, p.z);
            sr.sortingLayerName = string.IsNullOrEmpty(p.sortingLayer) ? "Castle" : p.sortingLayer;
            sr.sortingOrder = p.sortingOrder;
            sr.flipX = p.flipX;

            var box = t.GetComponent<BoxCollider2D>();
            if (box != null)
            {
                var bounds = sr.sprite.bounds;
                box.size = new Vector2(bounds.size.x, bounds.size.y);
                box.offset = new Vector2(bounds.center.x, bounds.center.y);
            }
        }

        void ApplyByLuaHp()
        {
            try
            {
#if XLUA
                var env = LuaHost.Env;
                if (env == null) return;
                // R1e：基地血量真相已迁到 grid_state 基地目标格 → poll Base_State.left/right（己方左城墙 / 敌方右城墙）。
                ApplyWallSprite(_sr, transform, _leftPlacement,
                    env.Global.GetInPath<int>("Base_State.left_hp"),
                    env.Global.GetInPath<int>("Base_State.left_max_hp"), ref _curKey);
                if (_rightVisible && _rightSr != null)
                {
                    ApplyWallSprite(_rightSr, _rightGo != null ? _rightGo.transform : null, _rightPlacement,
                        env.Global.GetInPath<int>("Base_State.right_hp"),
                        env.Global.GetInPath<int>("Base_State.right_max_hp"), ref _curKeyRight);
                }
#endif
            }
            catch { /* silent */ }
        }

        // R1e：按 hp% 切 4 段城墙 sprite（左右城墙共用；右墙额外保留 flipX+染红占位色）。
        void ApplyWallSprite(SpriteRenderer sr, Transform t, WallPlacement placement, int hp, int maxHp, ref string curKey)
        {
            if (sr == null || maxHp <= 0) return;
            float pct = (float)hp / maxHp;
            string targetKey;
            Sprite target;
            if (hp <= 0) { targetKey = "destroyed"; target = _spriteDestroyed; }
            else if (pct >= 0.67f) { targetKey = "full"; target = _spriteFull; }
            else if (pct >= 0.34f) { targetKey = "mid"; target = _spriteMid; }
            else { targetKey = "low"; target = _spriteLow; }

            if (curKey != targetKey && target != null)
            {
                sr.sprite = target;
                curKey = targetKey;
                if (_placed && t != null) PlaceWall(t, placement);   // sprite 尺寸可能不同 → 重算缩放
            }
        }

        // poll Lua 全局 Battle_ShowRightBase（PvE=false 隐 / PVP=true 显），切换右基地节点显隐
        void ApplyRightBaseVisibility()
        {
            try
            {
#if XLUA
                var env = LuaHost.Env;
                if (env == null) return;
                bool show = env.Global.Get<bool>("Battle_ShowRightBase");
                if (show != _rightVisible)
                {
                    _rightVisible = show;
                    if (_rightGo != null) _rightGo.SetActive(_rightVisible && _rightPlacement.visible);
                }
#endif
            }
            catch { /* silent */ }
        }
    }
}
