using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Config;
using HeroDefense.Engine.Host;
using HeroDefense.UI;
using HeroDefense.Utils;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Battle
{
    /// <summary>
    /// 单位视图（兵种 / 武将 / 建筑 / 怪 通用）。
    ///
    /// 设计原则（CLAUDE.md §1）：
    ///   - 0 SerializeField — 用 UIFinder 子节点查找 hp_bar / shadow / sprite_root
    ///   - **不存业务数据**（不存 hp/atk/lv 数值 — 那是 Lua 的）
    ///   - 持 long Handle（BattleBridge 句柄表 key），业务 Lua 通过 handle 操作
    ///   - sortingOrder 由 GridSortingService 在外部驱动（如 EnemyMover 每帧更新）
    ///
    /// 子节点约定（prefab 或 runtime build）：
    ///   - sprite_root (有 SpriteRenderer)
    ///   - hp_bar (UI Image 或 SpriteRenderer)
    ///   - shadow (SpriteRenderer，可选)
    ///
    /// Step 11 性能优化（v3 design.md §3）：
    ///   - 屏外 culling：OnBecameInvisible 暂停 SpriteAnimator 帧切（IsOnScreen 暴露给外部 culling 决策）
    ///   - 血条距离剔除：每 0.5s 检查到 Camera.main 距离，超过 shadow_distance_cull_units cell 时隐藏 hp_bar
    ///   - GameConfig 驱动（culling_enabled / shadow_distance_cull_units / hp_bar_cull_check_interval）
    /// </summary>
    public class UnitView : MonoBehaviour,
        UnityEngine.EventSystems.IPointerDownHandler,
        UnityEngine.EventSystems.IPointerUpHandler,
        UnityEngine.EventSystems.IBeginDragHandler,
        UnityEngine.EventSystems.IDragHandler,
        UnityEngine.EventSystems.IEndDragHandler
    {
        public long Handle { get; set; }

        // 资源引用（Awake 时一次性查找，避免每帧 lookup）
        public SpriteRenderer Sr { get; private set; }
        public Transform HpBarRoot { get; private set; }
        public Transform ShadowRoot { get; private set; }

        // sortingOrder 缓存（GridSortingService.UpdateIfChanged 使用）
        public float LastSortY { get; set; }

        // 屏外 culling 状态（Step 11 优化用）— 外部（EnemyMover/SpriteAnimator）可读
        public bool IsOnScreen { get; private set; } = true;

        private SpriteRenderer _shadowSr;
        // hp_bar 可能是 UI.Image (filled) 或 SpriteRenderer 的局部 scaleX
        private Image _hpBarImage;
        private Transform _hpBarFill;
        private float _hpBarMaxScaleX = 1f;

        // 缓存的 SpriteAnimator 引用（OnBecameInvisible 时暂停帧切以省 CPU）
        private SpriteAnimator _animator;

        // 血条距离剔除 — 配置 + 状态
        // T215 (2026-05-21):默认关掉剔除。MVP 阶段单位 < 50 性能无压力,直接关。
        // R1a 后 cell 距离单位取 GridMap.CellSizeX（真实 1.28，非旧逻辑 1.0），但 culling 关闭故仅缓存不生效。
        private static bool _perfCfgLoaded;
        private static bool _cullingEnabled = false;
        private static float _hpBarCullDistCells = 5f;
        private static float _hpBarCullCheckInterval = 0.5f;
        private static float _cellSizeCache = 1f;
        private float _hpBarCullAccum;
        private bool _hpBarUserVisible = true;     // 业务侧设的可见性（SetHpBarVisible）
        private bool _hpBarDistCulled;             // 距离剔除强制隐藏

        // ============ Round 12 — 多占位 footprint（Issue 1 + Issue 4） ============
        // SetFootprint 由 BattleBridge.Battle_SpawnUnit 在 spawn 后按 occupy.txt 调用：
        //   - collider 覆盖整个 w×h 占位格 → 点任意占位格都能起手拖（Issue 1）
        //   - sprite_root 重定位到 block 几何中心 + 缩放铺满 block → sprite 覆盖占位格（Issue 4）
        // root GameObject scale 始终 (1,1,1)；所有缩放只在 sprite_root.localScale 上（不影响 collider）。
        private int _fpW = 1;
        private int _fpH = 1;
        private float _blockW = CELL_W;   // block 世界包围盒宽（含一整个 cell）
        private float _blockH = CELL_H;
        // cell 视觉世界尺寸：R1a 经 grid.tab cell_w/cell_h → GridMap.CellSizeX/Y 配置化（默认 1.28×0.96）。
        // 单位在 grid 初始化后才 spawn → 取值时 GridMap 已加载；与 CellView.EnsureCellScale 同源。
        private static float CELL_W => GridMap.CellSizeX;
        private static float CELL_H => GridMap.CellSizeY;

        private void Awake()
        {
            // sprite_root：优先 child "sprite_root"，否则 GetComponentInChildren
            var spriteRootTr = UIFinder.FindChildByName(transform, "sprite_root");
            if (spriteRootTr != null) Sr = spriteRootTr.GetComponent<SpriteRenderer>();
            if (Sr == null) Sr = GetComponentInChildren<SpriteRenderer>();

            // Issue 1/4 — 加 BoxCollider2D 让 OnMouseDown/Drag/Up 能响应（场上单位起手拖）
            // 没有 Rigidbody2D 也能用 OnMouseXXX；尺寸初值 1×1（CELL_W×CELL_H），
            // 多占位单位由 SetFootprint 覆盖成整个 w×h 占位格
            EnsureCollider();

            HpBarRoot = UIFinder.FindChildByName(transform, "hp_bar");
            if (HpBarRoot != null)
            {
                _hpBarImage = HpBarRoot.GetComponent<Image>();
                if (_hpBarImage == null)
                {
                    // 尝试 fill 子节点（用 localScale.x 表示 hp 比例）
                    _hpBarFill = UIFinder.FindChildByName(HpBarRoot, "fill");
                    if (_hpBarFill != null)
                    {
                        _hpBarMaxScaleX = _hpBarFill.localScale.x;
                    }
                }
            }

            ShadowRoot = UIFinder.FindChildByName(transform, "shadow");
            if (ShadowRoot != null) _shadowSr = ShadowRoot.GetComponent<SpriteRenderer>();

            _animator = GetComponent<SpriteAnimator>();
            EnsurePerfConfig();
        }

        /// <summary>读 GameConfig.txt 缓存性能阈值（首次 Awake 触发，幂等）。</summary>
        private static void EnsurePerfConfig()
        {
            if (_perfCfgLoaded) return;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var rowEnable = cm.GetTableInfo("GameConfig", "key", "culling_enabled");
                    if (rowEnable != null) _cullingEnabled = cm.GetValue<bool>(rowEnable, "value", true);

                    var rowDist = cm.GetTableInfo("GameConfig", "key", "shadow_distance_cull_units");
                    if (rowDist != null) _hpBarCullDistCells = cm.GetValue<int>(rowDist, "value", 5);

                    var rowItv = cm.GetTableInfo("GameConfig", "key", "hp_bar_cull_check_interval");
                    if (rowItv != null) _hpBarCullCheckInterval = cm.GetValue<float>(rowItv, "value", 0.5f);

                    _cellSizeCache = GridMap.CellSizeX > 0f ? GridMap.CellSizeX : 1f;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnitView] 读 GameConfig 失败，沿用默认值: {e.Message}");
            }
            _perfCfgLoaded = true;
        }

        // ============ 视图操作（业务 Lua 通过 BattleBridge 调用） ============

        public void SetSprite(Sprite sprite)
        {
            if (Sr == null) Sr = GetComponentInChildren<SpriteRenderer>();
            if (Sr != null)
            {
                Sr.sprite = sprite;
                // Round 12 — sprite 换了，若已有 footprint 重新铺满 block
                if (_fpW > 0 && sprite != null) FitSpriteToBlock();
            }
        }

        public void SetCellPos(int row, int col)
        {
            var wp = GridMap.CellToWorld(row, col);
            transform.position = new Vector3(wp.x, wp.y, 0f);
            if (Sr != null) GridSortingService.UpdateIfChanged(Sr, ref _sortYRef, wp.y);
        }

        public void SetWorldPosition(float wx, float wy)
        {
            transform.position = new Vector3(wx, wy, 0f);
            if (Sr != null) GridSortingService.UpdateIfChanged(Sr, ref _sortYRef, wy);
        }

        /// <summary>hp 比例（0..1）。优先 Image.fillAmount，回退 child fill scaleX。</summary>
        public void SetHp(float cur, float max)
        {
            float pct = (max > 0f) ? Mathf.Clamp01(cur / max) : 0f;
            if (_hpBarImage != null)
            {
                _hpBarImage.fillAmount = pct;
            }
            else if (_hpBarFill != null)
            {
                var s = _hpBarFill.localScale;
                s.x = _hpBarMaxScaleX * pct;
                _hpBarFill.localScale = s;
            }
        }

        public void SetHpBarVisible(bool visible)
        {
            _hpBarUserVisible = visible;
            ApplyHpBarVisibility();
        }

        private void ApplyHpBarVisibility()
        {
            if (HpBarRoot == null) return;
            bool show = _hpBarUserVisible && !_hpBarDistCulled;
            if (HpBarRoot.gameObject.activeSelf != show)
                HpBarRoot.gameObject.SetActive(show);
        }

        public void SetShadow(bool visible)
        {
            if (ShadowRoot != null) ShadowRoot.gameObject.SetActive(visible);
        }

        public void SetAlpha(float alpha)
        {
            if (Sr == null) Sr = GetComponentInChildren<SpriteRenderer>();
            if (Sr != null)
            {
                var c = Sr.color;
                c.a = Mathf.Clamp01(alpha);
                Sr.color = c;
            }
        }

        public void SetScale(float scale)
        {
            transform.localScale = new Vector3(scale, scale, 1f);
        }

        public void SetSortingLayer(string layerName)
        {
            if (Sr != null) Sr.sortingLayerName = layerName;
        }

        /// <summary>设朝向：原图朝右；faceRight=false → flipX 翻转朝左。攻击时朝目标。</summary>
        public void SetFacing(bool faceRight)
        {
            var sr = ResolveSr();
            if (sr != null) sr.flipX = !faceRight;
        }

        // ============ 屏外 culling（Step 11 优化 — OnBecameInvisible/Visible 由 Unity 触发） ============
        //
        // OnBecameInvisible：暂停 SpriteAnimator 帧切 + 隐藏 hp_bar（仍跑 Update 让业务/距离剔除可工作，
        //                   但不耗 sprite swap）。位移/AI 由 EnemyMover 自身决策（怪屏外仍向营帐走）。
        // OnBecameVisible ：恢复帧切 + 还原 hp_bar 状态。
        private void OnBecameInvisible()
        {
            IsOnScreen = false;
            if (_cullingEnabled)
            {
                // 暂停帧动画切帧（SpriteAnimator 内部 LateUpdate 检查 _hitStopped；这里复用 Hit-Stop hook）
                if (_animator != null) _animator.OnHitStopBegin();
                // 隐藏血条（屏外不必显示，0 个 draw call）
                if (HpBarRoot != null && HpBarRoot.gameObject.activeSelf) HpBarRoot.gameObject.SetActive(false);
            }
        }

        private void OnBecameVisible()
        {
            IsOnScreen = true;
            if (_cullingEnabled)
            {
                if (_animator != null) _animator.OnHitStopEnd();
                // 还原血条（按业务 + 距离 cull 双门控）
                ApplyHpBarVisibility();
            }
        }

        // ============ 血条距离剔除（Step 11 优化 — 每 0.5s 检查 Camera.main 距离） ============
        private void Update()
        {
            // R3a：手势长按轮询（必须在 culling 早退之前 —— culling 关闭时拖拽/按住仍要工作）
            if (_gesture != null) _gesture.Tick();

            if (!_cullingEnabled) return;
            if (HpBarRoot == null) return;
            if (!IsOnScreen) return;  // 屏外已 0 cost，无需再算距离

            _hpBarCullAccum += Time.unscaledDeltaTime;
            if (_hpBarCullAccum < _hpBarCullCheckInterval) return;
            _hpBarCullAccum = 0f;

            var cam = Camera.main;
            if (cam == null) return;

            // 算 worldspace 距离 / cellSize → cell 数
            float dx = cam.transform.position.x - transform.position.x;
            float dy = cam.transform.position.y - transform.position.y;
            float distCells = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Max(0.01f, _cellSizeCache);

            bool shouldCull = distCells > _hpBarCullDistCells;
            if (shouldCull != _hpBarDistCulled)
            {
                _hpBarDistCulled = shouldCull;
                ApplyHpBarVisibility();
            }
        }

        // ============ R3a (2026-06-11) — 场上单位输入统一走 EventSystem + GestureArbiter ============
        //
        // 旧实现 OnMouseDown/Drag/Up + Input.mousePosition 已废（tech-research 炸弹①：与
        // DragInputBridge 是两套独立状态机/坐标系，WebGL 缩放 canvas 下 Input.mousePosition 偏格；
        // 且 OnMouse 路径无法拿到 tap/双击分类）。现与 UI 卡同源：
        //   Physics2DRaycaster(BattleCamera，BattleSceneController 注入) → IPointer/IDrag 事件
        //   → GestureArbiter 分类 → 拖拽走 Battle_OnDragBegin/Move/End("unit_field")，
        //     tap → Battle_OnTap，双击 → Battle_OnDoubleTap（弹详情/收回门控全在 Lua）。
        // UI 优先天然成立：EventSystem 单一命中，指针落在 UI 上时本组件收不到事件
        // （旧版手动 IsPointerOverGameObject 屏蔽不再需要）。

        private GestureArbiter _gesture;

        private GestureArbiter EnsureGesture()
        {
            if (_gesture == null)
            {
                _gesture = new GestureArbiter(this);
                _gesture.DragStarted = p => CallLuaDragBegin(Handle.ToString(), "unit_field", p.x, p.y);
                _gesture.DragMoved = p => CallLuaDragMove(p.x, p.y);
                _gesture.DragEnded = p => CallLuaDragEnd(p.x, p.y);
                _gesture.Tapped = p => GestureArbiter.CallLuaTap(Handle.ToString(), "unit_field", p.x, p.y);
                _gesture.DoubleTapped = p => GestureArbiter.CallLuaDoubleTap(Handle.ToString(), "unit_field", p.x, p.y);
            }
            return _gesture;
        }

        private void EnsureCollider()
        {
            var col = GetComponent<BoxCollider2D>();
            if (col == null) col = gameObject.AddComponent<BoxCollider2D>();
            col.isTrigger = true;   // 不参与物理，仅拾取
            // 兜底尺寸 = 1 个 cell 视觉大小（CELL_W×CELL_H）；多占位单位由 SetFootprint 覆盖。
            // root scale 恒为 (1,1,1)，collider 是 local 尺寸 → 直接等于 world 尺寸。
            col.size = new Vector2(CELL_W, CELL_H);
            col.offset = Vector2.zero;
            GestureArbiter.EnsureConfig();
        }

        // ============ Round 12 — SetFootprint / FitSpriteToBlock ============

        /// <summary>
        /// 按 occupy 形状（w×h 占位格，anchor 在 (row,col)）重定位 sprite_root + 重设 collider。
        /// 由 BattleBridge.Battle_SpawnUnit 在 spawn 后调用。
        ///   - collider.size = block 包围盒，offset = block 几何中心相对 anchor 的偏移 → 整个占位格可点（Issue 1）
        ///   - sprite_root.localPosition = 同偏移 → sprite 居中在 block 而非 anchor cell（Issue 4）
        ///   - 若 sprite 已存在立即 FitSpriteToBlock；否则等 Battle_SetSprite/PlayAnim 后补 fit
        /// 1×1 单位：offset=0, block=1cell → 行为与旧版一致（不回归）。
        ///
        /// 几何来源（Round 12 修正）：block 包围盒按 anchor cell + 固定 cell 步距推算，
        ///   **不**读 far cell 的 CellToWorld。原因：营帐 4 格在 GameScene 没有 CellView 节点，
        ///   GridMap.CellToWorld 对缺失 cell 会落到 1.0-步距公式（与真实 1.28×0.96 场景坐标不同尺度），
        ///   导致 anchor 上/左方的多占位单位 far corner 落在营帐时算出错乱包围盒。
        ///   网格为均匀 1.28(W)×0.96(H) 栅格（已实测）→ block 直接 = w·CELL_W × h·CELL_H，
        ///   block 从 anchor cell 向右(+X)、向下(-Y)展开。
        /// </summary>
        public void SetFootprint(int row, int col, int w, int h)
        {
            if (w < 1) w = 1;
            if (h < 1) h = 1;
            _fpW = w;
            _fpH = h;

            // block 包围盒 = w×h 个 cell 视觉尺寸（均匀栅格，不依赖 far cell 查找）
            _blockW = w * CELL_W;
            _blockH = h * CELL_H;

            // block 几何中心相对 anchor cell 中心的偏移：
            //   向右展开 (w-1) 个 cell → +X 半程；向下展开 (h-1) 个 cell → -Y 半程
            //   root scale 恒为 (1,1,1) → local 偏移 == world 偏移
            Vector2 offset = new Vector2(
                (w - 1) * 0.5f * CELL_W,
                -(h - 1) * 0.5f * CELL_H);

            // sprite_root 重定位到 block 几何中心
            // 注：Sr 用 ResolveSr() 懒查找 —— Awake 在 runtime AddComponent / edit-mode 下不一定已跑，
            //     不能假设 Awake 已缓存 Sr（与 SetSprite/SetAlpha 一致的兜底）。
            var sr = ResolveSr();
            if (sr != null)
            {
                var rt = sr.transform;
                var lp = rt.localPosition;
                rt.localPosition = new Vector3(offset.x, offset.y, lp.z);
            }

            // collider 覆盖整个 w×h 占位格（Issue 1）
            var bc = GetComponent<BoxCollider2D>();
            if (bc == null) bc = gameObject.AddComponent<BoxCollider2D>();
            bc.isTrigger = true;
            bc.size = new Vector2(_blockW, _blockH);
            bc.offset = new Vector2(offset.x, offset.y);

            // sprite 已设 → 立即铺满 block（Issue 4）
            if (sr != null && sr.sprite != null) FitSpriteToBlock();
        }

        /// <summary>懒查找 sprite_root 的 SpriteRenderer（兜底 Awake 未跑 / UIFinder 失配）。</summary>
        private SpriteRenderer ResolveSr()
        {
            if (Sr != null) return Sr;
            var spriteRootTr = UIFinder.FindChildByName(transform, "sprite_root");
            if (spriteRootTr != null) Sr = spriteRootTr.GetComponent<SpriteRenderer>();
            if (Sr == null) Sr = GetComponentInChildren<SpriteRenderer>();
            return Sr;
        }

        /// <summary>
        /// 把 sprite 缩放到铺满 w×h block（不依赖 PNG 精确像素，直接按 sprite world bounds 反推）。
        /// 缩放只设在 sprite_root.localScale（不是 root scale → 不影响 collider）。
        /// 在 ① SetFootprint 末尾（若 sprite 已设）② Battle_SetSprite/PlayAnim 设完 sprite 后 调用。
        /// 同一单位各帧 PNG 尺寸相同 → 在 transform 上设一次即可，SpriteAnimator 切帧不重置 localScale。
        ///
        /// 2026-05-23 — 红将水平居中修正：
        ///   红将（刘备/诸葛亮/吕布/曹操）occupy_id=2 (2×2 block) 但美术沿用 1×2_v 尺寸 (256W×384H)，
        ///   block 宽高比 ≠ sprite 宽高比，原版直接 scale 拉伸会把 256 宽美术水平拉伸到 2 格宽 → 形变。
        ///   改为 aspect-aware：sprite 宽高比 < block 宽高比 时（sprite 比 block 「更瘦」），
        ///     按高度等比缩放，水平方向自然留白 + 居中（sprite_root.localPosition 已在 SetFootprint
        ///     设到 block 几何中心，等比缩放后 sprite 直接居中在 block 内）。
        ///   sprite 与 block 宽高比一致（兵种 + 蓝/紫/橙将的 occupy_id=4 / 1×1 / 2×1 等）→ 保持原拉伸路径，
        ///   不引入回归（aspect 相等 → 两路径数值一致）。
        ///   sprite 宽高比 > block 宽高比（sprite 比 block 「更宽」，理论上未来才会出现）→ 按宽度等比缩放
        ///     垂直留白 + 居中，同样不形变。
        /// </summary>
        public void FitSpriteToBlock()
        {
            var sr = ResolveSr();
            if (sr == null || sr.sprite == null) return;
            var sz = sr.sprite.bounds.size;   // world units（已含 pixelsPerUnit）
            if (sz.x <= 0f || sz.y <= 0f) return;

            float spriteAspect = sz.x / sz.y;
            float blockAspect = _blockW / _blockH;
            // 容差：避免浮点 0.6667 vs 0.6666 误判进 letterbox 分支造成微小留白
            const float ASPECT_EPS = 0.01f;

            float sx, sy;
            bool bottomAlign = false;
            if (Mathf.Abs(spriteAspect - blockAspect) <= ASPECT_EPS)
            {
                // 宽高比一致 → 直接铺满（与旧版数值一致）
                sx = _blockW / sz.x;
                sy = _blockH / sz.y;
            }
            else if (spriteAspect < blockAspect)
            {
                if (_fpW == 1 && _fpH == 1)
                {
                    // 2026-06-11 验收修复④（设计块2.4-(2) 角色/器械占 1 格）：1×1 占位 + 竖版立绘（256×384）→
                    // 按宽度等比缩放（角色 = 1 格宽，符美术规范「内容占单 cell 宽 95%」），身高自然溢出到格子上方，
                    // sprite 底边对齐 block 底边（角色"站"在自己格子上）；溢出部分由 Y-sort 正常遮挡。
                    float s = _blockH / sz.y;   // 2026-06-14 Q2: 改高度适配, 整图"塞进格内"+居中(撤销旧"按宽缩放+底边对齐"拔高2格)
                    sx = s; sy = s;
                    // bottomAlign 保持 false: 立绘缩到 <=1 格、居中(与怪物 FitEnemyToCell 同效 -> 同行对齐, 不再竖直错位/相邻行重叠)
                }
                else
                {
                    // sprite 更瘦 → 按高度等比缩放，水平居中（红将 256×384 → 2×2 block）
                    float s = _blockH / sz.y;
                    sx = s; sy = s;
                }
            }
            else
            {
                // sprite 更宽 → 按宽度等比缩放，垂直居中（未来扩展）
                float s = _blockW / sz.x;
                sx = s;
                sy = s;
            }
            sr.transform.localScale = new Vector3(sx, sy, 1f);

            // sprite_root 定位（幂等，FitSpriteToBlock 每次 SetSprite 后都会调）：
            //   默认 = block 几何中心（与 SetFootprint 一致）；贴底路径 = 底边对齐 block 底边 + 水平按 bounds 居中。
            //   bounds 是 pivot 相对（pre-scale）→ 乘 scale 后参与定位，任意 pivot 美术都成立。
            float baseX = (_fpW - 1) * 0.5f * CELL_W;
            float baseY = -(_fpH - 1) * 0.5f * CELL_H;
            var bb = sr.sprite.bounds;
            var lp0 = sr.transform.localPosition;
            if (bottomAlign)
            {
                float blockBottom = baseY - _blockH * 0.5f;
                float lx = baseX - bb.center.x * sx;
                float ly = blockBottom - (bb.center.y - bb.extents.y) * sy;
                sr.transform.localPosition = new Vector3(lx, ly, lp0.z);

                // 审查 E (2026-06-11)：立绘溢出格上方后 collider 仍只盖 1 cell → 点头/胸拖不动（核心交互高频迷惑）。
                // 把 collider 拉高到 sprite 视觉包围盒（底边对齐 block 底边向上扩）。代价：与上一行单位 collider
                // 可能重叠、OnMouse 拾取不保证按 Y-sort 取前者 → 已知小概率误拾，R3 GestureArbiter 统一解决。
                float visH = bb.size.y * sy;
                var bc2 = GetComponent<BoxCollider2D>();
                if (bc2 != null && visH > _blockH)
                {
                    bc2.size = new Vector2(_blockW, visH);
                    bc2.offset = new Vector2(baseX, blockBottom + visH * 0.5f);
                }
            }
            else
            {
                sr.transform.localPosition = new Vector3(baseX, baseY, lp0.z);
            }
        }

        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e)
        {
            if (Handle == 0) return;
            EnsureGesture().PointerDown(e.position, e.pointerId);
        }

        public void OnBeginDrag(UnityEngine.EventSystems.PointerEventData e)
        {
            // EventSystem 位移超自带阈值才发 BeginDrag → 位移短路启动拖拽
            EnsureGesture().PointerMove(e.position, e.pointerId, viaMoveShortcut: true);
        }

        public void OnDrag(UnityEngine.EventSystems.PointerEventData e)
        {
            EnsureGesture().PointerMove(e.position, e.pointerId);
        }

        public void OnEndDrag(UnityEngine.EventSystems.PointerEventData e)
        {
            EnsureGesture().PointerUp(e.position, e.pointerId);
        }

        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e)
        {
            EnsureGesture().PointerUp(e.position, e.pointerId);
        }
        // 注：按住不动的长按轮询在上方既有 Update() 顶部 _gesture.Tick()（EventSystem 不动不发 OnDrag）。

        private static void CallLuaDragBegin(string srcIdStr, string srcKind, float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Battle_OnDragBegin");
                if (fn != null) { fn.Call(srcIdStr, srcKind, sx, sy); fn.Dispose(); }
            }
            catch (System.Exception e) { Debug.LogError($"[UnitView] OnDragBegin fail: {e.Message}"); }
#endif
        }

        private static void CallLuaDragMove(float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Battle_OnDragMove");
                if (fn != null) { fn.Call(sx, sy); fn.Dispose(); }
            }
            catch { /* silent */ }
#endif
        }

        private static void CallLuaDragEnd(float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Battle_OnDragEnd");
                if (fn != null) { fn.Call(sx, sy); fn.Dispose(); }
            }
            catch (System.Exception e) { Debug.LogError($"[UnitView] OnDragEnd fail: {e.Message}"); }
#endif
        }

        // ============ 私有 ============
        // 给 GridSortingService.UpdateIfChanged ref 用的字段（不能 ref 属性，所以用字段）
        private float _sortYRef;
    }
}
