using UnityEngine;
using HeroDefense.Engine.Host;

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
    ///   "1 列宽 × N 行高" 的城墙（等 GridMap.Cells 就绪后惰性执行一次，sprite 切换时重算缩放）。
    ///
    /// 0 [SerializeField]：sprite 运行时从 art/camp/ 按文件名加载；位置/缩放由网格坐标算（不读场景摆位）。
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
        float _pollAccum;
        const float POLL_INTERVAL = 0.3f;

        void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            _spriteFull = ResourceHost.LoadSprite("art/camp/camp_full_hp.png");
            _spriteMid = ResourceHost.LoadSprite("art/camp/camp_mid_hp.png");
            _spriteLow = ResourceHost.LoadSprite("art/camp/camp_low_hp.png");
            _spriteDestroyed = ResourceHost.LoadSprite("art/camp/camp_destroyed.png");

            // 进局左基地一定满血 → 立即应用 full（避免首帧闪默认占位）
            if (_sr != null && _spriteFull != null)
            {
                _sr.sprite = _spriteFull;
                _curKey = "full";
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
            try
            {
                var panel = GameObject.FindGameObjectWithTag("Panel_CampDetail");
                if (panel == null)
                {
                    Debug.LogWarning("[CampVisualController] Panel_CampDetail 未找到（UIWindow 未加载或 Tag 未绑定）");
                    return;
                }
                panel.SetActive(true);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CampVisualController] open CampDetail 失败: {e.Message}");
            }
        }

        void Update()
        {
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            if (!_placed) TryPlaceWalls();
            ApplyByLuaHp();
            ApplyRightBaseVisibility();
        }

        // 等 GridMap.Cells 就绪后把左/右城墙定位到网格外两侧（一次）
        void TryPlaceWalls()
        {
            if (!GridMap.IsInitialized || GridMap.Cells == null) return;
            // v7 双基地在网格外：左基地 = 最左列(col1)左侧一格；右基地 = 最右列(colCols)右侧一格。
            float colStep = Mathf.Abs(GridMap.CellToWorld(1, 2).x - GridMap.CellToWorld(1, 1).x);
            if (colStep < 0.01f) colStep = 1.28f;
            _leftBaseX  = GridMap.CellToWorld(1, 1).x - colStep;
            _rightBaseX = GridMap.CellToWorld(1, GridMap.Cols).x + colStep;

            PlaceWallAtX(transform, _leftBaseX);

            if (_rightGo == null)
            {
                _rightGo = new GameObject("RightBaseWall");
                // 兄弟节点（不挂在本 transform 下，避免继承左基地缩放）
                _rightGo.transform.SetParent(transform.parent, false);
                _rightSr = _rightGo.AddComponent<SpriteRenderer>();
                _rightSr.sprite = _spriteFull;
                if (_sr != null)
                {
                    _rightSr.sortingLayerID = _sr.sortingLayerID;
                    _rightSr.sortingOrder = _sr.sortingOrder;
                }
                _rightSr.flipX = true;                                   // 镜像朝向（面向战场）
                _rightSr.color = new Color(1f, 0.82f, 0.80f, 1f);        // 略红区分敌方（占位，待美术）
            }
            PlaceWallAtX(_rightGo.transform, _rightBaseX);
            _rightGo.SetActive(_rightVisible);
            _placed = true;
            Debug.Log($"[CampVisualController] v7 城墙定位 leftX={_leftBaseX:F2} rightX={_rightBaseX:F2} rows={GridMap.Rows} cols={GridMap.Cols}");
        }

        // v7：把 sprite 拉伸成 "1 列宽 × Rows 行高" 城墙，定位到给定世界 X（网格外）+ 列纵向中点。
        void PlaceWallAtX(Transform t, float worldX)
        {
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr == null || sr.sprite == null) return;

            Vector2 top = GridMap.CellToWorld(1, 1);
            Vector2 bot = GridMap.CellToWorld(GridMap.Rows, 1);
            float rowSpacing = Mathf.Abs(top.y - GridMap.CellToWorld(2, 1).y);
            if (rowSpacing < 0.01f) rowSpacing = 0.96f;
            float colWidth = Mathf.Abs(GridMap.CellToWorld(1, 2).x - GridMap.CellToWorld(1, 1).x);
            if (colWidth < 0.01f) colWidth = 1.28f;

            float height = Mathf.Abs(top.y - bot.y) + rowSpacing;       // N 行总高
            var b = sr.sprite.bounds.size;
            if (b.x > 0.0001f && b.y > 0.0001f)
                t.localScale = new Vector3(colWidth / b.x, height / b.y, 1f);

            float centerY = (top.y + bot.y) * 0.5f;
            t.position = new Vector3(worldX, centerY, t.position.z);
        }

        void ApplyByLuaHp()
        {
            try
            {
#if XLUA
                var env = LuaHost.Env;
                if (env == null) return;
                // R1e：基地血量真相已迁到 grid_state 基地目标格 → poll Base_State.left/right（己方左城墙 / 敌方右城墙）。
                ApplyWallSprite(_sr, transform, _leftBaseX,
                    env.Global.GetInPath<int>("Base_State.left_hp"),
                    env.Global.GetInPath<int>("Base_State.left_max_hp"), ref _curKey);
                if (_rightVisible && _rightSr != null)
                {
                    ApplyWallSprite(_rightSr, _rightGo != null ? _rightGo.transform : null, _rightBaseX,
                        env.Global.GetInPath<int>("Base_State.right_hp"),
                        env.Global.GetInPath<int>("Base_State.right_max_hp"), ref _curKeyRight);
                }
#endif
            }
            catch { /* silent */ }
        }

        // R1e：按 hp% 切 4 段城墙 sprite（左右城墙共用；右墙额外保留 flipX+染红占位色）。
        void ApplyWallSprite(SpriteRenderer sr, Transform t, float baseX, int hp, int maxHp, ref string curKey)
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
                if (_placed && t != null) PlaceWallAtX(t, baseX);   // sprite 尺寸可能不同 → 重算缩放
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
                    if (_rightGo != null) _rightGo.SetActive(_rightVisible);
                }
#endif
            }
            catch { /* silent */ }
        }
    }
}
