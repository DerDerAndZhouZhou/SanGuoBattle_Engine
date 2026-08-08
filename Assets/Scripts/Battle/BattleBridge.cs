using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Utils;
using Newtonsoft.Json;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Battle
{
    /// <summary>
    /// Lua → C# 战场表现桥。
    ///
    /// 设计原则（CLAUDE.md §1.1 + §6）：
    ///   - 全 static，便于 Lua 端走 `CS.HeroDefense.Battle.BattleBridge.XXX()` 或经 LuaHost 注入的 Battle_* 全局函数
    ///   - **不写业务**（不判断"该不该 spawn"，仅执行）
    ///   - 句柄表 long → UnitView / ProjectileTicker，Lua 仅持 handle
    ///   - tuple 拆分避 xLua delegate userdata 坑（CLAUDE.md §10 R-V8）：
    ///       Vector2 / (row,col) 返回 → 拆为 _X / _Y / _Row / _Col 多个标量函数
    ///   - SpawnUnit / DestroyUnit 同步维护 HitFeedback.RegisterHandle / UnregisterHandle（Agent E 表现层句柄表）
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class BattleBridge
    {
        // ============ 句柄表 ============
        private static long _handleCounter = 1;
        private static readonly Dictionary<long, UnitView> _units = new Dictionary<long, UnitView>();
        // 语义朝向与 SpriteRenderer.flipX 分离：原生 *_left 帧本身朝左但 flipX=false，
        // 投射物与死亡选态仍必须知道业务请求的真实左右。
        private static readonly Dictionary<long, bool> _visualFaceRight =
            new Dictionary<long, bool>();
        private static readonly HashSet<long> _missingStatusViewWarnings =
            new HashSet<long>();
        private const int UnitTeamOwn = 0;
        private const int UnitTeamEnemy = 1;
        private static readonly HashSet<int> _invalidUnitSpawnTeamsWarned = new HashSet<int>();
        private const int RuntimeOverlaySlotCount = 4;
        private static readonly Dictionary<long, string[]> _unitOverlaySlots =
            new Dictionary<long, string[]>();
        private static readonly Dictionary<long, ProjectileTicker> _projectiles = new Dictionary<long, ProjectileTicker>();

        // Step 11 投射物池：上限 30（GameConfig.max_projectiles）
        //   - SpawnProjectile：优先从 _projectilePool 拿，没有则 new（同时记入 _projectilePoolStats）
        //   - RecycleProjectile：从 _projectiles 句柄表移除 + SetActive(false) + 入 _projectilePool
        //   - OnBattleSceneExit：池 + 句柄表全销毁
        private static readonly Stack<GameObject> _projectilePool = new Stack<GameObject>();
        private static int _projectileMaxPool = 30;
        private static int _projectilePoolHits;
        private static int _projectilePoolMisses;
        private static int _projectilePoolCfgLoaded;

        // T203 (2026-05-21) — 血量条 1×1 白色 sprite 缓存（center + left pivot 两种）
        private static Sprite _whitePixelSpriteCenter;
        private static Sprite _whitePixelSpriteLeft;
        private static Sprite _hpBarBgSprite;
        private static Sprite _hpBarFillAllySprite;
        private static Sprite _hpBarFillEnemySprite;
        private static readonly Dictionary<int, Rect> _spriteVisibleRectCache = new Dictionary<int, Rect>();
        private const float HP_BAR_WIDTH = 0.72f;
        private const float HP_BAR_HEIGHT = 0.09f;
        private const float STATUS_BAR_DUAL_HEIGHT = 0.065f;
        private const float STATUS_BAR_ROW_OFFSET = 0.0475f;
        private const float HP_BAR_LOCAL_Y = 1.78f;   // 兜底：无 baseSr 时用（旧全尺寸精灵头顶经验值）
        private const float HP_BAR_GAP = 0.06f;        // 血条置于精灵渲染顶部之上的小间隙
        private static readonly Color HeroStatusColor =
            new Color(0.84f, 0.26f, 0.20f, 1f);
        private static readonly Color TroopStatusColor =
            new Color(0.28f, 0.68f, 0.34f, 1f);
        private static Sprite GetOrCreateWhitePixelSprite(bool leftPivot)
        {
            ref var cache = ref _whitePixelSpriteCenter;
            if (leftPivot) cache = ref _whitePixelSpriteLeft;
            if (cache != null) return cache;
            var tex = Texture2D.whiteTexture;
            var pivot = leftPivot ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 0.5f);
            cache = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), pivot, 1f);
            return cache;
        }

        private static Sprite LoadHpBarSprite(string relPath, bool leftPivot, ref Sprite cache)
        {
            if (cache != null) return cache;
            var src = HeroDefense.Engine.Host.LuaHost.LoadSprite(relPath, false);
            if (src == null) return null;
            var pivot = leftPivot ? new Vector2(0f, 0.5f) : new Vector2(0.5f, 0.5f);
            cache = Sprite.Create(src.texture, src.textureRect, pivot, src.pixelsPerUnit);
            return cache;
        }

        private static Vector3 ScaleForWorldSize(Sprite sprite, float worldW, float worldH)
        {
            if (sprite == null) return new Vector3(worldW, worldH, 1f);
            var sz = sprite.bounds.size;
            float sx = worldW / Mathf.Max(0.0001f, sz.x);
            float sy = worldH / Mathf.Max(0.0001f, sz.y);
            return new Vector3(sx, sy, 1f);
        }

        // T203/T214 hp_bar 真渲染。2026-06-03 抽出供 unit + enemy 复用（原仅 Battle_SpawnUnit 内联，
        //   导致怪物无血条 — 怪 spawn 路径从不建 hp_bar）。
        //   父节点 hp_bar 不带 SpriteRenderer（不缩放）,只作位置锚；子节点 bg/fill 各自独立 SpriteRenderer
        //   用自己 localScale 决定可见尺寸（父 scale 会让 fill 起点不在 bg 左缘 → 满血前有黑边）。
        //   2026-07-01 用户修正：角色运行图高度固定后，血条不再按序列帧可见 bounds 动态找头顶，
        //   统一固定在整张画布底部锚点上方，避免切帧时漂移。
        //   baseSr   — 宿主精灵渲染器：取其 sortingLayer + order 作血条层级基准（血条叠其上 +100/+101），
        //              使 unit 血条在 Tower 层、enemy 血条在 Enemy 层，各自盖住本体。
        //   localY   — 兼容旧签名；实际使用固定 HP_BAR_LOCAL_Y。
        //   fillColor— 进度条颜色（友军绿 / 敌军红）。
        private static GameObject BuildHpBar(
            GameObject root,
            SpriteRenderer baseSr,
            float localY,
            Color fillColor,
            bool dual = false)
        {
            int layerId = baseSr != null ? baseSr.sortingLayerID : 0;
            int baseOrder = baseSr != null ? baseSr.sortingOrder : 0;

            var hpBar = new GameObject("hp_bar");
            hpBar.transform.SetParent(root.transform, false);
            hpBar.transform.localPosition = new Vector3(0f, HP_BAR_LOCAL_Y, 0f);

            var hpBg = new GameObject("bg", typeof(SpriteRenderer));
            hpBg.transform.SetParent(hpBar.transform, false);
            var bgSr = hpBg.GetComponent<SpriteRenderer>();
            var bgSprite = LoadHpBarSprite("resources/art/ui/hp_bar/hp_bar_bg.png", false, ref _hpBarBgSprite);
            bool bgUsesAsset = bgSprite != null;
            bgSr.sprite = bgUsesAsset ? bgSprite : GetOrCreateWhitePixelSprite(false);
            bgSr.color = bgUsesAsset ? Color.white : new Color(0f, 0f, 0f, 0.9f);
            bgSr.sortingLayerID = layerId;
            // 审查 K (2026-06-11)：+100 偏移会被「向下走 ≥2 行」的 Y-sort 增量(~96/行)反超 → 血条被本体盖住
            //（R2 行走 + 1×1 立绘贴底加高后逐帧可见）。改 +1000 = 高于全场单位 sprite 的 Y-sort 跨度(~768)，
            // 血条恒在单位本体之上；条间相对层级仍按各自 baseOrder 保持。
            bgSr.sortingOrder = baseOrder + 1000;

            var hpFill = new GameObject("fill", typeof(SpriteRenderer));
            hpFill.transform.SetParent(hpBar.transform, false);
            var fillSr = hpFill.GetComponent<SpriteRenderer>();
            fillSr.sprite = GetOrCreateWhitePixelSprite(true);
            fillSr.color = dual ? HeroStatusColor : fillColor;
            fillSr.sortingLayerID = layerId;
            fillSr.sortingOrder = baseOrder + 1001;

            if (dual)
            {
                var troopBg = new GameObject("troop_bg", typeof(SpriteRenderer));
                troopBg.transform.SetParent(hpBar.transform, false);
                var troopBgSr = troopBg.GetComponent<SpriteRenderer>();
                troopBgSr.sprite = bgUsesAsset
                    ? bgSprite
                    : GetOrCreateWhitePixelSprite(false);
                troopBgSr.color = bgUsesAsset
                    ? Color.white
                    : new Color(0f, 0f, 0f, 0.9f);
                troopBgSr.sortingLayerID = layerId;
                troopBgSr.sortingOrder = baseOrder + 1002;

                var troopFill = new GameObject(
                    "troop_fill",
                    typeof(SpriteRenderer));
                troopFill.transform.SetParent(hpBar.transform, false);
                var troopFillSr =
                    troopFill.GetComponent<SpriteRenderer>();
                troopFillSr.sprite = GetOrCreateWhitePixelSprite(true);
                troopFillSr.color = TroopStatusColor;
                troopFillSr.sortingLayerID = layerId;
                troopFillSr.sortingOrder = baseOrder + 1003;
            }

            LayoutHpBar(hpBar.transform, baseSr);
            hpBar.SetActive(true);
            return hpBar;
        }

        private static float LayoutHpBarRow(
            Transform bg,
            Transform fill,
            SpriteRenderer baseSr,
            float centerY,
            float height,
            int orderOffset)
        {
            float oldMax =
                bg != null
                    ? Mathf.Max(0.0001f, bg.localScale.x)
                    : 1f;
            float pct = fill != null
                ? Mathf.Clamp01(fill.localScale.x / oldMax)
                : 1f;
            var bgSr =
                bg != null
                    ? bg.GetComponent<SpriteRenderer>()
                    : null;
            var fillSr =
                fill != null
                    ? fill.GetComponent<SpriteRenderer>()
                    : null;
            var bgScale = ScaleForWorldSize(
                bgSr != null ? bgSr.sprite : null,
                HP_BAR_WIDTH,
                height);
            var fillScale = ScaleForWorldSize(
                fillSr != null ? fillSr.sprite : null,
                HP_BAR_WIDTH,
                height);
            if (bg != null)
            {
                bg.localPosition =
                    new Vector3(0f, centerY, 0f);
                bg.localScale = bgScale;
            }
            if (fill != null)
            {
                fill.localPosition = new Vector3(
                    -HP_BAR_WIDTH * 0.5f,
                    centerY,
                    0f);
                fill.localScale = new Vector3(
                    fillScale.x * pct,
                    fillScale.y,
                    1f);
            }

            int layerId =
                baseSr != null ? baseSr.sortingLayerID : 0;
            int baseOrder =
                baseSr != null ? baseSr.sortingOrder : 0;
            if (bgSr != null)
            {
                bgSr.sortingLayerID = layerId;
                bgSr.sortingOrder = baseOrder + orderOffset;
            }
            if (fillSr != null)
            {
                fillSr.sortingLayerID = layerId;
                fillSr.sortingOrder =
                    baseOrder + orderOffset + 1;
            }
            return fillScale.x;
        }

        internal static float LayoutHpBar(Transform hpBar, SpriteRenderer baseSr)
        {
            if (hpBar == null) return 1f;
            var lp = hpBar.localPosition;
            // 自适应血条高度：置于精灵实际渲染顶部略上方，随 unit_screen_scale 等缩放自动跟随。
            //   旧固定 HP_BAR_LOCAL_Y=1.78 是全尺寸精灵的头顶经验值；缩放后精灵变矮，固定值会让血条飘高。
            //   精灵帧高固定（运行图统一），bounds.max.y 不随切帧漂移。
            var bg = hpBar.Find("bg");
            var fill = hpBar.Find("fill");
            var troopBg = hpBar.Find("troop_bg");
            var troopFill = hpBar.Find("troop_fill");
            bool dual = troopBg != null
                && troopFill != null
                && troopBg.gameObject.activeSelf
                && troopFill.gameObject.activeSelf;
            float barY = HP_BAR_LOCAL_Y;
            if (baseSr != null && baseSr.sprite != null && hpBar.parent != null)
            {
                barY = baseSr.bounds.max.y
                    - hpBar.parent.position.y
                    + HP_BAR_GAP
                    + (dual ? STATUS_BAR_ROW_OFFSET : 0f);
            }
            hpBar.localPosition = new Vector3(0f, barY, lp.z);

            float heroFullScale = LayoutHpBarRow(
                bg,
                fill,
                baseSr,
                dual ? STATUS_BAR_ROW_OFFSET : 0f,
                dual ? STATUS_BAR_DUAL_HEIGHT : HP_BAR_HEIGHT,
                1000);
            if (dual)
            {
                LayoutHpBarRow(
                    troopBg,
                    troopFill,
                    baseSr,
                    -STATUS_BAR_ROW_OFFSET,
                    STATUS_BAR_DUAL_HEIGHT,
                    1002);
            }
            return heroFullScale;
        }

        internal static Rect GetSpriteVisibleLocalRect(Sprite sprite)
        {
            if (sprite == null) return Rect.zero;
            int key = sprite.GetInstanceID();
            if (_spriteVisibleRectCache.TryGetValue(key, out var cached)) return cached;

            var fallback = Rect.MinMaxRect(sprite.bounds.min.x, sprite.bounds.min.y, sprite.bounds.max.x, sprite.bounds.max.y);
            try
            {
                var tex = sprite.texture;
                if (tex == null)
                {
                    _spriteVisibleRectCache[key] = fallback;
                    return fallback;
                }

                var texRect = sprite.textureRect;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(texRect.xMin), 0, tex.width);
                int x1 = Mathf.Clamp(Mathf.CeilToInt(texRect.xMax), 0, tex.width);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(texRect.yMin), 0, tex.height);
                int y1 = Mathf.Clamp(Mathf.CeilToInt(texRect.yMax), 0, tex.height);
                var pixels = tex.GetPixels32();
                int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

                for (int y = y0; y < y1; y++)
                {
                    int row = y * tex.width;
                    for (int x = x0; x < x1; x++)
                    {
                        if (pixels[row + x].a <= 8) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }

                if (minX == int.MaxValue)
                {
                    _spriteVisibleRectCache[key] = fallback;
                    return fallback;
                }

                float ppu = sprite.pixelsPerUnit;
                float lx0 = sprite.bounds.min.x + (minX - texRect.xMin) / ppu;
                float lx1 = sprite.bounds.min.x + (maxX + 1 - texRect.xMin) / ppu;
                float ly0 = sprite.bounds.min.y + (minY - texRect.yMin) / ppu;
                float ly1 = sprite.bounds.min.y + (maxY + 1 - texRect.yMin) / ppu;
                var rect = Rect.MinMaxRect(lx0, ly0, lx1, ly1);
                _spriteVisibleRectCache[key] = rect;
                return rect;
            }
            catch
            {
                _spriteVisibleRectCache[key] = fallback;
                return fallback;
            }
        }

        private static long NextHandle() => _handleCounter++;

        private static void EnsureProjectilePoolConfig()
        {
            if (_projectilePoolCfgLoaded == 1) return;
            _projectilePoolCfgLoaded = 1;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var row = cm.GetTableInfo("GameConfig", "key", "max_projectiles");
                    if (row != null) _projectileMaxPool = cm.GetValue<int>(row, "value", 30);
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[BattleBridge] 读 max_projectiles 失败: {e.Message}"); }
        }

        /// <summary>投射物池统计（Lua / 测试 / Profiler 可读）。</summary>
        public static int Battle_GetProjectilePoolHits() => _projectilePoolHits;
        public static int Battle_GetProjectilePoolMisses() => _projectilePoolMisses;
        public static int Battle_GetProjectilePoolFree() => _projectilePool.Count;

        /// <summary>由 ProjectileTicker 在命中 / 超时 / 目标丢失时调用。回池或销毁（池满）。</summary>
        public static void RecycleProjectile(long handle)
        {
            if (handle == 0) return;
            if (!_projectiles.TryGetValue(handle, out var p) || p == null) return;
            _projectiles.Remove(handle);

            var go = p.gameObject;
            if (go == null) return;

            // 标记 + 重置组件状态
            p.PooledRecycled = true;
            p.Reset();

            // 池未满 → 入池（SetActive(false) 即可，下次复用）
            if (_projectilePool.Count < _projectileMaxPool)
            {
                go.SetActive(false);
                _projectilePool.Push(go);
            }
            else
            {
                Object.Destroy(go);
            }
        }

        /// <summary>由 BattleSceneController.OnDisable 调用，清空所有句柄 + 销毁 GameObject。</summary>
        public static void OnBattleSceneExit()
        {
            foreach (var kv in _units)
            {
                ClearHandleAnimationState(kv.Key);
                var squad = kv.Value != null
                    ? kv.Value.GetComponent<SquadVisualController>()
                    : null;
                if (squad != null) squad.Clear();
                if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
                try { HitFeedback.UnregisterHandle(kv.Key); } catch { /* silent */ }
            }
            _units.Clear();

            _unitOverlaySlots.Clear();
            _missingStatusViewWarnings.Clear();

            foreach (var kv in _projectiles)
            {
                if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
            }
            _projectiles.Clear();

            // Step 11 池：场景退出时一并销毁
            while (_projectilePool.Count > 0)
            {
                var go = _projectilePool.Pop();
                if (go != null) Object.Destroy(go);
            }
            _projectilePoolHits = 0;
            _projectilePoolMisses = 0;
        }

        // ============ 单位 5 方法 ============

        /// <summary>
        /// 实例化一个单位 GameObject（兵种/武将/建筑）。
        /// 业务 Lua 后续可调 SetSprite / PlayAnim 等。
        /// 返回 long handle（不为 0；0 = 失败）。
        /// </summary>
        public static long Battle_SpawnUnit(int npcId, int row, int col)
        {
            return SpawnUnitInternal(npcId, row, col, UnitTeamOwn);
        }

        /// <summary>
        /// 实例化带运行时敌我阵营的 UnitView。
        /// team=0 为己方（Tower 层/绿血条），team=1 为敌方（Enemy 层/红血条）。
        /// 业务层仍以 own/enemy 表达，int 仅作为 Lua→C# 桥接协议。
        /// </summary>
        public static long Battle_SpawnUnitForTeam(int npcId, int row, int col, int team)
        {
            if (team != UnitTeamOwn && team != UnitTeamEnemy)
            {
                if (_invalidUnitSpawnTeamsWarned.Add(team))
                    Debug.LogWarning($"[BattleBridge] Battle_SpawnUnitForTeam team 非法：{team}（仅支持 0=own / 1=enemy）");
                return 0;
            }

            return SpawnUnitInternal(npcId, row, col, team);
        }

        private static long SpawnUnitInternal(int npcId, int row, int col, int team)
        {
            try
            {
                bool isEnemy = team == UnitTeamEnemy;
                var go = new GameObject($"Unit_{npcId}_h?");
                var wp = GridMap.CellToWorld(row, col);
                go.transform.position = new Vector3(wp.x, wp.y, 0f);

                // sprite_root 子节点（SpriteRenderer 挂在子节点上，UIFinder 能找到）— 消除"找不到 sprite_root"警告
                var spriteRoot = new GameObject("sprite_root");
                spriteRoot.transform.SetParent(go.transform, false);
                var sr = spriteRoot.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = isEnemy ? HDSortingLayers.Enemy : HDSortingLayers.Tower;
                sr.sortingOrder = GridSortingService.CalcSortingOrderForRow(row);

                // T203/T214 hp_bar（2026-06-03 抽 BuildHpBar 复用）：己方绿、敌方红，头顶 0.4。
                BuildHpBar(go, sr, 0.4f,
                    isEnemy
                        ? new Color(1f, 0.2f, 0.2f, 1f)
                        : new Color(0.2f, 1f, 0.2f, 1f),
                    dual: true);

                // shadow 占位子节点（UnitView.SetShadow 用），暂用空 GameObject
                var shadow = new GameObject("shadow");
                shadow.transform.SetParent(go.transform, false);
                shadow.SetActive(false);

                var view = go.AddComponent<UnitView>();
                var anim = go.AddComponent<SpriteAnimator>();

                // 2026-05-29 (Q1) — 读 npc.tab.anim_type 注入到 SpriteAnimator，决定后续 Battle_PlayAnim 走哪条路径
                anim.AnimType = ResolveAnimType(npcId);

                long h = NextHandle();
                view.Handle = h;
                view.Team = team;
                go.name = $"Unit_{npcId}_h{h}";
                _units[h] = view;
                _missingStatusViewWarnings.Remove(h);
                _visualFaceRight[h] = !isEnemy;
                view.SetFacing(!isEnemy);

                // Round 12 Issue 1/4 — 按 occupy 形状重定位 sprite_root + 重设 collider，
                // 使 sprite 覆盖整个 w×h 占位格、点任意占位格都能起手拖。
                var (fpW, fpH) = ResolveFootprint(npcId);
                view.SetFootprint(row, col, fpW, fpH);

                // 同步注册到 Agent E 的 HitFeedback 句柄表
                try { HitFeedback.RegisterHandle(h, go); } catch (System.Exception e) { Debug.LogWarning($"[BattleBridge] RegisterHandle 失败: {e.Message}"); }

                return h;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleBridge] SpawnUnit 失败（team={team}）: {e.Message}");
                return 0;
            }
        }

        public static void Battle_DestroyUnit(long handle)
        {
            if (handle == 0) return;
            ClearHandleAnimationState(handle);
            if (_units.TryGetValue(handle, out var view))
            {
                _units.Remove(handle);
                var squad = view != null
                    ? view.GetComponent<SquadVisualController>()
                    : null;
                if (squad != null) squad.Clear();
                try { HitFeedback.UnregisterHandle(handle); } catch { /* silent */ }
                if (view != null && view.gameObject != null) Object.Destroy(view.gameObject);
            }
            else if (_projectiles.TryGetValue(handle, out var p))
            {
                // 走池路径
                RecycleProjectile(handle);
            }
        }

        private static bool TryGetSquadController(
            long rootHandle,
            bool create,
            out SquadVisualController controller)
        {
            controller = null;
            if (!_units.TryGetValue(rootHandle, out var view) || view == null) return false;
            controller = view.GetComponent<SquadVisualController>();
            if (controller == null && create)
                controller = view.gameObject.AddComponent<SquadVisualController>();
            return controller != null;
        }

        /// <summary>将兵种表现绑定到已有英雄根；不会新增任何公开单位 handle。</summary>
        public static bool Battle_SquadBind(long rootHandle, int troopNpcId, int team)
        {
            return TryGetSquadController(rootHandle, true, out var controller)
                && controller.Bind(troopNpcId, team);
        }

        public static bool Battle_SquadSetFormation(
            long rootHandle,
            int visualCount,
            string facing,
            int reflowMs)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.SetFormation(visualCount, facing, reflowMs);
        }

        public static bool Battle_SquadPlayState(
            long rootHandle,
            string stateName,
            float speedMult)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.PlayState(stateName, speedMult);
        }

        public static bool Battle_SquadPlayAttack(
            long rootHandle,
            string stateName,
            float speedMult,
            long targetHandle,
            string projectileKey,
            int pulseId)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.PlayAttack(
                    stateName,
                    speedMult,
                    targetHandle,
                    projectileKey,
                    pulseId);
        }

        public static bool Battle_SquadApplyCasualties(
            long rootHandle,
            int newVisualCount,
            int deathVisualCount,
            string sourceDirection,
            string deathState,
            int reflowMs,
            int deathHoldMs)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.ApplyCasualties(
                    newVisualCount,
                    deathVisualCount,
                    sourceDirection,
                    deathState,
                    reflowMs,
                    deathHoldMs);
        }

        public static bool Battle_SquadRefill(
            long rootHandle,
            int newVisualCount,
            int fadeMs,
            string stateName)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.Refill(newVisualCount, fadeMs, stateName);
        }

        public static bool Battle_SquadDetachHeroAndRetreat(
            long rootHandle,
            string deathState,
            int deathHoldMs)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                && controller.DetachHeroAndRetreat(deathState, deathHoldMs);
        }

        public static int Battle_SquadGetVisualCount(long rootHandle)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                ? controller.GetVisualCount()
                : 0;
        }

        public static int Battle_SquadGetGhostCount(long rootHandle)
        {
            return TryGetSquadController(rootHandle, false, out var controller)
                ? controller.GetGhostCount()
                : 0;
        }

        public static void Battle_SquadClear(long rootHandle)
        {
            if (TryGetSquadController(rootHandle, false, out var controller))
                controller.Clear();
        }

        // npc.tab.anim_type 反查。
        // 查不到默认 "atFrame"。Spine 路径目前为 stub（fallback frame），后续接 spine-unity 替换。
        // 注意 Enum_ANIM_TYPE 列由 TabParser/EnumRegistry 转成 int 存储（atFrame=1 / atSpine=2），不是 string。
        private const int ANIM_TYPE_FRAME = 1;
        private const int ANIM_TYPE_SPINE = 2;
        private static string ResolveAnimType(int npcId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return "atFrame";
                cm.LoadIfNeeded();
                var npcRow = cm.GetTableInfo("npc", "id", npcId.ToString());
                if (npcRow == null) return "atFrame";
                int v = cm.GetValue<int>(npcRow, "anim_type", ANIM_TYPE_FRAME);
                return v == ANIM_TYPE_SPINE ? "atSpine" : "atFrame";
            }
            catch { return "atFrame"; }
        }

        /// <summary>
        /// 仅供 SquadVisualController 创建无句柄兵模时读取 NPC 的表现元数据。
        /// 该入口不会注册单位、创建碰撞体或触及 Lua/Match。
        /// </summary>
        internal static bool TryGetVisualNpcInfo(
            int npcId,
            out string baseKey,
            out string animType)
        {
            baseKey = string.Empty;
            animType = "atFrame";
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return false;
                cm.LoadIfNeeded();
                var npcRow = cm.GetTableInfo("npc", "id", npcId);
                if (npcRow == null) return false;
                baseKey = cm.GetValue<string>(npcRow, "sprite_key", string.Empty);
                animType = ResolveAnimType(npcId);
                return !string.IsNullOrEmpty(baseKey);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>纯表现投射物只读取目标根 Transform，不暴露 UnitView 或 handle 表给 Lua。</summary>
        internal static bool TryGetUnitVisualTransform(long handle, out Transform transform)
        {
            transform = null;
            if (!_units.TryGetValue(handle, out var view) || view == null) return false;
            transform = view.transform;
            return transform != null;
        }

        // npc.tab occupy_id → occupy.tab width/height。查不到任一环节都回落 1×1。
        // 注：这是渲染层几何（collider / sprite 包围盒）所需的底层信息，
        //     与 EnsureProjectilePoolConfig 同属 BattleBridge 已有的"spawn 时读配置"模式。
        private static (int w, int h) ResolveFootprint(int npcId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return (1, 1);
                cm.LoadIfNeeded();

                var npcRow = cm.GetTableInfo("npc", "id", npcId);
                if (npcRow == null) return (1, 1);
                int occupyId = cm.GetValue<int>(npcRow, "occupy_id", 1);
                if (occupyId <= 0) return (1, 1);

                var occRow = cm.GetTableInfo("occupy", "id", occupyId);
                if (occRow == null) return (1, 1);
                int w = cm.GetValue<int>(occRow, "width", 1);
                int h = cm.GetValue<int>(occRow, "height", 1);
                return (w < 1 ? 1 : w, h < 1 ? 1 : h);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BattleBridge] ResolveFootprint({npcId}) 失败: {e.Message}");
                return (1, 1);
            }
        }

        // 动画帧缓存：(baseKey|state) → 已加载的帧数组。
        // 帧数按文件实际存在自动探测（{base}_{state}_{i}.png 从 0 递增到首个缺失），不再硬编码 —
        // 美术每个状态的帧数由 art_gen 按 roster 生成，可能各不相同。
        private static readonly Dictionary<string, Sprite[]> _animFrameCache = new Dictionary<string, Sprite[]>();
        private const int ANIM_MAX_FRAMES = 16;  // 单 state 最多 16 帧；现行十态合同为 10×16=160 帧，仍按文件实际存在数量探测。

        // anim json v1：仅描述扁平帧，按 base key 进程内缓存。null 值同时缓存“文件不存在/文件无效”，
        // 避免每次播放重复触碰热更文件系统；进程重启后自然重读。
        private sealed class AnimJsonDefinition
        {
            [JsonProperty("version", Required = Required.Always)] public int Version { get; set; }
            [JsonProperty("base_key", Required = Required.Always)] public string BaseKey { get; set; }
            [JsonProperty("states", Required = Required.Always)] public Dictionary<string, AnimJsonState> States { get; set; }
        }

        private sealed class AnimJsonState
        {
            [JsonProperty("loop", Required = Required.Always)] public bool Loop { get; set; }
            [JsonProperty("frames", Required = Required.Always)] public List<AnimJsonFrame> Frames { get; set; }
            [JsonProperty("events")] public List<AnimJsonEvent> Events { get; set; }
        }

        private sealed class AnimJsonFrame
        {
            [JsonProperty("img", Required = Required.Always)] public int Img { get; set; }
            [JsonProperty("dur", Required = Required.Always)] public float Dur { get; set; }
        }

        private sealed class AnimJsonEvent
        {
            [JsonProperty("frame", Required = Required.Always)] public int Frame { get; set; }
            [JsonProperty("name", Required = Required.Always)] public string Name { get; set; }
        }

        private sealed class TimedAnimClip
        {
            public Sprite[] Frames;
            public float[] Durations;
            public bool Looping;
            public List<AnimJsonEvent> Events;
            public float RawDurationTotal;
            public float SpeedMultiplier;
        }

        private static readonly Dictionary<string, AnimJsonDefinition> _animJsonCache =
            new Dictionary<string, AnimJsonDefinition>();
        private static readonly HashSet<string> _bundleMissingStateWarnings = new HashSet<string>();
        private static readonly HashSet<string> _bundleBuildWarnings = new HashSet<string>();
        // 动画时序查询必须独立于场上对象和播放回调；此表只去重兼容 fallback 的提示。
        private static readonly HashSet<string> _animMetadataWarnings = new HashSet<string>();
        private static readonly HashSet<string> _formalStatePlaceholderWarnings = new HashSet<string>();
        private static readonly HashSet<string> _runtimeOverlayWarnings = new HashSet<string>();
        private static readonly HashSet<string> _runtimeOverlayApiWarnings = new HashSet<string>();

        private static string NormalizeAnimBaseKey(string baseKey)
        {
            return string.IsNullOrEmpty(baseKey) ? string.Empty : baseKey.Replace('\\', '/').Trim('/');
        }

        private static string AnimJsonPath(string normalizedBaseKey)
        {
            int slash = normalizedBaseKey.LastIndexOf('/');
            string dir = slash >= 0 ? normalizedBaseKey.Substring(0, slash + 1) : string.Empty;
            string leaf = slash >= 0 ? normalizedBaseKey.Substring(slash + 1) : normalizedBaseKey;
            return $"resources/art/{dir}{leaf}.anim.json";
        }

        private static AnimJsonDefinition GetAnimJson(string baseKey)
        {
            string normalized = NormalizeAnimBaseKey(baseKey);
            if (string.IsNullOrEmpty(normalized)) return null;
            if (_animJsonCache.TryGetValue(normalized, out var cached)) return cached;

            string path = AnimJsonPath(normalized);
            try
            {
                if (!HeroDefense.Engine.Host.ResourceHost.Exists(path))
                {
                    _animJsonCache[normalized] = null; // 缺文件是正常旧路径，静默缓存。
                    return null;
                }

                string json = HeroDefense.Engine.Host.ResourceHost.ReadText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return CacheInvalidAnimJson(normalized, path, "文件为空");

                var definition = JsonConvert.DeserializeObject<AnimJsonDefinition>(json);
                if (!ValidateAnimJson(definition, normalized, out string reason))
                    return CacheInvalidAnimJson(normalized, path, reason);

                _animJsonCache[normalized] = definition;
                return definition;
            }
            catch (System.Exception e)
            {
                return CacheInvalidAnimJson(normalized, path, e.Message);
            }
        }

        private static AnimJsonDefinition CacheInvalidAnimJson(string normalizedBaseKey, string path, string reason)
        {
            _animJsonCache[normalizedBaseKey] = null;
            Debug.LogWarning($"[BattleBridge] anim json 无效，整文件回落均摊（{path}）：{reason}");
            return null;
        }

        // 仅保留历史调用方的右向别名。新 Match 域不再生成这些名字；不能把任意方向
        // 后缀剥成基础状态，否则会把缺失的正式动作静默伪装成另一个方向。
        private static string NormalizeActionStateAlias(string stateName)
        {
            if (stateName == "walk_right") return "walk";
            if (stateName == "attack_right") return "attack";
            if (stateName == "die_right") return "die";
            return stateName;
        }

        /// <summary>供无句柄兵模复用同一套白名单别名，不开放泛方向回退。</summary>
        internal static string NormalizeVisualActionState(string stateName)
        {
            return NormalizeActionStateAlias(stateName);
        }

        private static bool IsFormalActionState(string stateName)
        {
            return stateName == "idle_down"
                || stateName == "combat_idle"
                || stateName == "combat_idle_left"
                || stateName == "walk"
                || stateName == "walk_left"
                || stateName == "walk_up"
                || stateName == "walk_down"
                || stateName == "attack"
                || stateName == "attack_left"
                || stateName == "die"
                || stateName == "die_left";
        }

        // 这是唯一允许旧动画数据层使用的状态映射。它不适用于 idle_down，也不为
        // walk/attack/die 的左、上、下方向补帧或镜像。
        private static string LegacyPackageState(string semanticStateName)
        {
            if (semanticStateName == "combat_idle") return "idle";
            if (semanticStateName == "combat_idle_left") return "idle_left";
            return null;
        }

        private static bool TryResolveBundleState(
            AnimBundle bundle,
            string requestedStateName,
            out BundleState state,
            out string resolvedStateName)
        {
            state = null;
            resolvedStateName = NormalizeActionStateAlias(requestedStateName);
            if (bundle == null || string.IsNullOrEmpty(resolvedStateName)) return false;
            if (bundle.TryGetState(resolvedStateName, out state)) return true;

            string legacyStateName = LegacyPackageState(resolvedStateName);
            if (legacyStateName != null && bundle.TryGetState(legacyStateName, out state))
            {
                resolvedStateName = legacyStateName;
                return true;
            }
            return false;
        }

        private static bool TryResolveTimedState(
            AnimJsonDefinition definition,
            string requestedStateName,
            out AnimJsonState state,
            out string resolvedStateName)
        {
            state = null;
            resolvedStateName = NormalizeActionStateAlias(requestedStateName);
            if (definition == null || string.IsNullOrEmpty(resolvedStateName)) return false;
            if (definition.States.TryGetValue(resolvedStateName, out state)) return true;

            string legacyStateName = LegacyPackageState(resolvedStateName);
            if (legacyStateName != null && definition.States.TryGetValue(legacyStateName, out state))
            {
                resolvedStateName = legacyStateName;
                return true;
            }
            return false;
        }

        private static bool TryGetCompatibleFrameStorage(
            string baseKey,
            string requestedStateName,
            out bool usesAtlas,
            out string resolvedStateName)
        {
            resolvedStateName = NormalizeActionStateAlias(requestedStateName);
            if (TryGetFrameStorage(baseKey, resolvedStateName, out usesAtlas)) return true;

            string legacyStateName = LegacyPackageState(resolvedStateName);
            if (legacyStateName != null && TryGetFrameStorage(baseKey, legacyStateName, out usesAtlas))
            {
                resolvedStateName = legacyStateName;
                return true;
            }
            usesAtlas = false;
            return false;
        }

        private static bool TryGetCompatibleFlatFrames(
            string baseKey,
            string requestedStateName,
            out Sprite[] frames,
            out string resolvedStateName)
        {
            resolvedStateName = NormalizeActionStateAlias(requestedStateName);
            frames = GetAnimFrames(baseKey, resolvedStateName);
            if (frames.Length > 0) return true;

            string legacyStateName = LegacyPackageState(resolvedStateName);
            if (legacyStateName != null)
            {
                frames = GetAnimFrames(baseKey, legacyStateName);
                if (frames.Length > 0)
                {
                    resolvedStateName = legacyStateName;
                    return true;
                }
            }
            frames = new Sprite[0];
            return false;
        }

        private static void WarnAnimMetadataFallback(
            string baseKey,
            string stateName,
            string eventName,
            string reason)
        {
            string normalizedBaseKey = NormalizeAnimBaseKey(baseKey);
            string semanticStateName = NormalizeActionStateAlias(stateName);
            string warningKey = normalizedBaseKey + "|" + semanticStateName + "|" + eventName;
            if (_animMetadataWarnings.Add(warningKey))
            {
                Debug.LogWarning(
                    $"[BattleBridge] animation metadata fallback (base={normalizedBaseKey}, state={semanticStateName}, event={eventName}): {reason}");
            }
        }

        private static bool TryCopyRawDurations(BundleState state, out float[] durations)
        {
            durations = new float[0];
            if (state == null || state.Frames == null || state.Frames.Length == 0) return false;
            durations = new float[state.Frames.Length];
            for (int i = 0; i < durations.Length; i++)
            {
                float duration = state.Frames[i].Duration;
                if (duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                {
                    durations = new float[0];
                    return false;
                }
                durations[i] = duration;
            }
            return true;
        }

        private static bool TryCopyRawDurations(AnimJsonState state, out float[] durations)
        {
            durations = new float[0];
            if (state == null || state.Frames == null || state.Frames.Count == 0) return false;
            durations = new float[state.Frames.Count];
            for (int i = 0; i < durations.Length; i++)
            {
                float duration = state.Frames[i].Dur;
                if (duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                {
                    durations = new float[0];
                    return false;
                }
                durations[i] = duration;
            }
            return true;
        }

        // 元数据读取不创建或查找场上 GameObject；选择顺序与播放层保持 bundle -> timed json。
        private static bool TryGetRawStateDurations(
            string baseKey,
            string stateName,
            out float[] durations)
        {
            durations = new float[0];
            string normalizedBaseKey = NormalizeAnimBaseKey(baseKey);
            if (string.IsNullOrEmpty(normalizedBaseKey) || string.IsNullOrEmpty(stateName))
                return false;

            if (AnimBundleCache.TryGet(normalizedBaseKey, out var bundle)
                && TryResolveBundleState(bundle, stateName, out var bundleState, out _)
                && TryCopyRawDurations(bundleState, out durations))
                return true;

            var definition = GetAnimJson(normalizedBaseKey);
            return TryResolveTimedState(definition, stateName, out var timedState, out _)
                && TryCopyRawDurations(timedState, out durations);
        }

        private static bool TryFindEventTimelineIndex(
            BundleState state,
            string eventName,
            out int timelineIndex,
            out bool exactOne)
        {
            timelineIndex = -1;
            exactOne = false;
            if (state == null || state.Frames == null || state.Frames.Length == 0) return false;
            int count = 0;
            if (state.Events != null)
            {
                for (int i = 0; i < state.Events.Length; i++)
                {
                    var frameEvent = state.Events[i];
                    if (!string.Equals(frameEvent.Name, eventName, System.StringComparison.Ordinal)) continue;
                    count++;
                    timelineIndex = frameEvent.TimelineIndex;
                }
            }
            exactOne = count == 1
                && timelineIndex >= 0
                && timelineIndex < state.Frames.Length;
            if (!exactOne) timelineIndex = -1;
            return true;
        }

        private static bool TryFindEventTimelineIndex(
            AnimJsonState state,
            string eventName,
            out int timelineIndex,
            out bool exactOne)
        {
            timelineIndex = -1;
            exactOne = false;
            if (state == null || state.Frames == null || state.Frames.Count == 0) return false;
            int count = 0;
            if (state.Events != null)
            {
                for (int i = 0; i < state.Events.Count; i++)
                {
                    var frameEvent = state.Events[i];
                    if (!string.Equals(frameEvent.Name, eventName, System.StringComparison.Ordinal)) continue;
                    count++;
                    timelineIndex = frameEvent.Frame;
                }
            }
            exactOne = count == 1
                && timelineIndex >= 0
                && timelineIndex < state.Frames.Count;
            if (!exactOne) timelineIndex = -1;
            return true;
        }

        private static bool TryGetEventTimelineIndex(
            string baseKey,
            string stateName,
            string eventName,
            out int timelineIndex,
            out bool exactOne)
        {
            timelineIndex = -1;
            exactOne = false;
            string normalizedBaseKey = NormalizeAnimBaseKey(baseKey);
            if (string.IsNullOrEmpty(normalizedBaseKey)
                || string.IsNullOrEmpty(stateName)
                || string.IsNullOrEmpty(eventName))
                return false;

            if (AnimBundleCache.TryGet(normalizedBaseKey, out var bundle)
                && TryResolveBundleState(bundle, stateName, out var bundleState, out _))
                return TryFindEventTimelineIndex(bundleState, eventName, out timelineIndex, out exactOne);

            var definition = GetAnimJson(normalizedBaseKey);
            if (TryResolveTimedState(definition, stateName, out var timedState, out _))
                return TryFindEventTimelineIndex(timedState, eventName, out timelineIndex, out exactOne);
            return false;
        }

        public static int Battle_GetAnimStateDurationMs(
            string baseKey,
            string stateName,
            int fallbackMs)
        {
            if (!TryGetRawStateDurations(baseKey, stateName, out var durations))
            {
                WarnAnimMetadataFallback(baseKey, stateName, "duration", "state unavailable");
                return fallbackMs;
            }

            double totalSeconds = 0d;
            for (int i = 0; i < durations.Length; i++) totalSeconds += durations[i];
            double rawMilliseconds = totalSeconds * 1000d;
            if (rawMilliseconds <= 0d || rawMilliseconds > int.MaxValue
                || double.IsNaN(rawMilliseconds) || double.IsInfinity(rawMilliseconds))
            {
                WarnAnimMetadataFallback(baseKey, stateName, "duration", "invalid total duration");
                return fallbackMs;
            }
            return (int)System.Math.Round(
                rawMilliseconds,
                System.MidpointRounding.AwayFromZero);
        }

        public static int Battle_GetAnimEventRatioBp(
            string baseKey,
            string stateName,
            string eventName,
            int fallbackBp)
        {
            if (!TryGetRawStateDurations(baseKey, stateName, out var durations)
                || !TryGetEventTimelineIndex(
                    baseKey,
                    stateName,
                    eventName,
                    out int timelineIndex,
                    out bool exactOne)
                || !exactOne)
            {
                WarnAnimMetadataFallback(baseKey, stateName, eventName, "event unavailable or non-unique");
                return fallbackBp;
            }

            double total = 0d;
            double beforeEvent = 0d;
            for (int i = 0; i < durations.Length; i++)
            {
                if (i < timelineIndex) beforeEvent += durations[i];
                total += durations[i];
            }
            if (total <= 0d || double.IsNaN(total) || double.IsInfinity(total))
            {
                WarnAnimMetadataFallback(baseKey, stateName, eventName, "invalid total duration");
                return fallbackBp;
            }

            int ratioBp = (int)System.Math.Round(
                beforeEvent / total * 10000d,
                System.MidpointRounding.AwayFromZero);
            if (ratioBp < 1) return 1;
            if (ratioBp > 9999) return 9999;
            return ratioBp;
        }

        private static bool ValidateAnimJson(AnimJsonDefinition definition, string expectedBaseKey, out string reason)
        {
            if (definition == null)
            {
                reason = "根对象为空";
                return false;
            }
            if (definition.Version != 1)
            {
                reason = $"version 必须为 1，实际 {definition.Version}";
                return false;
            }
            if (string.IsNullOrWhiteSpace(definition.BaseKey)
                || NormalizeAnimBaseKey(definition.BaseKey) != expectedBaseKey)
            {
                reason = $"base_key 必须精确匹配 {expectedBaseKey}";
                return false;
            }
            if (definition.States == null || definition.States.Count == 0)
            {
                reason = "states 缺失或为空";
                return false;
            }

            foreach (var pair in definition.States)
            {
                string stateName = pair.Key;
                var state = pair.Value;
                if (string.IsNullOrWhiteSpace(stateName) || state == null)
                {
                    reason = "state 名为空或 state 对象为空";
                    return false;
                }
                if (state.Frames == null || state.Frames.Count == 0)
                {
                    reason = $"state '{stateName}' 的 frames 缺失或为空";
                    return false;
                }
                for (int i = 0; i < state.Frames.Count; i++)
                {
                    var frame = state.Frames[i];
                    if (frame == null || frame.Img < 0)
                    {
                        reason = $"state '{stateName}' frame[{i}].img 非法";
                        return false;
                    }
                    if (frame.Dur <= 0f || float.IsNaN(frame.Dur) || float.IsInfinity(frame.Dur))
                    {
                        reason = $"state '{stateName}' frame[{i}].dur 必须为有限正数";
                        return false;
                    }
                }
                if (state.Events == null) continue;
                for (int i = 0; i < state.Events.Count; i++)
                {
                    var frameEvent = state.Events[i];
                    if (frameEvent == null || frameEvent.Frame < 0 || frameEvent.Frame >= state.Frames.Count)
                    {
                        reason = $"state '{stateName}' event[{i}].frame 越界";
                        return false;
                    }
                    if (string.IsNullOrWhiteSpace(frameEvent.Name))
                    {
                        reason = $"state '{stateName}' event[{i}].name 为空";
                        return false;
                    }
                }
            }

            reason = null;
            return true;
        }

        private static bool TryGetFrameStorage(string baseKey, string state, out bool usesAtlas)
        {
            var atlas = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                $"resources/art/{baseKey}/atlas/{state}_0.png", false);
            if (atlas != null)
            {
                usesAtlas = true;
                return true;
            }
            var flat = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                $"resources/art/{baseKey}_{state}_0.png", false);
            usesAtlas = false;
            return flat != null;
        }

        // 与 ResolveAnimFrames 的 direct → 基础方向态 → idle 回退顺序一致；任一级命中 atlas
        // （即使同名扁平帧也并存）都沿用旧 atlas 优先路径，不查 json。
        private static bool ResolvesToAtlasFrames(string baseKey, string state)
        {
            return TryGetCompatibleFrameStorage(baseKey, state, out bool usesAtlas, out _)
                && usesAtlas;
        }

        private static float NormalizeTimedSpeed(float speedMult)
        {
            return speedMult > 0f && !float.IsNaN(speedMult) && !float.IsInfinity(speedMult)
                ? speedMult
                : 1f;
        }

        private static float[] ScaleTimedDurations(float[] rawDurations, float speedMult,
            out float normalizedMultiplier)
        {
            normalizedMultiplier = NormalizeTimedSpeed(speedMult);
            var durations = new float[rawDurations.Length];
            bool scaledValid = true;
            for (int i = 0; i < rawDurations.Length; i++)
            {
                double scaled = (double)rawDurations[i] / normalizedMultiplier;
                if (scaled <= 0d || scaled > float.MaxValue
                    || double.IsNaN(scaled) || double.IsInfinity(scaled))
                {
                    scaledValid = false;
                    break;
                }
                durations[i] = (float)scaled;
            }
            if (!scaledValid)
            {
                // 极端但为正的倍率导致 float 溢出时按旧兼容语义视为 1。
                normalizedMultiplier = 1f;
                for (int i = 0; i < rawDurations.Length; i++)
                    durations[i] = rawDurations[i];
            }
            return durations;
        }

        private static bool TryBuildTimedClip(string baseKey, string stateName, float speedMult,
            out TimedAnimClip clip, out string playbackStateName)
        {
            clip = null;
            playbackStateName = NormalizeActionStateAlias(stateName);
            string normalized = NormalizeAnimBaseKey(baseKey);
            if (string.IsNullOrEmpty(normalized) || string.IsNullOrEmpty(playbackStateName)) return false;
            if (ResolvesToAtlasFrames(normalized, playbackStateName)) return false;

            var definition = GetAnimJson(normalized);
            if (!TryResolveTimedState(
                    definition,
                    playbackStateName,
                    out var state,
                    out string resourceStateName))
                return false;

            int count = state.Frames.Count;
            var frames = new Sprite[count];
            var rawDurations = new float[count];
            double rawTotal = 0d;
            for (int i = 0; i < count; i++)
            {
                var entry = state.Frames[i];
                frames[i] = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                    $"resources/art/{normalized}_{resourceStateName}_{entry.Img}.png", false);
                if (frames[i] == null)
                {
                    CacheInvalidAnimJson(normalized, AnimJsonPath(normalized),
                        $"state '{resourceStateName}' frame[{i}] 引用的 img={entry.Img} 不存在");
                    return false;
                }
                rawDurations[i] = entry.Dur;
                rawTotal += entry.Dur;
            }
            if (rawTotal <= 0d || rawTotal > float.MaxValue || double.IsNaN(rawTotal) || double.IsInfinity(rawTotal))
            {
                CacheInvalidAnimJson(normalized, AnimJsonPath(normalized),
                    $"state '{resourceStateName}' 总时长非法");
                return false;
            }

            var durations = ScaleTimedDurations(rawDurations, speedMult, out float mult);

            clip = new TimedAnimClip
            {
                Frames = frames,
                Durations = durations,
                Looping = ResolveStateLooping(playbackStateName, state.Loop),
                Events = state.Events,
                RawDurationTotal = (float)rawTotal,
                SpeedMultiplier = mult,
            };
            return true;
        }

        private static bool TryBuildBundleClip(AnimBundle bundle, BundleState state, float speedMult,
            out BundleClip clip, out string reason)
        {
            clip = null;
            reason = null;
            if (bundle == null || state == null || state.Frames == null || state.Frames.Length == 0)
            {
                reason = "bundle/state 为空";
                return false;
            }

            int count = state.Frames.Length;
            var frames = new Sprite[count];
            var rawDurations = new float[count];
            for (int i = 0; i < count; i++)
            {
                var entry = state.Frames[i];
                frames[i] = bundle.GetFrameSprite(entry.FramePoolIndex);
                if (frames[i] == null)
                {
                    reason = $"timeline frame[{i}] 帧池引用不可用";
                    return false;
                }
                rawDurations[i] = entry.Duration;
            }

            var durations = ScaleTimedDurations(rawDurations, speedMult, out _);
            var entryTimes = new float[count];
            double total = 0d;
            for (int i = 0; i < count; i++)
            {
                if (total > float.MaxValue)
                {
                    reason = "缩放后总时长溢出";
                    return false;
                }
                entryTimes[i] = (float)total;
                total += durations[i];
            }
            if (total <= 0d || total > float.MaxValue
                || double.IsNaN(total) || double.IsInfinity(total))
            {
                reason = "缩放后总时长非法";
                return false;
            }

            BundleSelfKey[] selfKeys = null;
            if (state.SelfKeys != null && state.SelfKeys.Length > 0)
            {
                selfKeys = new BundleSelfKey[state.SelfKeys.Length];
                for (int i = 0; i < selfKeys.Length; i++)
                {
                    var source = state.SelfKeys[i];
                    selfKeys[i] = new BundleSelfKey
                    {
                        Time = entryTimes[source.TimelineIndex],
                        Dx = source.Dx,
                        Dy = source.Dy,
                        Alpha = source.Alpha,
                    };
                }
            }

            BundleTrackClip[] tracks = null;
            if (state.Tracks != null && state.Tracks.Length > 0)
            {
                tracks = new BundleTrackClip[state.Tracks.Length];
                for (int i = 0; i < tracks.Length; i++)
                {
                    var sourceTrack = state.Tracks[i];
                    int cellCount = sourceTrack.Cells.Length;
                    var track = new BundleTrackClip
                    {
                        Sprites = new Sprite[cellCount],
                        TimelineIndices = new int[cellCount],
                        Times = new float[cellCount],
                        Dx = new float[cellCount],
                        Dy = new float[cellCount],
                        ScaleX = new float[cellCount],
                        ScaleY = new float[cellCount],
                        Alpha = new float[cellCount],
                        Above = sourceTrack.Above,
                    };

                    for (int k = 0; k < cellCount; k++)
                    {
                        var sourceCell = sourceTrack.Cells[k];
                        int timelineIndex = sourceCell.TimelineIndex;
                        track.Sprites[k] = bundle.ResolveTrackSprite(
                            sourceTrack, sourceCell.FrameIndex);
                        track.TimelineIndices[k] = timelineIndex;
                        track.Times[k] = entryTimes[timelineIndex];
                        track.Dx[k] = sourceCell.Dx;
                        track.Dy[k] = sourceCell.Dy;
                        track.ScaleX[k] = sourceCell.ScaleX;
                        track.ScaleY[k] = sourceCell.ScaleY;
                        track.Alpha[k] = sourceCell.Alpha;
                    }

                    int lastTimelineIndex = sourceTrack.Cells[cellCount - 1].TimelineIndex;
                    track.EndTime = entryTimes[lastTimelineIndex] + durations[lastTimelineIndex];
                    tracks[i] = track;
                }
            }

            clip = new BundleClip
            {
                Frames = frames,
                Durations = durations,
                Looping = state.Loop,
                SelfKeys = selfKeys,
                Tracks = tracks,
                CanvasW = bundle.CanvasW,
                CanvasH = bundle.CanvasH,
                TotalDuration = (float)total,
                HideHp = state.HideHp,
            };
            return true;
        }

        private static bool TryBuildRuntimeOverlayClip(
            AnimBundle bundle, BundleState state, float speedMult,
            out RuntimeOverlayClip clip, out string reason)
        {
            clip = null;
            reason = null;
            if (bundle == null || state == null || state.Frames == null || state.Frames.Length == 0)
            {
                reason = "bundle/state 为空";
                return false;
            }

            int count = state.Frames.Length;
            var frames = new Sprite[count];
            var rawDurations = new float[count];
            for (int i = 0; i < count; i++)
            {
                var entry = state.Frames[i];
                frames[i] = bundle.GetFrameSprite(entry.FramePoolIndex);
                if (frames[i] == null)
                {
                    reason = $"timeline frame[{i}] 帧池引用不可用";
                    return false;
                }
                rawDurations[i] = entry.Duration;
            }

            var durations = ScaleTimedDurations(rawDurations, speedMult, out _);
            double total = 0d;
            for (int i = 0; i < durations.Length; i++) total += durations[i];
            if (total <= 0d || total > float.MaxValue
                || double.IsNaN(total) || double.IsInfinity(total))
            {
                reason = "缩放后总时长非法";
                return false;
            }

            clip = new RuntimeOverlayClip
            {
                Frames = frames,
                Durations = durations,
                Looping = state.Loop,
                Above = state.Above,
                TotalDuration = (float)total,
            };
            return true;
        }

        private static RuntimeOverlayClip[] BuildRuntimeOverlayClips(
            long handle, string resolvedStateName, float speedMult)
        {
            if (!_unitOverlaySlots.TryGetValue(handle, out var slots) || slots == null)
                return null;

            RuntimeOverlayClip[] clips = null;
            for (int i = 0; i < RuntimeOverlaySlotCount; i++)
            {
                string baseKey = slots[i];
                if (string.IsNullOrEmpty(baseKey)) continue;
                if (!AnimBundleCache.TryGet(baseKey, out var overlayBundle))
                {
                    string warningKey = "bundle|" + baseKey;
                    if (_runtimeOverlayWarnings.Add(warningKey))
                    {
                        Debug.LogWarning(
                            $"[BattleBridge] 运行时 overlay 包无效，槽位静默跳过（base={baseKey}）");
                    }
                    continue;
                }

                // 精确匹配实际命中的本体状态；overlay 缺状态是正常稀疏数据，不记录 warning。
                if (!overlayBundle.TryGetState(resolvedStateName, out var overlayState))
                    continue;
                if (!TryBuildRuntimeOverlayClip(
                    overlayBundle, overlayState, speedMult, out var clip, out string reason))
                {
                    string warningKey = "clip|" + baseKey + "|" + resolvedStateName;
                    if (_runtimeOverlayWarnings.Add(warningKey))
                    {
                        Debug.LogWarning(
                            $"[BattleBridge] 运行时 overlay 状态构建失败，槽位静默跳过（base={baseKey}, state={resolvedStateName}）：{reason}");
                    }
                    continue;
                }

                if (clips == null) clips = new RuntimeOverlayClip[RuntimeOverlaySlotCount];
                clips[i] = clip;
            }
            return clips;
        }

        // 方向态在包内先退基础态（walk_up→walk），与旧链 ResolveAnimFrames 的方向回退语义一致；
        // 真四方向帧入包后直接命中基础/方向态，不会走到这里。
        private static bool TryPlayBundleAnim(long handle, SpriteAnimator anim, string stateName,
            float speedMult)
        {
            if (anim == null || string.IsNullOrEmpty(stateName)
                || !AnimBundleCache.TryGet(anim.SpriteBaseKey, out var bundle))
                return false;

            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (!TryResolveBundleState(
                    bundle,
                    playbackStateName,
                    out var state,
                    out string resourceStateName))
            {
                string warningKey = bundle.BaseKey + "|" + playbackStateName;
                if (_bundleMissingStateWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] 动画包缺 state，当前状态回落旧链（base={bundle.BaseKey}, state={stateName}）");
                }
                return false;
            }
            ApplyNativeDirectionRendering(handle, playbackStateName);

            if (!TryBuildBundleClip(bundle, state, speedMult, out var clip, out string reason))
            {
                string warningKey = bundle.BaseKey + "|" + playbackStateName;
                if (_bundleBuildWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] 动画包状态构建失败，当前状态回落旧链（base={bundle.BaseKey}, state={stateName}）：{reason}");
                }
                return false;
            }
            clip.Looping = ResolveStateLooping(playbackStateName, clip.Looping);

            if (state.Events != null && state.Events.Length > 0)
            {
                clip.OnFrameEnter = frameIndex =>
                {
                    for (int i = 0; i < state.Events.Length; i++)
                    {
                        var frameEvent = state.Events[i];
                        if (frameEvent.TimelineIndex == frameIndex)
                        {
                            HeroDefense.Engine.Host.LuaHost.CallGlobal(
                                "Anim_OnFrameEvent", handle, playbackStateName, frameEvent.Name);
                        }
                    }
                };
            }

            var runtimeOverlays =
                BuildRuntimeOverlayClips(handle, resourceStateName, speedMult);
            anim.PlayBundle(playbackStateName, clip, runtimeOverlays);
            SetHandleHpBarAnimHidden(handle, state.HideHp);
            RefreshAnimLayout(handle);
            return true;
        }

        private static bool TryGetBundleAnimLength(string baseKey, string stateName, float speedMult,
            out float duration)
        {
            duration = 0f;
            if (string.IsNullOrEmpty(stateName)
                || !AnimBundleCache.TryGet(baseKey, out var bundle))
                return false;
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (!TryResolveBundleState(
                    bundle,
                    playbackStateName,
                    out var state,
                    out _))
            {
                string warningKey = bundle.BaseKey + "|" + playbackStateName;
                if (_bundleMissingStateWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] 动画包缺 state，当前状态回落旧链（base={bundle.BaseKey}, state={stateName}）");
                }
                return false;
            }

            var rawDurations = new float[state.Frames.Length];
            for (int i = 0; i < rawDurations.Length; i++)
                rawDurations[i] = state.Frames[i].Duration;
            var scaledDurations = ScaleTimedDurations(rawDurations, speedMult, out _);

            double total = 0d;
            for (int i = 0; i < scaledDurations.Length; i++) total += scaledDurations[i];
            if (total <= 0d || total > float.MaxValue
                || double.IsNaN(total) || double.IsInfinity(total))
                return false;
            duration = (float)total;
            return true;
        }

        private static bool TryPlayTimedAnim(long handle, SpriteAnimator anim, string stateName, float speedMult)
        {
            if (!TryBuildTimedClip(
                    anim.SpriteBaseKey,
                    stateName,
                    speedMult,
                    out var clip,
                    out string playbackStateName))
                return false;
            ApplyNativeDirectionRendering(handle, playbackStateName);

            System.Action<int> onFrameEnter = null;
            if (clip.Events != null && clip.Events.Count > 0)
            {
                onFrameEnter = frameIndex =>
                {
                    for (int i = 0; i < clip.Events.Count; i++)
                    {
                        var frameEvent = clip.Events[i];
                        if (frameEvent.Frame == frameIndex)
                        {
                            HeroDefense.Engine.Host.LuaHost.CallGlobal(
                                "Anim_OnFrameEvent", handle, playbackStateName, frameEvent.Name);
                        }
                    }
                };
            }

            anim.PlayTimed(playbackStateName, clip.Frames, clip.Durations, clip.Looping, onFrameEnter);
            return true;
        }

        // ============ 无句柄队伍兵模播放入口 ============
        // 与英雄根的播放链共用同一套 bundle/timed/flat 解析和正式状态兼容，
        // 但故意不注册 handle、overlay、HP 状态条或 Anim_OnFrameEvent 回调。
        private static void ApplyVisualDirectionRendering(
            SpriteRenderer renderer,
            string stateName,
            bool faceRight)
        {
            if (renderer == null) return;
            bool nativeDirectionalState = !string.IsNullOrEmpty(stateName)
                && (stateName.EndsWith("_left")
                    || stateName.EndsWith("_right")
                    || stateName.EndsWith("_up")
                    || stateName.EndsWith("_down"));
            renderer.flipX = nativeDirectionalState ? false : !faceRight;
        }

        private static bool TryPlayVisualBundle(
            SpriteAnimator anim,
            SpriteRenderer renderer,
            string stateName,
            float speedMult,
            bool faceRight)
        {
            if (anim == null || string.IsNullOrEmpty(stateName)
                || !AnimBundleCache.TryGet(anim.SpriteBaseKey, out var bundle))
                return false;
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (!TryResolveBundleState(
                    bundle,
                    playbackStateName,
                    out var state,
                    out _))
                return false;
            if (!TryBuildBundleClip(bundle, state, speedMult, out var clip, out string reason))
            {
                string warningKey = "visual|" + bundle.BaseKey + "|" + playbackStateName;
                if (_bundleBuildWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] visual bundle state build failed (base={bundle.BaseKey}, state={playbackStateName}): {reason}");
                }
                return false;
            }
            clip.Looping = ResolveStateLooping(playbackStateName, clip.Looping);
            ApplyVisualDirectionRendering(renderer, playbackStateName, faceRight);
            anim.PlayBundle(playbackStateName, clip);
            return true;
        }

        private static bool TryPlayVisualTimed(
            SpriteAnimator anim,
            SpriteRenderer renderer,
            string stateName,
            float speedMult,
            bool faceRight)
        {
            if (anim == null
                || !TryBuildTimedClip(
                    anim.SpriteBaseKey,
                    stateName,
                    speedMult,
                    out var clip,
                    out string playbackStateName))
                return false;
            ApplyVisualDirectionRendering(renderer, playbackStateName, faceRight);
            anim.PlayTimed(playbackStateName, clip.Frames, clip.Durations, clip.Looping);
            return true;
        }

        private static bool PlayVisualFormalStatePlaceholder(
            SpriteAnimator anim,
            SpriteRenderer renderer,
            string baseKey,
            string stateName,
            float speedMult,
            bool faceRight)
        {
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (anim == null || !IsFormalActionState(playbackStateName)) return false;
            string warningKey = NormalizeAnimBaseKey(baseKey) + "|" + playbackStateName;
            if (_formalStatePlaceholderWarnings.Add(warningKey))
            {
                Debug.LogWarning(
                    $"[BattleBridge] formal animation state missing; using single-frame placeholder (base={NormalizeAnimBaseKey(baseKey)}, state={playbackStateName})");
            }
            var placeholder = GetFallbackSprite(baseKey);
            if (placeholder == null) return false;
            ApplyVisualDirectionRendering(renderer, playbackStateName, faceRight);
            anim.Play(
                playbackStateName,
                new[] { placeholder },
                fps: ScaledFps(playbackStateName, speedMult),
                looping: IsLoopingState(playbackStateName));
            return true;
        }

        /// <summary>
        /// 只供 SquadVisualController 调用的内部播放入口。它不能触发 Lua、不能创建句柄，
        /// 因此兵模帧事件和假攻击绝不可能反馈为权威伤害。
        /// </summary>
        internal static bool PlayVisualAnimation(
            SpriteAnimator anim,
            SpriteRenderer renderer,
            string stateName,
            float speedMult,
            bool faceRight)
        {
            if (anim == null) return false;
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (string.IsNullOrEmpty(anim.SpriteBaseKey)
                || string.IsNullOrEmpty(playbackStateName))
                return false;
            if (TryPlayVisualBundle(
                    anim,
                    renderer,
                    playbackStateName,
                    speedMult,
                    faceRight))
                return true;
            if (TryPlayVisualTimed(
                    anim,
                    renderer,
                    playbackStateName,
                    speedMult,
                    faceRight))
                return true;
            if (TryGetCompatibleFlatFrames(
                    anim.SpriteBaseKey,
                    playbackStateName,
                    out var frames,
                    out _))
            {
                ApplyVisualDirectionRendering(renderer, playbackStateName, faceRight);
                anim.Play(
                    playbackStateName,
                    frames,
                    fps: ScaledFps(playbackStateName, speedMult),
                    looping: IsLoopingState(playbackStateName));
                return true;
            }
            return PlayVisualFormalStatePlaceholder(
                anim,
                renderer,
                anim.SpriteBaseKey,
                playbackStateName,
                speedMult,
                faceRight);
        }

        private static void RefreshAnimLayout(long handle)
        {
            if (_units.TryGetValue(handle, out var uv) && uv != null) uv.FitSpriteToBlock();
        }

        /// <summary>按文件实际存在探测并加载某 (baseKey,state) 的全部帧；结果缓存。</summary>
        // ============ 2026-05-29 (Q1) — Spine 动画 stub ============
        // 当前未集成 spine-unity SDK，先打 warning（只警告一次/handle）并 fall back 到 frame 路径。
        // 未来 spine-unity 接入后，本方法替换为真实 SkeletonAnimation 播放逻辑。
        // 接入要点（保留 TODO 形式）：
        //   1. Package Manager 装 spine-unity（com.esotericsoftware.spine.spine-unity）
        //   2. unit 的 GameObject 在 spawn 时不挂 SpriteRenderer，而是挂 SkeletonAnimation
        //   3. spawn 时按 sprite_key 加载 SkeletonDataAsset（spine-unity 4.x 支持 runtime byte[] 解析）
        //   4. 此处调 skel.AnimationState.SetAnimation(0, stateName, loop) 即可
        private static readonly HashSet<long> _spineWarnedHandles = new HashSet<long>();
        private static void PlayAnim_Spine(long handle, SpriteAnimator anim, string stateName, float speedMult)
        {
            long warningHandle = 0;
            // 按 UnitView handle 对 warning 去重。
            if (anim != null && anim.gameObject != null)
            {
                var view = anim.gameObject.GetComponent<UnitView>();
                if (view != null) warningHandle = view.Handle;
            }
            if (!_spineWarnedHandles.Contains(warningHandle))
            {
                _spineWarnedHandles.Add(warningHandle);
                Debug.LogWarning($"[BattleBridge] anim_type=atSpine 配置但 spine-unity SDK 未集成 → 兜底走 frame 路径（key={anim.SpriteBaseKey}, state={stateName}）");
            }
            // stub fallback 与 atFrame 一致先尝试 bundle；无可用 bundle 再走 timed/旧均摊路径。
            if (TryPlayBundleAnim(handle, anim, stateName, speedMult)) return;
            if (TryPlayTimedAnim(handle, anim, stateName, speedMult))
            {
                RefreshAnimLayout(handle);
                return;
            }
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (!TryGetCompatibleFlatFrames(
                    anim.SpriteBaseKey,
                    playbackStateName,
                    out var frames,
                    out _))
            {
                PlayFormalStatePlaceholder(
                    handle,
                    anim,
                    anim.SpriteBaseKey,
                    playbackStateName,
                    speedMult);
                return;
            }
            ApplyNativeDirectionRendering(handle, playbackStateName);
            bool looping = IsLoopingState(playbackStateName);
            anim.Play(playbackStateName, frames, fps: ScaledFps(playbackStateName, speedMult), looping: looping);
            // uniform fallback 仍按 UnitView footprint 更新布局。
            if (_units.TryGetValue(warningHandle, out var uv) && uv != null) uv.FitSpriteToBlock();
        }

        // 动画播放帧率（配置驱动，按状态分级；越小动作越慢）。2026-06-07
        //   GameConfig: anim_fps_default(其余动作) + 可选 anim_fps_<state>（如 anim_fps_idle / anim_fps_die）。
        //   当前配置: idle/die=8（休闲/死亡慢）, 其余=12。缓存一次。
        private static System.Collections.Generic.Dictionary<string, float> _animFps;
        private static float _animFpsDefault = 12f;
        private static void EnsureAnimFps()
        {
            if (_animFps != null) return;
            _animFps = new System.Collections.Generic.Dictionary<string, float>();
            try
            {
                var cm = HeroDefense.Config.ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var d = cm.GetTableInfo("GameConfig", "key", "anim_fps_default");
                    if (d != null) _animFpsDefault = cm.GetValue<float>(d, "value", 12f);
                    foreach (var st in new[]
                    {
                        "idle", "idle_left",
                        "idle_down", "combat_idle", "combat_idle_left",
                        "walk", "walk_left", "walk_up", "walk_down",
                        "attack", "attack_left", "die", "die_left"
                    })
                    {
                        var row = cm.GetTableInfo("GameConfig", "key", "anim_fps_" + st);
                        if (row != null) _animFps[st] = cm.GetValue<float>(row, "value", _animFpsDefault);
                    }
                }
            }
            catch (System.Exception) { }
            if (_animFpsDefault < 0.1f) _animFpsDefault = 12f;
        }
        private static float AnimFpsFor(string state)
        {
            EnsureAnimFps();
            string semanticStateName = NormalizeActionStateAlias(state);
            if (semanticStateName != null
                && _animFps.TryGetValue(semanticStateName, out var f)
                && f >= 0.1f)
                return f;
            string legacyStateName = LegacyPackageState(semanticStateName);
            if (legacyStateName != null
                && _animFps.TryGetValue(legacyStateName, out var legacyFps)
                && legacyFps >= 0.1f)
            {
                return legacyFps;
            }
            return _animFpsDefault;
        }

        // 按倍率缩放并取整（攻击动画随攻速 buff 提速；mult=1 即基础 fps）。2026-06-07
        private static float ScaledFps(string state, float speedMult)
        {
            float f = AnimFpsFor(state);
            if (speedMult > 0f && speedMult != 1f)
                f = UnityEngine.Mathf.Max(1f, UnityEngine.Mathf.Round(f * speedMult));
            return f;
        }

        /// <summary>状态名 → 帧数组，带两级回退：
        ///   1) 方向后缀状态缺帧 → 回退基础状态（walk_left/up/down→walk；attack_left→attack；idle_left→idle；die_left→die）。
        ///      左向旧资源回退时保留 Lua 设置的 flipX；原生方向帧由播放路径取消镜像。
        ///   2) 仍 0 帧 → 回退 idle（T237：怪复用武将美术无 walk 状态时播 idle 循环而非僵首帧）。</summary>
        private static Sprite[] ResolveAnimFrames(string baseKey, string stateName)
        {
            return TryGetCompatibleFlatFrames(baseKey, stateName, out var frames, out _)
                ? frames
                : new Sprite[0];
        }

        /// <summary>持续循环态判定：idle 与 walk 的全部方向态循环；attack/die 一次性。</summary>
        private static bool IsLoopingState(string state)
        {
            string semanticStateName = NormalizeActionStateAlias(state);
            return semanticStateName == "idle"
                || state == "idle_left"
                || semanticStateName == "idle_down"
                || semanticStateName == "combat_idle"
                || semanticStateName == "combat_idle_left"
                || semanticStateName == "walk"
                || semanticStateName == "walk_left"
                || semanticStateName == "walk_up"
                || semanticStateName == "walk_down";
        }

        private static bool IsDeathState(string state)
        {
            return state == "die"
                || state == "die_left"
                || state == "die_right";
        }

        private static bool IsAttackState(string state)
        {
            return state == "attack"
                || state == "attack_left"
                || state == "attack_right";
        }

        // 动画包/anim.json 可声明自定义状态循环；十态合同中的 idle/walk/attack/die 由运行时兜底纠正。
        private static bool ResolveStateLooping(string state, bool declaredLooping)
        {
            if (IsLoopingState(state)) return true;
            if (IsAttackState(state) || IsDeathState(state)) return false;
            return declaredLooping;
        }

        private static Sprite[] GetAnimFrames(string baseKey, string state)
        {
            string cacheKey = baseKey + "|" + state;
            if (_animFrameCache.TryGetValue(cacheKey, out var cached)) return cached;
            var list = new List<Sprite>();
            for (int i = 0; i < ANIM_MAX_FRAMES; i++)
            {
                // 2026-05-29 (Q5) — 双路径回落:
                //   优先新结构: resources/art/<baseKey>/atlas/<state>_<i>.png（pack_atlas.py 输出+ atlas xml key 也用此路径）
                //   兼容旧扁平: resources/art/<baseKey>_<state>_<i>.png（旧美术资源命名）
                // logMissing=false：探测到首个缺失帧即为终点，不是错误，不刷警告
                var s = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{baseKey}/atlas/{state}_{i}.png", false);
                if (s == null)
                {
                    // 旧扁平路径回落
                    s = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{baseKey}_{state}_{i}.png", false);
                }
                if (s == null) break;   // 两种路径都没 → 该状态帧数 = i
                list.Add(s);
            }
            var arr = list.ToArray();
            _animFrameCache[cacheKey] = arr;
            return arr;
        }

        /// <summary>把 base key 存到 SpriteAnimator，便于后续 PlayAnim 按 state 拼帧加载。</summary>
        private static SpriteAnimator GetAnimator(long handle)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
                return view.GetComponent<SpriteAnimator>();
            return null;
        }

        private static void SetHandleHpBarAnimHidden(long handle, bool hidden)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
                view.SetHpBarAnimHidden(hidden);
        }

        private static void ClearHandleAnimationState(long handle)
        {
            _unitOverlaySlots.Remove(handle);
            _visualFaceRight.Remove(handle);
            var animator = GetAnimator(handle);
            if (animator != null) animator.ClearRuntimeOverlays();
            SetHandleHpBarAnimHidden(handle, false);
        }

        private static SpriteRenderer GetRenderer(long handle)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
                return view.GetComponentInChildren<SpriteRenderer>();
            return null;
        }

        // 原生方向态已经画好方向，不允许继承旧资源 flipX；只改渲染，不覆盖语义朝向缓存。
        private static void ApplyNativeDirectionRendering(long handle, string stateName)
        {
            if (string.IsNullOrEmpty(stateName)
                || !(stateName.EndsWith("_left")
                    || stateName.EndsWith("_right")
                    || stateName.EndsWith("_up")
                    || stateName.EndsWith("_down")))
                return;
            var renderer = GetRenderer(handle);
            if (renderer != null) renderer.flipX = false;
        }

        // 正式美术缺失时用 32px 逻辑画布占位，保证实体和碰撞区仍可验收。
        // 主营保持 3 格横向比例；占位图只属于表现层，不进入业务数据。
        private static readonly Dictionary<string, Sprite> _fallbackSprites =
            new Dictionary<string, Sprite>();

        private static Sprite GetFallbackSprite(string spriteKey)
        {
            string kind = "unit";
            if (!string.IsNullOrEmpty(spriteKey))
            {
                if (spriteKey.IndexOf(
                        "building/camp",
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    kind = "camp";
                else if (spriteKey.IndexOf(
                        "building/barricade",
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    kind = "barricade";
                else if (spriteKey.IndexOf(
                        "building/tower",
                        System.StringComparison.OrdinalIgnoreCase) >= 0)
                    kind = "tower";
            }
            if (_fallbackSprites.TryGetValue(kind, out var cached) && cached != null)
                return cached;

            int width = kind == "camp" ? 96 : 32;
            const int height = 32;
            var texture = new Texture2D(
                width,
                height,
                TextureFormat.RGBA32,
                false)
            {
                name = $"match_placeholder_{kind}",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[width * height];
            Color32 outline = new Color32(54, 43, 36, 255);
            Color32 fill = kind == "unit"
                ? new Color32(210, 154, 74, 255)
                : kind == "tower"
                    ? new Color32(126, 108, 84, 255)
                    : kind == "barricade"
                        ? new Color32(126, 72, 42, 255)
                        : new Color32(154, 116, 70, 255);

            int left = kind == "camp" ? 4 : 6;
            int right = width - left - 1;
            int bottom = 2;
            int top = kind == "tower" ? 29 : 27;
            for (int y = bottom; y <= top; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    bool edge = x == left
                        || x == right
                        || y == bottom
                        || y == top;
                    pixels[y * width + x] = edge ? outline : fill;
                }
            }

            if (kind == "unit")
            {
                for (int y = 23; y <= 30; y++)
                {
                    for (int x = 12; x <= 19; x++)
                    {
                        pixels[y * width + x] =
                            x == 12 || x == 19 || y == 23 || y == 30
                                ? outline
                                : new Color32(224, 190, 132, 255);
                    }
                }
            }
            else if (kind == "barricade")
            {
                for (int x = 3; x < width - 3; x += 6)
                {
                    for (int y = 5; y < 31; y++)
                        pixels[y * width + x] = outline;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            var sprite = Sprite.Create(
                texture,
                new Rect(0, 0, width, height),
                new Vector2(0.5f, 0f),
                32f);
            sprite.name = $"match_placeholder_{kind}";
            _fallbackSprites[kind] = sprite;
            return sprite;
        }

        // 旧包没有 idle_down 时不能拿 idle 或左右战斗姿态冒充。以单帧占位保持对象
        // 可见且不改变 Lua 的攻击/结算时序；其他缺失正式状态同样不做方向镜像。
        private static bool PlayFormalStatePlaceholder(
            long handle,
            SpriteAnimator anim,
            string baseKey,
            string stateName,
            float speedMult)
        {
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (anim == null || !IsFormalActionState(playbackStateName)) return false;

            string warningKey = NormalizeAnimBaseKey(baseKey) + "|" + playbackStateName;
            if (_formalStatePlaceholderWarnings.Add(warningKey))
            {
                Debug.LogWarning(
                    $"[BattleBridge] formal animation state missing; using single-frame placeholder (base={NormalizeAnimBaseKey(baseKey)}, state={playbackStateName})");
            }

            var placeholder = GetFallbackSprite(baseKey);
            if (placeholder == null) return false;
            ApplyNativeDirectionRendering(handle, playbackStateName);
            anim.Play(
                playbackStateName,
                new[] { placeholder },
                fps: ScaledFps(playbackStateName, speedMult),
                looping: IsLoopingState(playbackStateName));
            RefreshAnimLayout(handle);
            return true;
        }

        public static void Battle_SetSprite(long handle, string spriteKey)
        {
            if (string.IsNullOrEmpty(spriteKey)) return;

            // 1. 记录 base key 到 SpriteAnimator，Battle_PlayAnim 之后用
            var anim = GetAnimator(handle);
            if (anim != null) anim.SpriteBaseKey = spriteKey;

            // 2. 尝试加载首帧并显示（多路径回落:
            //    a) resources/art/{key}.png            — 单图 sprite_key 指向单 PNG（无动画的建筑等）
            //    b) resources/art/{key}/atlas/idle_0   — 新结构序列帧（2026-05-29 Q5）
            //    c) resources/art/{key}/atlas/walk_0   — 新结构 walk 备选
            //    d) resources/art/{key}_idle_0         — 旧扁平兼容
            //    e) resources/art/{key}_walk_0         — 旧扁平 walk 备选
            var sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                $"resources/art/{spriteKey}.png",
                false);
            if (sprite == null)
                sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                    $"resources/art/{spriteKey}/atlas/idle_0.png",
                    false);
            if (sprite == null)
                sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                    $"resources/art/{spriteKey}/atlas/walk_0.png",
                    false);
            if (sprite == null)
                sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                    $"resources/art/{spriteKey}_idle_0.png",
                    false);
            if (sprite == null)
                sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite(
                    $"resources/art/{spriteKey}_walk_0.png",
                    false);
            // 3. 所有 fallback 失败 → 用全局兜底，保证 unit 可见（配置错配占位文件时不至于无视觉）
            if (sprite == null) sprite = GetFallbackSprite(spriteKey);
            if (sprite == null) return;

            var sr = GetRenderer(handle);
            if (sr != null) sr.sprite = sprite;

            // sprite 设好后按 footprint 脚底锚定。
            if (_units.TryGetValue(handle, out var uv) && uv != null) uv.FitSpriteToBlock(true);
        }

        public static void Battle_PlayAnim(long handle, string stateName)
        {
            Battle_PlayAnim(handle, stateName, 1f);
        }

        public static bool Battle_SetUnitOverlay(long handle, int slot, string baseKey)
        {
            if (slot < 1 || slot > RuntimeOverlaySlotCount)
            {
                string warningKey = "slot|" + slot;
                if (_runtimeOverlayApiWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] Battle_SetUnitOverlay slot 越界：{slot}（有效范围 1..{RuntimeOverlaySlotCount}）");
                }
                return false;
            }

            var animator = GetAnimator(handle);
            if (animator == null)
            {
                string warningKey = "handle|" + handle;
                if (_runtimeOverlayApiWarnings.Add(warningKey))
                {
                    Debug.LogWarning(
                        $"[BattleBridge] Battle_SetUnitOverlay handle 无效：{handle}");
                }
                return false;
            }

            int slotIndex = slot - 1;
            if (string.IsNullOrWhiteSpace(baseKey))
            {
                if (_unitOverlaySlots.TryGetValue(handle, out var existing) && existing != null)
                {
                    existing[slotIndex] = null;
                    bool any = false;
                    for (int i = 0; i < existing.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(existing[i]))
                        {
                            any = true;
                            break;
                        }
                    }
                    if (!any) _unitOverlaySlots.Remove(handle);
                }
                animator.ClearRuntimeOverlaySlot(slotIndex);
                return true;
            }

            if (!_unitOverlaySlots.TryGetValue(handle, out var slots) || slots == null)
            {
                slots = new string[RuntimeOverlaySlotCount];
                _unitOverlaySlots[handle] = slots;
            }
            slots[slotIndex] = NormalizeAnimBaseKey(baseKey.Trim());
            return true;
        }

        public static void Battle_ClearUnitOverlays(long handle)
        {
            _unitOverlaySlots.Remove(handle);
            var animator = GetAnimator(handle);
            if (animator != null) animator.ClearRuntimeOverlays();
        }

        // speedMult: 攻速加成倍率（基准 1.0）。攻击动画 fps = round(基础fps × mult)，攻速 buff 越高出手越快。2026-06-07
        public static void Battle_PlayAnim(long handle, string stateName, float speedMult)
        {
            var anim = GetAnimator(handle);
            if (anim == null) return;
            string playbackStateName = NormalizeActionStateAlias(stateName);
            if (string.IsNullOrEmpty(anim.SpriteBaseKey))
            {
                anim.SendMessage("OnAnimStateChanged", playbackStateName, SendMessageOptions.DontRequireReceiver);
                return;
            }

            // 2026-05-29 (Q1) — 按 AnimType 分发：spine 走 stub（log + fallback 到 frame）；frame 走原路径
            if (anim.AnimType == "atSpine")
            {
                PlayAnim_Spine(handle, anim, playbackStateName, speedMult);
                return;
            }

            if (TryPlayBundleAnim(handle, anim, playbackStateName, speedMult)) return;

            if (TryPlayTimedAnim(handle, anim, playbackStateName, speedMult))
            {
                RefreshAnimLayout(handle);
                return;
            }

            if (!TryGetCompatibleFlatFrames(
                    anim.SpriteBaseKey,
                    playbackStateName,
                    out var frames,
                    out _))
            {
                PlayFormalStatePlaceholder(
                    handle,
                    anim,
                    anim.SpriteBaseKey,
                    playbackStateName,
                    speedMult);
                return;
            }
            ApplyNativeDirectionRendering(handle, playbackStateName);

            bool looping = IsLoopingState(playbackStateName);
            anim.Play(playbackStateName, frames, fps: ScaledFps(playbackStateName, speedMult), looping: looping);

            // Play 已把首帧设到 SpriteRenderer，按 footprint 脚底锚定；友军单位尺寸对齐拖拽 UI ghost。
            RefreshAnimLayout(handle);   // 切态复用 idle 身体基准缩放(不重算→身体不缩·兵器溢出)
        }

        // v2 批 1b（2026-06-14）C#④：取某单位/怪某动画状态在给定攻速倍率下的播放时长（秒）。
        // 帧数走 ResolveAnimFrames（含方向后缀/idle 两级回退，与 Battle_PlayAnim 同源）；fps 走 ScaledFps。
        // 用途：Lua 出手点定时（普攻减 CD：attack 动画时长 × atk_hit_pct = 出手帧时刻）/ 技能动画对齐。
        // handle/animator 无效或 baseKey 空或帧数=0/fps<=0 → 返回 0（Lua 侧自行兜底）。
        public static float Battle_GetAnimLen(long handle, string state, float speedMult)
        {
            var anim = GetAnimator(handle);
            if (anim == null || string.IsNullOrEmpty(anim.SpriteBaseKey)) return 0f;
            if (TryGetBundleAnimLength(anim.SpriteBaseKey, state, speedMult, out float bundleDuration))
                return bundleDuration;
            if (TryBuildTimedClip(anim.SpriteBaseKey, state, speedMult, out var timed, out _))
                return timed.RawDurationTotal / timed.SpeedMultiplier;
            var frames = ResolveAnimFrames(anim.SpriteBaseKey, state);
            int n = frames != null ? frames.Length : 0;
            float fps = ScaledFps(state, speedMult);
            return (n <= 0 || fps <= 0f) ? 0f : n / fps;
        }

        public static void Battle_SetWorldPosition(long handle, float wx, float wy)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
            {
                // 审查 D (2026-06-11)：瞬移 = 权威落位，取消进行中的行走（否则 GridMover 下一帧把单位拽回旧路径）。
                // Unit_OnWalkArrived 的精确落位 snap 发生在 Finish 之后（Active 已 false）→ Stop 幂等无副作用。
                var gm = view.GetComponent<GridMover>();
                if (gm != null && gm.Active) gm.Stop();
                view.SetWorldPosition(wx, wy);
            }
        }

        /// <summary>2026-06-14 用户：移动中的单位被再拖/双击回收 → 停在"当前视觉格"(不回 walk 目标)。
        /// 读 transform.position(GridMover 走到一半的真实位置) → WorldToCell → 停 GridMover → 返回 cellId；
        /// Lua(Unit_StopWalk) 解码后用 Unit_MoveTo 把占格/row/col 从 walk 目标转移到当前格 + snap。-1=单位不存在。</summary>
        public static int Battle_GetUnitCellAndStop(long handle)
        {
            if (!_units.TryGetValue(handle, out var view) || view == null) return -1;
            var p = view.transform.position;
            var cell = GridMap.WorldToCell(new Vector2(p.x, p.y));
            var gm = view.GetComponent<GridMover>();
            if (gm != null && gm.Active) gm.Stop();
            return GridMap.RowColToCellId(cell.row, cell.col);
        }

        /// <summary>读取移动中单位的当前视觉格，不停止 GridMover。-1 表示单位不存在。</summary>
        public static int Battle_GetUnitCell(long handle)
        {
            if (!_units.TryGetValue(handle, out var view) || view == null) return -1;
            var p = view.transform.position;
            var cell = GridMap.WorldToCell(new Vector2(p.x, p.y));
            return GridMap.RowColToCellId(cell.row, cell.col);
        }

        /// <summary>场上单位沿 cell 路径逐格走（非瞬移）。
        /// pathCsv = "r,c;r,c;..."（Lua Path_Find 产出，不含当前格也可——从当前位置朝首点走）。
        /// 速度读 GameConfig.unit_move_speed（GridMover 内缓存）；到达终点 GridMover 回调 Lua Unit_OnWalkArrived(handle)。
        /// 单位不存在或路径无有效格时同步回调 Unit_OnWalkArrived；越界 cell 会被过滤。</summary>
        public static void Battle_UnitWalkPath(long handle, string pathCsv, float speed = 0f)
        {
            if (!_units.TryGetValue(handle, out var view) || view == null)
            {
                CallLuaUnitWalkArrived(handle);   // 句柄表脱同步 → 立即"到达"，Lua 清 moving + snap 落位
                return;
            }
            var waypoints = new List<Vector2>();
            if (!string.IsNullOrEmpty(pathCsv))
            {
                var segs = pathCsv.Split(';');
                foreach (var seg in segs)
                {
                    if (string.IsNullOrEmpty(seg)) continue;
                    var rc = seg.Split(',');
                    if (rc.Length < 2) continue;
                    if (int.TryParse(rc[0], out int r) && int.TryParse(rc[1], out int c)
                        && GridMap.IsCellInBounds(r, c))
                        waypoints.Add(GridMap.CellToWorld(r, c));
                }
            }
            var mover = view.GetComponent<GridMover>();
            if (mover == null) mover = view.gameObject.AddComponent<GridMover>();
            mover.BeginPath(handle, waypoints, speed);   // 空 waypoints → BeginPath 内立即 NotifyArrived；speed<=0 → ConfigSpeed 兜底
        }

        /// <summary>Battle_UnitWalkPath 失败分支用：直接调 Lua Unit_OnWalkArrived（与 GridMover.NotifyArrived 同款）。</summary>
        private static void CallLuaUnitWalkArrived(long handle)
        {
#if XLUA
            try
            {
                var env = Engine.Host.LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Unit_OnWalkArrived");
                if (fn != null)
                {
                    fn.Call(handle);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleBridge] CallLua Unit_OnWalkArrived 失败: {e.Message}");
            }
#endif
        }

        public static void Battle_SetAlpha(long handle, float alpha)
        {
            if (_units.TryGetValue(handle, out var view) && view != null) view.SetAlpha(alpha);
        }

        public static void Battle_SetScale(long handle, float scale)
        {
            if (_units.TryGetValue(handle, out var view) && view != null) view.SetScale(scale);
        }

        /// <summary>设单位朝向。faceRight=true 朝右，false 朝左；攻击时由业务传入目标方向。</summary>
        public static void Battle_SetUnitFacing(long handle, bool faceRight)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
            {
                _visualFaceRight[handle] = faceRight;
                view.SetFacing(faceRight);
            }
        }

        /// <summary>
        /// 设置武将生命与兵力两条表现比例。比例在 C# 内钳制到 0..1；
        /// maxHP、兵力上限和任何伤害规则都留在 Lua Match 领域层。
        /// </summary>
        public static void Battle_SetUnitStatusBars(
            long handle,
            float heroPct,
            float troopPct)
        {
            if (_units.TryGetValue(handle, out var view)
                    && view != null)
            {
                view.SetStatusBars(heroPct, troopPct);
                return;
            }
            if (_missingStatusViewWarnings.Add(handle))
            {
                Debug.LogWarning(
                    $"[BattleBridge] 状态条目标 view 不存在：{handle}");
            }
        }

        /// <summary>
        /// dual=武将生命+兵力，single=建筑耐久。这里只切显示模式，
        /// 不判断实体业务类型。
        /// </summary>
        public static void Battle_SetUnitStatusBarMode(
            long handle,
            string mode)
        {
            if (_units.TryGetValue(handle, out var view)
                    && view != null)
            {
                view.SetStatusBarMode(mode);
                return;
            }
            if (_missingStatusViewWarnings.Add(handle))
            {
                Debug.LogWarning(
                    $"[BattleBridge] 状态条模式目标 view 不存在：{handle}");
            }
        }

        // T203 (2026-05-21) — 单位头顶血量条可见性：Lua Drag_OnDragBegin 时调 false 隐藏，OnDragEnd 时调 true 恢复
        public static void Battle_SetUnitHpBarVisible(long handle, bool visible)
        {
            if (_units.TryGetValue(handle, out var u) && u != null)
            {
                u.SetHpBarVisible(visible);
            }
        }

        // ============ 三区桥接（R1b 2026-06-10）============

        /// <summary>Lua（camp 管理）战斗开始 + 基地升级时推入三区列数。通关模式 enemyCols 传 0（R6 假对局再接）。
        /// 2026-06-11 用户否决 F5 三区常驻淡色 → 不再重染 cell 底色（区域只是落点门控数据，无常驻视觉）。</summary>
        public static void Battle_SetZones(int ownCols, int enemyCols) => GridMap.InitZones(ownCols, enemyCols);

        public static bool Battle_IsCellInOwnZone(int row, int col) => GridMap.IsCellInOwnZone(row, col);
        public static bool Battle_IsCellInPublicZone(int row, int col) => GridMap.IsCellInPublicZone(row, col);
        public static bool Battle_IsCellInEnemyZone(int row, int col) => GridMap.IsCellInEnemyZone(row, col);
        public static string Battle_GetCellZone(int row, int col) => GridMap.GetCellZone(row, col);

        // ============ 投射物 1 方法 ============

        // 投射物默认 sprite（箭矢）— 缓存，首次按需加载
        private static Sprite _projectileSprite;
        private static Sprite GetProjectileSprite()
        {
            if (_projectileSprite == null)
            {
                _projectileSprite =
                    HeroDefense.Engine.Host.LuaHost.LoadSprite(
                        "resources/art/projectile/arrow.png",
                        false);
                if (_projectileSprite == null)
                {
                    var texture = new Texture2D(
                        8,
                        2,
                        TextureFormat.RGBA32,
                        false)
                    {
                        name = "match_placeholder_projectile",
                        filterMode = FilterMode.Point,
                        wrapMode = TextureWrapMode.Clamp,
                    };
                    var pixels = new Color32[16];
                    for (int index = 0;
                            index < pixels.Length;
                            index++)
                    {
                        pixels[index] =
                            new Color32(
                                244,
                                205,
                                96,
                                255);
                    }
                    texture.SetPixels32(pixels);
                    texture.Apply(false, true);
                    _projectileSprite = Sprite.Create(
                        texture,
                        new Rect(0, 0, 8, 2),
                        new Vector2(0.5f, 0.5f),
                        32f);
                    _projectileSprite.name =
                        "match_placeholder_projectile";
                }
            }
            return _projectileSprite;
        }

        // v2 批1：按 key 取投射物 sprite（resources/art/projectile/<key>.png；缺则 LogWarning + arrow 兜底；空 key 直接默认）。军规2。
        private static readonly Dictionary<string, Sprite> _projSpriteCache = new Dictionary<string, Sprite>();
        private static Sprite GetProjectileSprite(string key)
        {
            if (string.IsNullOrEmpty(key)) return GetProjectileSprite();
            if (_projSpriteCache.TryGetValue(key, out var s)) return s;
            var sp = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/projectile/{key}.png", false);
            if (sp == null)
            {
                Debug.LogWarning($"[BattleBridge] 投射物贴图缺失 resources/art/projectile/{key}.png → 回退 arrow");
                sp = GetProjectileSprite();
            }
            _projSpriteCache[key] = sp;
            return sp;
        }

        /// <summary>供纯视觉兵模投射物复用资源加载与安全默认图，不注册 ProjectileTicker。</summary>
        internal static Sprite GetVisualProjectileSprite(string key)
        {
            return GetProjectileSprite(key);
        }

        /// <summary>
        /// 投射物（C# 跑数学：直线位移 + 命中 → 调 Lua Battle_OnProjectileHit）。
        /// damage 实际不在 C# 用（Lua 计算克制/暴击），保留参数是为了未来扩展 callback。
        /// </summary>
        public static long Battle_SpawnProjectile(long srcHandle, long tgtHandle, float damage)
        {
            try
            {
                EnsureProjectilePoolConfig();

                Transform target = null;
                if (_units.TryGetValue(tgtHandle, out var tu) && tu != null) target = tu.transform;
                if (target == null)
                {
                    Debug.LogWarning($"[BattleBridge] Battle_SpawnProjectile 目标 {tgtHandle} 不存在");
                    return 0;
                }

                Vector2 spawn;
                if (_units.TryGetValue(srcHandle, out var su) && su != null) spawn = su.transform.position;
                else spawn = Vector2.zero;

                // Step 11 池化：优先池复用
                GameObject go;
                ProjectileTicker p;
                if (_projectilePool.Count > 0)
                {
                    go = _projectilePool.Pop();
                    if (go == null)
                    {
                        // 池中持有的引用被销毁了（场景切换等），降级到 new
                        go = new GameObject("Proj_pooled_fallback");
                        go.AddComponent<SpriteRenderer>().sortingLayerName = HDSortingLayers.Projectile;
                        go.AddComponent<ProjectileTicker>();
                        _projectilePoolMisses++;
                    }
                    else
                    {
                        go.SetActive(true);
                        _projectilePoolHits++;
                    }
                    p = go.GetComponent<ProjectileTicker>();
                    if (p == null) p = go.AddComponent<ProjectileTicker>();
                }
                else
                {
                    go = new GameObject("Proj_new");
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sortingLayerName = HDSortingLayers.Projectile;
                    p = go.AddComponent<ProjectileTicker>();
                    _projectilePoolMisses++;
                }

                long h = NextHandle();
                p.PooledRecycled = false;
                p.Init(h, tgtHandle, target, spawn, speed: 8f, hitThreshold: 0.2f);
                go.name = $"Proj_h{h}";

                // 投射物 sprite — 缺则不可见（远程攻击"看不见"的根源）
                var psr = go.GetComponent<SpriteRenderer>();
                if (psr != null && psr.sprite == null)
                {
                    psr.sprite = GetProjectileSprite();
                    psr.sortingLayerName = HDSortingLayers.Projectile;
                }

                _projectiles[h] = p;
                return h;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleBridge] Battle_SpawnProjectile 失败: {e.Message}");
                return 0;
            }
        }

        // ============ P1.6 (2026-05-26) 投掷物 3 模式扩展 ============
        // 按 Docs/skill-system-architecture.md §5 + §10
        // 旧 Battle_SpawnProjectile 保留不动；新 3 个方法各自创建对应模式投掷物

        /// <summary>供 ProjectileTicker 的直线模式枚举当前存活单位。</summary>
        internal static IEnumerable<KeyValuePair<long, UnitView>> EnumerateUnits()
        {
            return _units;
        }

        // 共享 spawn helper：复用旧 Battle_SpawnProjectile 的池化 + sprite 逻辑
        // 返回 (handle, ticker, spawnPos)；srcHandle 找不到时 spawnPos = Vector2.zero
        // v2 批 1b（2026-06-14）C#⑤：
        //   - muzzle 偏移：出生点 = 单位 pos + (dx,dy)。dx/dy 默认读 GameConfig.proj_muzzle_dx/dy（格），
        //     逐将增强按 srcHandle 查 npc.tab.state_seq json 的 "muzzle":[dx_px,dy_px]（px，256px/格基准换算 world）。
        //     faceRight=false（朝左）时 dx 取反。（夜间决策：muzzle 走 C# 内查，Lua 不传偏移。）
        //   - projectileKey 无条件赋 sprite（去掉 ==null 守卫；空 key → arrow 默认）。
        private static (long handle, ProjectileTicker ticker, Vector2 spawnPos) SpawnProjectileShell(long srcHandle, string projectileKey, bool faceRight)
        {
            EnsureProjectilePoolConfig();

            Vector2 unitPos;
            int srcNpcId = 0;
            if (_units.TryGetValue(srcHandle, out var su) && su != null) { unitPos = su.transform.position; srcNpcId = ResolveNpcIdFromUnitName(su.gameObject); }
            else unitPos = Vector2.zero;

            var (mdx, mdy) = ResolveMuzzleOffset(srcNpcId);
            if (!faceRight) mdx = -mdx;
            Vector2 spawn = unitPos + new Vector2(mdx, mdy);

            GameObject go;
            ProjectileTicker p;
            if (_projectilePool.Count > 0)
            {
                go = _projectilePool.Pop();
                if (go == null)
                {
                    go = new GameObject("Proj_pooled_fallback");
                    go.AddComponent<SpriteRenderer>().sortingLayerName = HDSortingLayers.Projectile;
                    go.AddComponent<ProjectileTicker>();
                    _projectilePoolMisses++;
                }
                else { go.SetActive(true); _projectilePoolHits++; }
                p = go.GetComponent<ProjectileTicker>();
                if (p == null) p = go.AddComponent<ProjectileTicker>();
            }
            else
            {
                go = new GameObject("Proj_new");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = HDSortingLayers.Projectile;
                p = go.AddComponent<ProjectileTicker>();
                _projectilePoolMisses++;
            }

            long h = NextHandle();
            p.PooledRecycled = false;

            var psr = go.GetComponent<SpriteRenderer>();
            if (psr != null)
            {
                psr.sprite = GetProjectileSprite(projectileKey);   // 无条件按 key 赋（空 key → arrow）
                psr.sortingLayerName = HDSortingLayers.Projectile;
            }
            go.name = $"Proj_h{h}";
            _projectiles[h] = p;
            return (h, p, spawn);
        }

        // muzzle 偏移缓存（仿 EnsureProjectilePoolConfig / EnsureAnimFps 的"启动期读配置"模式）。
        private static bool _muzzleCfgLoaded;
        private static float _muzzleDx = 0.3f;   // GameConfig.proj_muzzle_dx（格；前向）
        private static float _muzzleDy = 0.6f;   // GameConfig.proj_muzzle_dy（格；手部高度）
        private static void EnsureMuzzleConfig()
        {
            if (_muzzleCfgLoaded) return;
            _muzzleCfgLoaded = true;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var rx = cm.GetTableInfo("GameConfig", "key", "proj_muzzle_dx");
                    if (rx != null) _muzzleDx = cm.GetValue<float>(rx, "value", 0.3f);
                    var ry = cm.GetTableInfo("GameConfig", "key", "proj_muzzle_dy");
                    if (ry != null) _muzzleDy = cm.GetValue<float>(ry, "value", 0.6f);
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[BattleBridge] 读 proj_muzzle_dx/dy 失败: {e.Message}"); }
        }

        // 逐将 muzzle 缓存：npcId → (dx,dy)（world 格）。0 = 用全局默认（无 state_seq.muzzle 或解析失败）。
        private static readonly Dictionary<int, Vector2> _muzzleByNpc = new Dictionary<int, Vector2>();
        private static readonly System.Text.RegularExpressions.Regex _muzzleRegex =
            new System.Text.RegularExpressions.Regex(@"""muzzle""\s*:\s*\[\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\]");

        /// <summary>出生点偏移（world 格）：默认 GameConfig.proj_muzzle_dx/dy；
        /// 若该 npc 的 state_seq json 含 "muzzle":[dx_px,dy_px]（px，256px/格基准）则换算覆盖。</summary>
        private static (float dx, float dy) ResolveMuzzleOffset(int npcId)
        {
            EnsureMuzzleConfig();
            if (npcId <= 0) return (_muzzleDx, _muzzleDy);
            if (_muzzleByNpc.TryGetValue(npcId, out var cached))
                return cached == Vector2.zero ? (_muzzleDx, _muzzleDy) : (cached.x, cached.y);

            Vector2 result = Vector2.zero;   // zero = 用全局默认
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var npcRow = cm.GetTableInfo("npc", "id", npcId);
                    if (npcRow != null)
                    {
                        string json = cm.GetValue<string>(npcRow, "state_seq", "{}");
                        if (!string.IsNullOrEmpty(json))
                        {
                            var m = _muzzleRegex.Match(json);
                            if (m.Success
                                && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float px)
                                && float.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float py))
                            {
                                // px → world 格：256px/格基准，与格宽/高对齐
                                result = new Vector2(px / 256f * GridMap.CellSizeX, py / 256f * GridMap.CellSizeY);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { Debug.LogWarning($"[BattleBridge] ResolveMuzzleOffset({npcId}) 失败: {e.Message}"); }

            _muzzleByNpc[npcId] = result;
            return result == Vector2.zero ? (_muzzleDx, _muzzleDy) : (result.x, result.y);
        }

        // 从单位 GameObject 名字反查 npc_id（spawn 时命名 "Unit_{npcId}_h{h}"）。失败返回 0。
        private static int ResolveNpcIdFromUnitName(GameObject go)
        {
            if (go == null) return 0;
            var name = go.name;
            if (string.IsNullOrEmpty(name) || !name.StartsWith("Unit_")) return 0;
            int us = name.IndexOf('_', 5);   // "Unit_" 后第一个 '_'（npcId 段终点）
            if (us <= 5) return 0;
            return int.TryParse(name.Substring(5, us - 5), out int id) ? id : 0;
        }

        // 源单位当前朝向（faceRight）：从其 SpriteRenderer.flipX 推（Battle_SetUnitFacing 维护 flipX=!faceRight）。
        // 找不到 → 默认朝右（true）。Tracking 模式用（追单位无固定方向，按 spawn 时朝向出膛）。
        private static bool ResolveSrcFaceRight(long srcHandle)
        {
            if (_visualFaceRight.TryGetValue(srcHandle, out bool faceRight))
                return faceRight;
            var sr = GetRenderer(srcHandle);
            return sr == null || !sr.flipX;
        }

        // 源单位世界 X（落点朝向判定用）；找不到返回 0。
        private static float GetSrcWorldX(long srcHandle)
        {
            if (_units.TryGetValue(srcHandle, out var su) && su != null) return su.transform.position.x;
            return 0f;
        }

        /// <summary>P1.6: 追单位投掷物（带死亡 fallback - ProjectileTicker 内部切 FlyToPoint）。</summary>
        public static long Battle_SpawnProjectileTracking(long srcHandle, long tgtHandle, string projectileKey, float speed)
        {
            try
            {
                Transform target = null;
                if (_units.TryGetValue(tgtHandle, out var tu) && tu != null) target = tu.transform;
                if (target == null) { Debug.LogWarning($"[BattleBridge] Tracking 目标 {tgtHandle} 不存在"); return 0; }

                var s = SpawnProjectileShell(srcHandle, projectileKey, ResolveSrcFaceRight(srcHandle));
                s.ticker.Init(s.handle, tgtHandle, target, s.spawnPos, speed > 0 ? speed : 8f, 0.2f);
                return s.handle;
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SpawnProjectileTracking 失败: {e.Message}"); return 0; }
        }

        /// <summary>P1.6: 飞向固定 cell（落点不追单位，敌人移动也不变方向）。</summary>
        public static long Battle_SpawnProjectileToCell(long srcHandle, int landingRow, int landingCol, string projectileKey, float speed)
        {
            try
            {
                float wx = Battle_CellToWorldX(landingRow, landingCol);
                float wy = Battle_CellToWorldY(landingRow, landingCol);
                // 落点在源单位左侧 → 朝左（投石/落格类一般朝己方区方向）
                bool faceRight = wx >= GetSrcWorldX(srcHandle);
                var s = SpawnProjectileShell(srcHandle, projectileKey, faceRight);
                s.ticker.InitFlyToPoint(s.handle, s.spawnPos, new Vector2(wx, wy), speed > 0 ? speed : 8f, 0.15f);
                return s.handle;
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SpawnProjectileToCell 失败: {e.Message}"); return 0; }
        }

        /// <summary>P1.6: 通道穿越投掷物。沿 (dirX,dirY) 飞 distance 距离，width 半径内的敌人触发命中。</summary>
        public static long Battle_SpawnProjectileLine(long srcHandle, float dirX, float dirY, float distance, float width, string projectileKey, float speed)
        {
            try
            {
                var s = SpawnProjectileShell(srcHandle, projectileKey, dirX >= 0f);
                int sourceTeam = _units.TryGetValue(srcHandle, out var source) && source != null
                    ? source.Team
                    : UnitTeamOwn;
                s.ticker.InitLine(
                    s.handle,
                    s.spawnPos,
                    new Vector2(dirX, dirY),
                    distance,
                    width,
                    speed > 0 ? speed : 8f,
                    sourceTeam);
                return s.handle;
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SpawnProjectileLine 失败: {e.Message}"); return 0; }
        }

        /// <summary>R5 (2026-06-11) 连弩直线投掷物（D1）：第一个敌人挡住即停（命中即回收）；
        /// 飞满 distance 未被阻挡 → 回调 Battle_OnProjectileHit(handle, 0)，直达基地的结算由 Lua stash 决定。</summary>
        public static long Battle_SpawnProjectileLineStop(long srcHandle, float dirX, float dirY, float distance, float width, string projectileKey, float speed)
        {
            try
            {
                var s = SpawnProjectileShell(srcHandle, projectileKey, dirX >= 0f);
                int sourceTeam = _units.TryGetValue(srcHandle, out var source) && source != null
                    ? source.Team
                    : UnitTeamOwn;
                s.ticker.InitLine(
                    s.handle,
                    s.spawnPos,
                    new Vector2(dirX, dirY),
                    distance,
                    width,
                    speed > 0 ? speed : 8f,
                    sourceTeam,
                    true);
                return s.handle;
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SpawnProjectileLineStop 失败: {e.Message}"); return 0; }
        }

        // ============ 网格 1 方法 ============

        /// <summary>
        /// 设置某 cell 的高亮态。stateEnum:
        ///   0=none, 1=yellow(淡黄/可放置区), 2=darkYellow(深黄/升级提示),
        ///   3=green(可放下), 4=red(不可放下), 5=lockedGrey(锁定灰/兼容旧 — 走 None),
        ///   6=grey(灰色高亮，Issue 5 — 拖解锁卡时未解锁 cell 提示)
        /// 走 GridMap.Cells[row,col].SetHighlight()，不再 new GameObject（编辑器预摆 cell + 复用 sprite renderer）
        /// </summary>
        public static void Battle_SetCellHighlight(int row, int col, int stateEnum)
        {
            if (GridMap.Cells == null) return;
            if (row < 1 || row > GridMap.Rows || col < 1 || col > GridMap.Cols) return;
            var cv = GridMap.Cells[row, col];
            if (cv == null) return;
            cv.SetHighlight(MapHighlightEnum(stateEnum));
        }

        private static CellView.HL MapHighlightEnum(int stateEnum)
        {
            switch (stateEnum)
            {
                case 1: return CellView.HL.Yellow;
                case 2: return CellView.HL.DeepYellow;
                case 3: return CellView.HL.Green;
                case 4: return CellView.HL.Red;
                default: return CellView.HL.None;
            }
        }

        // ============ 坐标 / 边界 / 排序（tuple 拆分） ============

        public static float Battle_CellToWorldX(int row, int col) => GridMap.CellToWorld(row, col).x;
        public static float Battle_CellToWorldY(int row, int col) => GridMap.CellToWorld(row, col).y;

        /// <summary>
        /// 优先取 BattleCamera（按名字），其次 Camera.main，最后 allCameras[0]。
        /// 修复：BootScene/BattleScene 都有 MainCamera tag → Camera.main 偶发返回 BootCamera 导致坐标错位。
        /// </summary>
        private static Camera GetBattleCamera()
        {
            var cams = Camera.allCameras;
            // 名字含 "Battle" 的优先
            foreach (var c in cams)
            {
                if (c != null && c.name != null && c.name.Contains("Battle"))
                    return c;
            }
            return Camera.main ?? (cams.Length > 0 ? cams[0] : null);
        }

        /// <summary>屏幕像素 → 世界 X（不 snap 到 cell，用于拖拽 ghost 平滑跟随）。</summary>
        public static float Battle_ScreenToWorldX(float sx, float sy)
        {
            var cam = GetBattleCamera();
            if (cam == null) return 0f;
            var w = cam.ScreenToWorldPoint(new Vector3(sx, sy, -cam.transform.position.z));
            return w.x;
        }

        public static float Battle_ScreenToWorldY(float sx, float sy)
        {
            var cam = GetBattleCamera();
            if (cam == null) return 0f;
            var w = cam.ScreenToWorldPoint(new Vector3(sx, sy, -cam.transform.position.z));
            return w.y;
        }

        /// <summary>屏幕像素 → cell row。优先用 BattleCamera；无相机时返回 -1。</summary>
        public static int Battle_ScreenToCellRow(float sx, float sy)
        {
            var cam = GetBattleCamera();
            if (cam == null) return -1;
            var w = cam.ScreenToWorldPoint(new Vector3(sx, sy, -cam.transform.position.z));
            return GridMap.WorldToCellRow(w.y);
        }

        public static int Battle_ScreenToCellCol(float sx, float sy)
        {
            var cam = GetBattleCamera();
            if (cam == null) return -1;
            var w = cam.ScreenToWorldPoint(new Vector3(sx, sy, -cam.transform.position.z));
            return GridMap.WorldToCellCol(w.x);
        }

        public static bool Battle_IsCellInBounds(int row, int col) => GridMap.IsCellInBounds(row, col);

        /// <summary>
        /// 开发/编辑器联调用：重新从 2D 场景布局 XML 构建战场格子。
        /// Lua 业务接口不变；若布局关闭或 XML 失败，返回 false，调用方可继续使用旧场景网格。
        /// </summary>
        public static bool Battle_ReloadScene2DLayout()
        {
            bool ok = GridMap.InitFromScene2DLayout();
            Battlefield2DLayoutBridge.ApplyVisuals();
            return ok;
        }

        /// <summary>
        /// 开发/编辑器联调用：重新从 Scene3D/2.5D 布局 XML 构建战场格子和视觉层。
        /// 失败时不主动回退 Scene2D，便于编辑器联调直接暴露 Scene3D XML 问题。
        /// </summary>
        public static bool Battle_ReloadScene3DLayout()
        {
            bool ok = GridMap.InitFromScene3DLayout();
            if (ok) Battlefield3DLayoutBridge.ApplyVisuals();
            return ok;
        }

        public static int Battle_CalcSortingOrder(float worldY) => GridSortingService.CalcSortingOrder(worldY);

        // ============ 时间 1 方法 ============

        // 业务暂停 flag — 位移 / 攻击 / 投射物 / HitFeedback 等自检。
        public static bool BattlePaused;

        // 暂停时一律 Time.timeScale=0：冻结全部缩放时间逻辑（帧动画 / 粒子特效 / 位移 / Lua 计时器）。
        // UI 不受影响 —— 所有面板 controller 的 poll 用 Time.unscaledDeltaTime，按钮 / input / 截图在
        // timeScale=0 下照常工作。HitFeedback 用 unscaledDeltaTime → 另靠 BattlePaused flag 冻结。
        public static void Battle_SetTimeScale(float scale)
        {
            if (scale <= 0.01f)
            {
                BattlePaused = true;
                Time.timeScale = 0f;
            }
            else
            {
                BattlePaused = false;
                Time.timeScale = Mathf.Max(0.01f, scale);
            }
        }

        // 对局表现时钟（秒）。
        // 返回 Time.time（受 timeScale 缩放的时钟）：Battle_SetTimeScale 暂停时 timeScale=0 → Time.time 冻结，
        //   故对局暂停期间本时钟自然停走，CD/buff 不流逝（无需额外 BattlePaused 判定）。
        // 相对比较语义：Lua 侧统一用 now+duration 存到期时间、再与 now 比，基准大小无关，只需单调 + 暂停冻结。
        public static float Battle_GetGameTime()
        {
            return Time.time;
        }

        // ============ 便利查询（给 Lua 调试 / 业务可选用） ============

        public static int Battle_GetUnitCount() => _units.Count;
        public static int Battle_GetProjectileCount() => _projectiles.Count;
    }
}
