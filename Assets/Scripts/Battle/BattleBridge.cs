using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Utils;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Battle
{
    /// <summary>
    /// Lua → C# Battle 桥（v3 design.md §4.2.1）。
    ///
    /// 设计原则（AGENTS.md §1.1 + §6）：
    ///   - 全 static，便于 Lua 端走 `CS.HeroDefense.Battle.BattleBridge.XXX()` 或经 LuaHost 注入的 Battle_* 全局函数
    ///   - **不写业务**（不判断"该不该 spawn"，仅执行）
    ///   - 句柄表 long → UnitView / EnemyMover / ProjectileTicker，Lua 仅持 handle
    ///   - tuple 拆分避 xLua delegate userdata 坑（AGENTS.md §10 R-V8）：
    ///       Vector2 / (row,col) 返回 → 拆为 _X / _Y / _Row / _Col 多个标量函数
    ///   - SpawnUnit / DestroyUnit 同步维护 HitFeedback.RegisterHandle / UnregisterHandle（Agent E 表现层句柄表）
    ///
    /// 18 个方法分组：
    ///   单位 5：SpawnUnit / DestroyUnit / SetSprite / PlayAnim / SetWorldPosition
    ///   敌人 3：SpawnEnemy / SetEnemyHpBar / SetEnemySpeed
    ///   投射物 1：SpawnProjectile
    ///   网格 1：SetCellHighlight
    ///   坐标 5 拆分：CellToWorldX/Y / ScreenToCellRow/Col / CalcSortingOrder
    ///   边界 3：IsCellInBounds / IsCellInPlayerArea / IsCellInCamp
    ///   时间 1：SetTimeScale
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class BattleBridge
    {
        // ============ 句柄表 ============
        private static long _handleCounter = 1;
        private static readonly Dictionary<long, UnitView> _units = new Dictionary<long, UnitView>();
        private static readonly Dictionary<long, EnemyMover> _enemies = new Dictionary<long, EnemyMover>();
        // 2026-06-30 — 怪物身体基准缩放缓存（按 idle 算一次·切动作复用·见 FitEnemyToCell）。
        private static readonly Dictionary<long, float> _enemyFitScale = new Dictionary<long, float>();
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
        private const float HP_BAR_LOCAL_Y = 1.78f;
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
        private static GameObject BuildHpBar(GameObject root, SpriteRenderer baseSr, float localY, Color fillColor)
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
            bool ally = fillColor.g >= fillColor.r;
            var fillSprite = ally
                ? LoadHpBarSprite("resources/art/ui/hp_bar/hp_bar_fill_ally.png", true, ref _hpBarFillAllySprite)
                : LoadHpBarSprite("resources/art/ui/hp_bar/hp_bar_fill_enemy.png", true, ref _hpBarFillEnemySprite);
            bool fillUsesAsset = fillSprite != null;
            fillSr.sprite = fillUsesAsset ? fillSprite : GetOrCreateWhitePixelSprite(true);
            fillSr.color = fillUsesAsset ? Color.white : fillColor;
            fillSr.sortingLayerID = layerId;
            fillSr.sortingOrder = baseOrder + 1001;
            LayoutHpBar(hpBar.transform, baseSr);
            hpBar.SetActive(true);
            return hpBar;
        }

        internal static float LayoutHpBar(Transform hpBar, SpriteRenderer baseSr)
        {
            if (hpBar == null) return 1f;
            var lp = hpBar.localPosition;
            hpBar.localPosition = new Vector3(0f, HP_BAR_LOCAL_Y, lp.z);

            var bg = hpBar.Find("bg");
            var fill = hpBar.Find("fill");
            float oldMax = bg != null ? Mathf.Max(0.0001f, bg.localScale.x) : 1f;
            float pct = 1f;
            if (fill != null) pct = Mathf.Clamp01(fill.localScale.x / oldMax);

            var bgSr = bg != null ? bg.GetComponent<SpriteRenderer>() : null;
            var fillSr = fill != null ? fill.GetComponent<SpriteRenderer>() : null;
            var bgScale = ScaleForWorldSize(bgSr != null ? bgSr.sprite : null, HP_BAR_WIDTH, HP_BAR_HEIGHT);
            var fillScale = ScaleForWorldSize(fillSr != null ? fillSr.sprite : null, HP_BAR_WIDTH, HP_BAR_HEIGHT);
            if (bg != null) bg.localScale = bgScale;
            if (fill != null)
            {
                fill.localPosition = new Vector3(-HP_BAR_WIDTH * 0.5f, 0f, 0f);
                fill.localScale = new Vector3(fillScale.x * pct, fillScale.y, 1f);
            }

            int layerId = baseSr != null ? baseSr.sortingLayerID : 0;
            int baseOrder = baseSr != null ? baseSr.sortingOrder : 0;
            if (bgSr != null)
            {
                bgSr.sortingLayerID = layerId;
                bgSr.sortingOrder = baseOrder + 1000;
            }
            if (fillSr != null)
            {
                fillSr.sortingLayerID = layerId;
                fillSr.sortingOrder = baseOrder + 1001;
            }

            return fillScale.x;
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
                if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
                try { HitFeedback.UnregisterHandle(kv.Key); } catch { /* silent */ }
            }
            _units.Clear();

            foreach (var kv in _enemies)
            {
                if (kv.Value != null) Object.Destroy(kv.Value.gameObject);
                try { HitFeedback.UnregisterHandle(kv.Key); } catch { /* silent */ }
            }
            _enemies.Clear();
            _enemyFitScale.Clear();

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
            try
            {
                var go = new GameObject($"Unit_{npcId}_h?");
                var wp = GridMap.CellToWorld(row, col);
                go.transform.position = new Vector3(wp.x, wp.y, 0f);

                // sprite_root 子节点（SpriteRenderer 挂在子节点上，UIFinder 能找到）— 消除"找不到 sprite_root"警告
                var spriteRoot = new GameObject("sprite_root");
                spriteRoot.transform.SetParent(go.transform, false);
                var sr = spriteRoot.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = HDSortingLayers.Tower;
                sr.sortingOrder = GridSortingService.CalcSortingOrderForRow(row);

                // T203/T214 hp_bar（2026-06-03 抽 BuildHpBar 复用）：友军绿，头顶 0.4。
                BuildHpBar(go, sr, 0.4f, new Color(0.2f, 1f, 0.2f, 1f));

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
                go.name = $"Unit_{npcId}_h{h}";
                _units[h] = view;

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
                Debug.LogError($"[BattleBridge] Battle_SpawnUnit 失败: {e.Message}");
                return 0;
            }
        }

        public static void Battle_DestroyUnit(long handle)
        {
            if (handle == 0) return;
            if (_units.TryGetValue(handle, out var view))
            {
                _units.Remove(handle);
                try { HitFeedback.UnregisterHandle(handle); } catch { /* silent */ }
                if (view != null && view.gameObject != null) Object.Destroy(view.gameObject);
            }
            else if (_enemies.TryGetValue(handle, out var em))
            {
                _enemies.Remove(handle);
                _enemyFitScale.Remove(handle);
                try { HitFeedback.UnregisterHandle(handle); } catch { /* silent */ }
                if (em != null && em.gameObject != null) Object.Destroy(em.gameObject);
            }
            else if (_projectiles.TryGetValue(handle, out var p))
            {
                // 走池路径
                RecycleProjectile(handle);
            }
        }

        // ============ 2026-05-29 Q1 — npc.tab.anim_type 反查 ============
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

        // ============ Round 12 — occupy 形状反查（Issue 1/4） ============
        // npc.txt occupy_id → occupy.txt width/height。查不到任一环节都回落 1×1。
        // 注：这是渲染层几何（collider / sprite 包围盒）所需的底层信息，与 ResolveWaypointsForLane
        //     / EnsureProjectilePoolConfig 同属 BattleBridge 已有的"spawn 时读配置"模式。
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
        private const int ANIM_MAX_FRAMES = 16;  // 2026-05-29 (Q2): 单个 state 探测上限 16 帧（GetAnimFrames 是 per-state 调用：idle 一次、attack 一次...）。实际帧数仍按文件自动检测（首个缺失帧停止），美术 8 帧就播 8 帧，16 帧就播 16 帧。一个角色 6 状态总帧数上限 96

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
        private static void PlayAnim_Spine(SpriteAnimator anim, string stateName)
        {
            long h = 0;
            // 找 handle（用于去重 warning）— 不影响功能
            if (anim != null && anim.gameObject != null)
            {
                var view = anim.gameObject.GetComponent<UnitView>();
                if (view != null) h = view.Handle;
            }
            if (!_spineWarnedHandles.Contains(h))
            {
                _spineWarnedHandles.Add(h);
                Debug.LogWarning($"[BattleBridge] anim_type=atSpine 配置但 spine-unity SDK 未集成 → 兜底走 frame 路径（key={anim.SpriteBaseKey}, state={stateName}）");
            }
            // fallback：走 frame 路径（与下方相同逻辑）
            var frames = ResolveAnimFrames(anim.SpriteBaseKey, stateName);
            if (frames.Length == 0) return;
            bool looping = IsLoopingState(stateName);
            anim.Play(stateName, frames, fps: AnimFpsFor(stateName), looping: looping);
            if (_units.TryGetValue(h, out var uv) && uv != null) uv.FitSpriteToBlock();
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
                    foreach (var st in new[] { "idle", "walk", "walk_up", "walk_down", "attack", "attack_up", "attack_down", "die" })
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
            if (state != null && _animFps.TryGetValue(state, out var f) && f >= 0.1f) return f;
            // 方向后缀态无独立配置 → 继承基础态（anim_fps_walk_up 缺省时用 anim_fps_walk）。2026-06-11
            if (state != null)
            {
                string baseState = StripDirSuffix(state);
                if (baseState != state && _animFps.TryGetValue(baseState, out var bf) && bf >= 0.1f) return bf;
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

        /// <summary>状态名 → 帧数组，带两级回退（2026-06-11 四方向移动）：
        ///   1) 方向后缀状态缺帧 → 回退基础状态（walk_up/walk_down→walk；attack_up/attack_down→attack）。
        ///      美术分批补方向帧期间（当前仅孙尚香有 attack_up/down、全员无 walk_up/down），缺帧角色自动用侧面帧顶。
        ///   2) 仍 0 帧 → 回退 idle（T237：怪复用武将美术无 walk 状态时播 idle 循环而非僵首帧）。</summary>
        private static Sprite[] ResolveAnimFrames(string baseKey, string stateName)
        {
            var frames = GetAnimFrames(baseKey, stateName);
            if (frames.Length == 0)
            {
                string baseState = StripDirSuffix(stateName);
                if (baseState != stateName) frames = GetAnimFrames(baseKey, baseState);
            }
            if (frames.Length == 0 && stateName != "idle")
            {
                frames = GetAnimFrames(baseKey, "idle");
            }
            return frames;
        }

        /// <summary>"walk_up"→"walk"、"attack_down"→"attack"；无方向后缀原样返回。</summary>
        private static string StripDirSuffix(string state)
        {
            if (string.IsNullOrEmpty(state)) return state;
            if (state.EndsWith("_up")) return state.Substring(0, state.Length - 3);
            if (state.EndsWith("_down")) return state.Substring(0, state.Length - 5);
            return state;
        }

        /// <summary>持续循环态判定：idle + walk 全方向（walk/walk_up/walk_down）。attack*/die 一次性。</summary>
        private static bool IsLoopingState(string state)
        {
            return state == "idle" || (state != null && state.StartsWith("walk"));
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
            if (_enemies.TryGetValue(handle, out var em) && em != null)
                return em.GetComponent<SpriteAnimator>();
            return null;
        }

        private static SpriteRenderer GetRenderer(long handle)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
                return view.GetComponentInChildren<SpriteRenderer>();
            if (_enemies.TryGetValue(handle, out var em) && em != null)
                return em.GetComponentInChildren<SpriteRenderer>();
            return null;
        }

        // 全局兜底占位 sprite — 配置 sprite_key 找不到时显示这个，保证 unit 至少可见
        private static Sprite _fallbackSprite;
        private static Sprite GetFallbackSprite()
        {
            if (_fallbackSprite != null) return _fallbackSprite;
            // 优先用枪兵 idle_0 作为兜底（保证存在）
            _fallbackSprite = HeroDefense.Engine.Host.LuaHost.LoadSprite("resources/art/unit/spearman_idle_0.png");
            if (_fallbackSprite == null)
                _fallbackSprite = HeroDefense.Engine.Host.LuaHost.LoadSprite("resources/art/unit/archer_idle_0.png");
            return _fallbackSprite;
        }

        // 怪物尺寸与武将同步：root 只负责世界位置，sprite_root 负责图片缩放/脚底锚定。
        // 按首帧计算一次 UI 像素等效缩放，切动画帧复用，避免攻击帧画布变化导致身体缩放抖动。
        private static void FitEnemyToCell(long handle, bool recomputeScale = false)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return;
            var sr = em.GetComponentInChildren<SpriteRenderer>();
            if (sr == null || sr.sprite == null) return;
            float s;
            if (recomputeScale || !_enemyFitScale.TryGetValue(handle, out s) || s <= 0f)
            {
                var sz = sr.sprite.bounds.size;
                if (sz.x <= 0.0001f || sz.y <= 0.0001f) return;
                s = UnitView.CalcScreenPixelEquivalentScale(sr.sprite, sz);
                _enemyFitScale[handle] = s;
            }
            sr.transform.localScale = new Vector3(s, s, 1f);

            var bb = sr.sprite.bounds;
            var lp0 = sr.transform.localPosition;
            float lx = -bb.center.x * s;
            float ly = -GridMap.CellSizeY * 0.5f - (bb.center.y - bb.extents.y) * s;
            sr.transform.localPosition = new Vector3(lx, ly, lp0.z);
            em.RefreshHpBarLayout();
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
            var sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}/atlas/idle_0.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}/atlas/walk_0.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}_idle_0.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}_walk_0.png");
            // 3. 所有 fallback 失败 → 用全局兜底，保证 unit 可见（配置错配占位文件时不至于无视觉）
            if (sprite == null) sprite = GetFallbackSprite();
            if (sprite == null) return;

            var sr = GetRenderer(handle);
            if (sr != null) sr.sprite = sprite;

            // sprite 设好后按 footprint 脚底锚定；友军单位尺寸对齐拖拽 UI ghost，不再按格子压缩。
            if (_units.TryGetValue(handle, out var uv) && uv != null) uv.FitSpriteToBlock(true);
            else FitEnemyToCell(handle, true);   // 怪物按武将同尺寸显示；换底图(idle)→重算身体基准缩放
        }

        public static void Battle_PlayAnim(long handle, string stateName)
        {
            Battle_PlayAnim(handle, stateName, 1f);
        }

        // speedMult: 攻速加成倍率（基准 1.0）。攻击动画 fps = round(基础fps × mult)，攻速 buff 越高出手越快。2026-06-07
        public static void Battle_PlayAnim(long handle, string stateName, float speedMult)
        {
            var anim = GetAnimator(handle);
            if (anim == null) return;
            if (string.IsNullOrEmpty(anim.SpriteBaseKey))
            {
                anim.SendMessage("OnAnimStateChanged", stateName, SendMessageOptions.DontRequireReceiver);
                return;
            }

            // 2026-05-29 (Q1) — 按 AnimType 分发：spine 走 stub（log + fallback 到 frame）；frame 走原路径
            if (anim.AnimType == "atSpine")
            {
                PlayAnim_Spine(anim, stateName);
                return;
            }

            // 帧数按文件实际存在自动探测（缓存）+ 方向后缀/idle 双级回退
            var frames = ResolveAnimFrames(anim.SpriteBaseKey, stateName);
            if (frames.Length == 0) return;

            bool looping = IsLoopingState(stateName);
            anim.Play(stateName, frames, fps: ScaledFps(stateName, speedMult), looping: looping);  // fps 随攻速倍率缩放(整数取整)

            // Play 已把首帧设到 SpriteRenderer，按 footprint 脚底锚定；友军单位尺寸对齐拖拽 UI ghost。
            if (_units.TryGetValue(handle, out var uv) && uv != null) uv.FitSpriteToBlock();
            else FitEnemyToCell(handle);   // 切态复用 idle 身体基准缩放(不重算→身体不缩·兵器溢出)
        }

        // v2 批 1b（2026-06-14）C#④：取某单位/怪某动画状态在给定攻速倍率下的播放时长（秒）。
        // 帧数走 ResolveAnimFrames（含方向后缀/idle 两级回退，与 Battle_PlayAnim 同源）；fps 走 ScaledFps。
        // 用途：Lua 出手点定时（普攻减 CD：attack 动画时长 × atk_hit_pct = 出手帧时刻）/ 技能动画对齐。
        // handle/animator 无效或 baseKey 空或帧数=0/fps<=0 → 返回 0（Lua 侧自行兜底）。
        public static float Battle_GetAnimLen(long handle, string state, float speedMult)
        {
            var anim = GetAnimator(handle);
            if (anim == null || string.IsNullOrEmpty(anim.SpriteBaseKey)) return 0f;
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
            else if (_enemies.TryGetValue(handle, out var em) && em != null)
            {
                em.transform.position = new Vector3(wx, wy, 0f);
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

        /// <summary>2026-06-14 用户：拖拽【移动中】单位时按它"当前视觉格"动态算路径(只读,不停 GridMover——
        /// 按下不改变卡片状态,卡片继续走)。读 transform.position → WorldToCell → cellId。-1=单位不存在。</summary>
        public static int Battle_GetUnitCell(long handle)
        {
            if (!_units.TryGetValue(handle, out var view) || view == null) return -1;
            var p = view.transform.position;
            var cell = GridMap.WorldToCell(new Vector2(p.x, p.y));
            return GridMap.RowColToCellId(cell.row, cell.col);
        }

        /// <summary>R2 玩家移动（2026-06-11，块2.2）：场上单位沿 cell 路径逐格走（非瞬移）。
        /// pathCsv = "r,c;r,c;..."（Lua Path_Find 产出，不含当前格也可——从当前位置朝首点走）。
        /// 速度读 GameConfig.unit_move_speed（GridMover 内缓存）；到达终点 GridMover 回调 Lua Unit_OnWalkArrived(handle)。
        /// 失败保证（审查 A）：单位不存在/路径全无效时**同步回调 Unit_OnWalkArrived**（Lua 已先置 u.moving=true、
        /// 占格已转移，回调缺席会让单位永久不可拖；Lua 侧另有 watchdog 双保险）。越界 cell 过滤（审查 M）。</summary>
        // v2 批 1b（2026-06-14）C#⑦ 方案A：加 speed 参（逐单位移速，npc.tab.move_speed → Lua 透传；格/秒）。
        // speed<=0 时 GridMover.BeginPath 内回退 ConfigSpeed()（旧 unit_move_speed 兜底，批4 删）。
        // xLua 注册为 Action<long,string,float>；只传 2 参的旧 Lua 调用（unit_logic.lua）→ xLua 补 speed=0 → 走兜底，零回归。
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

        /// <summary>设单位/怪朝向。faceRight=true 朝右(原图方向)，false flipX 朝左。攻击时朝目标。</summary>
        public static void Battle_SetUnitFacing(long handle, bool faceRight)
        {
            if (_units.TryGetValue(handle, out var view) && view != null)
            {
                view.SetFacing(faceRight);
            }
            else if (_enemies.TryGetValue(handle, out var em) && em != null)
            {
                var sr = em.GetComponentInChildren<SpriteRenderer>();
                if (sr != null) sr.flipX = !faceRight;
            }
        }

        // ============ T237 怪物 HSV 暗化（怪 = 武将/兵种黑暗变体） ============
        // 怪物运行时复用武将/兵种美术（Battle_SetSprite 已支持任意 sprite_key），
        // 再给其 SpriteRenderer 套 HeroDefense/SpriteHsvShift material 做 HSV 偏移即暗化。
        // 业务侧（enemy_manager.lua）查 npc.txt / 解析 dark_color_shift；此处只做底层渲染。
        private static Shader _hsvShader;
        private static readonly int HueShiftID   = Shader.PropertyToID("_HueShift");
        private static readonly int SaturationID = Shader.PropertyToID("_Saturation");
        private static readonly int BrightnessID = Shader.PropertyToID("_Brightness");

        private static Shader GetHsvShader()
        {
            if (_hsvShader == null) _hsvShader = Shader.Find("HeroDefense/SpriteHsvShift");
            return _hsvShader;
        }

        // R5/F3 (2026-06-11) 炸弹③：去逐怪 new Material → 全部怪共享 1 个 HSV material，
        // 逐怪参数走 MaterialPropertyBlock（怪=卡片化后逐怪材质实例 = DrawCall 头号风险）。
        private static Material _hsvSharedMaterial;
        private static MaterialPropertyBlock _hsvMpb;

        /// <summary>
        /// 给怪物 SpriteRenderer 应用 HSV 偏移（暗化）。
        ///   hueShift   — 色相偏移度（dark_color_shift shift_hue）
        ///   saturation — 饱和度乘子（dark_color_shift saturation，<1 去色）
        ///   brightness — 亮度乘子（dark_color_shift brightness，<1 暗化）
        /// R5/F3：所有怪共享 1 个 SpriteHsvShift material，逐怪参数经 MaterialPropertyBlock
        /// 下发（不再 new Material）。HSV shader 同时含 _FlashColor/_FlashAmount，受击闪白
        /// （HitFeedback，同样走 MPB）仍生效。仅对怪物（_enemies）生效；单位（_units）调用无效果。
        /// </summary>
        public static void Battle_SetEnemyHsv(long handle, float hueShift, float saturation, float brightness)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return;
            var sr = em.GetComponentInChildren<SpriteRenderer>();
            if (sr == null) return;

            var shader = GetHsvShader();
            if (shader == null)
            {
                Debug.LogWarning("[BattleBridge] HeroDefense/SpriteHsvShift shader 未找到，跳过 HSV 暗化");
                return;
            }
            if (_hsvSharedMaterial == null) _hsvSharedMaterial = new Material(shader);
            if (sr.sharedMaterial != _hsvSharedMaterial) sr.sharedMaterial = _hsvSharedMaterial;

            if (_hsvMpb == null) _hsvMpb = new MaterialPropertyBlock();
            // 先 Get 再改再 Set：保留 Unity 给 SpriteRenderer 内部写入的 _MainTex 等 block 值
            sr.GetPropertyBlock(_hsvMpb);
            _hsvMpb.SetFloat(HueShiftID, hueShift);
            _hsvMpb.SetFloat(SaturationID, saturation);
            _hsvMpb.SetFloat(BrightnessID, brightness);
            sr.SetPropertyBlock(_hsvMpb);
        }

        // ============ 敌人 3 方法 ============

        /// <summary>查 lane.txt 取 (level_id, lane_id) 的 waypoints_json，拼成 world Vector2 列表（[col,row] → CellToWorld）。
        /// T212：spawnRowOverride > 0 时把非营帐 waypoint 的 row 整体平移到 spawn_row，让 lane 沿入场行水平推进。</summary>
        private static List<Vector2> ResolveWaypointsForLane(int levelId, int laneId, int spawnRowOverride = 0)
        {
            var result = new List<Vector2>();
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return result;
                cm.LoadIfNeeded();
                var rows = cm.GetTableInfoList("lane", "level_id", levelId);
                if (rows == null) return result;
                Dictionary<string, object> matchedRow = null;
                foreach (var r in rows)
                {
                    if (r.TryGetValue("lane_id", out var v) && int.TryParse(v.ToString(), out int lid) && lid == laneId)
                    {
                        matchedRow = r;
                        break;
                    }
                }
                if (matchedRow == null)
                {
                    Debug.LogWarning($"[BattleBridge] lane.txt 找不到 level={levelId} lane={laneId}，用默认中路兜底");
                    return FallbackLane(spawnRowOverride);
                }

                if (!matchedRow.TryGetValue("waypoints_json", out var rawJson)) return result;
                string json = rawJson?.ToString() ?? "";
                if (string.IsNullOrEmpty(json)) return result;

                // 简单解析 [[col,row],[col,row],...] — 提取数字对
                var matches = System.Text.RegularExpressions.Regex.Matches(json, @"\[\s*(\d+)\s*,\s*(\d+)\s*\]");
                int wpTotal = matches.Count;
                int wpIdx = 0;
                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    int col = int.Parse(m.Groups[1].Value);
                    int row = int.Parse(m.Groups[2].Value);
                    // v7：lane 末点 = 左基地（网格外左侧城墙，怪到此=失败）；前面的点 T212 平移到 spawn_row 水平推进。
                    bool isLast = (wpIdx == wpTotal - 1);
                    if (!isLast && spawnRowOverride > 0) row = spawnRowOverride;
                    Vector2 wp;
                    if (isLast)
                    {
                        int er = spawnRowOverride > 0 ? spawnRowOverride : row;
                        float wallX = GetCampWorldPos().x;          // 左基地世界 X（网格外左侧）
                        float rowY = GridMap.CellToWorld(er, 1).y;  // 用最左可玩列(col1)的行 Y → 怪停在自己行的城墙
                        wp = new Vector2(wallX, rowY);
                    }
                    else
                    {
                        wp = GridMap.CellToWorld(row, col);
                    }
                    result.Add(wp);
                    wpIdx++;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BattleBridge] ResolveWaypointsForLane 异常: {e.Message}");
            }
            // 兜底：任何原因导致 waypoints 为空（lane 缺失 / json 缺失 / 解析失败）→ 默认中路，
            // 否则 EnemyMover._waypoints 为空 → 怪原地不动 → 不到营帐不死 → 波次永远卡死。
            if (result.Count == 0)
            {
                Debug.LogWarning($"[BattleBridge] lane level={levelId} lane={laneId} waypoints 为空，用默认中路兜底");
                return FallbackLane(spawnRowOverride);
            }
            return result;
        }

        /// <summary>lane 缺失/解析失败兜底：默认中路 col 13→8→3→营帐，避免怪原地不动卡死波次。</summary>
        private static List<Vector2> FallbackLane(int spawnRowOverride)
        {
            int fr = spawnRowOverride > 0 ? spawnRowOverride : 4;
            return new List<Vector2>
            {
                GridMap.CellToWorld(fr, GridMap.Cols + 1),
                GridMap.CellToWorld(fr, 8),
                GridMap.CellToWorld(fr, 3),
                GetCampWorldPos(),
            };
        }

        /// <summary>营帐 sprite 的真实世界坐标（lane 末点用）。CampVisual 无则回落到公式估算。</summary>
        // v7：左基地在网格最左列(col1)左侧一格。从 CellView 边缘算（不依赖 CampVisual 摆位时机/camp_rect）。
        private static Vector2 GetCampWorldPos()
        {
            Battlefield2DLayoutBridge.CampWallLayout layout;
            if (Battlefield2DLayoutBridge.TryGetCampWallLayout("left", out layout))
                return new Vector2(layout.x, layout.y);

            float colStep = Mathf.Abs(GridMap.CellToWorld(1, 2).x - GridMap.CellToWorld(1, 1).x);
            if (colStep < 0.01f) colStep = 1.28f;
            var edge = GridMap.CellToWorld(4, 1);
            return new Vector2(edge.x - colStep, edge.y);
        }

        // 原签名:无 spawn_row,EnemyMover 走 lane.txt 默认 row(怪汇集到 lane 中线)
        public static long Battle_SpawnEnemy(string monsterId, int laneId, float spawnX, float spawnY)
        {
            return Battle_SpawnEnemyAtRow(monsterId, laneId, spawnX, spawnY, 0);
        }

        // T212 (2026-05-21):spawnRow > 0 时 ResolveWaypointsForLane 把非营帐 waypoint 整体平移到该行,
        // 让怪沿入场行水平推进直到营帐 — 8 行随机入场后视觉上各行独立,不再几秒后汇为一条直线。
        public static long Battle_SpawnEnemyAtRow(string monsterId, int laneId, float spawnX, float spawnY, int spawnRow)
        {
            try
            {
                var go = new GameObject($"Enemy_{monsterId}_lane{laneId}");
                go.transform.position = new Vector3(spawnX, spawnY, 0f);

                var spriteRoot = new GameObject("sprite_root");
                spriteRoot.transform.SetParent(go.transform, false);
                var sr = spriteRoot.AddComponent<SpriteRenderer>();
                sr.sortingLayerName = HDSortingLayers.Enemy;
                sr.sortingOrder = GridSortingService.CalcSortingOrder(spawnY);

                var em = go.AddComponent<EnemyMover>();
                go.AddComponent<SpriteAnimator>();

                // 2026-06-03 — 怪物头顶血条：与单位同款 hp_bar(bg+fill)，敌军红。
                //   初始 Y 占位 0.4，EnemyMover 在 sprite 就绪后按 sprite 实际高度重定位到头顶。
                //   sr=root SpriteRenderer（Enemy 层）→ 血条叠在 Enemy 层本体之上。
                var hpBar = BuildHpBar(go, sr, 0.4f, new Color(0.9f, 0.25f, 0.2f, 1f));
                em.BindHpBar(hpBar);

                long h = NextHandle();
                // 自动从 lane.txt 拼 waypoints（业务 Lua 不操心）, spawnRow>0 时整条 lane 平移到 spawn 行
                var waypoints = ResolveWaypointsForLane(BattleSceneController.PendingLevelId, laneId, spawnRow);
                em.Init(h, 1f /* speed: Lua 之后通过 SetEnemySpeed 注入 */, waypoints, new Vector2(spawnX, spawnY));
                go.name = $"Enemy_{monsterId}_h{h}";
                _enemies[h] = em;

                // 同步注册到 HitFeedback 句柄表（怪也能受打击四件套）
                try { HitFeedback.RegisterHandle(h, go); } catch { /* silent */ }

                return h;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleBridge] Battle_SpawnEnemy 失败: {e.Message}");
                return 0;
            }
        }

        // pct = hp/max_hp (0..1)。怪走 EnemyMover 自持的 hp_bar（2026-06-03）；
        //   友军单位（Unit_TakeDamage/Unit_Heal 也调本函数）走 UnitView。
        public static void Battle_SetEnemyHpBar(long handle, float pct)
        {
            if (_enemies.TryGetValue(handle, out var em) && em != null)
            {
                em.SetHp(pct);
            }
            else if (_units.TryGetValue(handle, out var u) && u != null)
            {
                u.SetHp(pct, 1f);
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

        public static void Battle_SetEnemySpeed(long handle, float speed)
        {
            if (_enemies.TryGetValue(handle, out var em) && em != null)
            {
                em.Speed = speed;
            }
        }

        /// <summary>怪进入/退出攻击状态时 Lua 调用 → 暂停/恢复位移（怪攻击时停下来）。</summary>
        public static void Battle_SetEnemyHalted(long handle, bool halted)
        {
            if (_enemies.TryGetValue(handle, out var em) && em != null)
            {
                em.Halted = halted;
            }
        }

        // 怪物当前 cell（按 GameObject world 坐标实时反查）。
        // attack_logic 索敌靠 enemy.row/col —— 怪物移动后 Lua 侧 enemy_manager.Enemy_UpdateCell 调本接口刷新。
        // 未找到 / 已销毁 → 返回 -1。
        public static int Battle_GetEnemyRow(long handle)
        {
            if (_enemies.TryGetValue(handle, out var em) && em != null)
                return GridMap.WorldToCellRow(em.transform.position.y);
            return -1;
        }

        public static int Battle_GetEnemyCol(long handle)
        {
            if (_enemies.TryGetValue(handle, out var em) && em != null)
                return GridMap.WorldToCellCol(em.transform.position.x);
            return -1;
        }

        /// <summary>怪物网格步进中接战：停在当前视觉位置并返回当前 cellId，Lua 负责同步 e.row/e.col 与占格表。</summary>
        public static int Battle_GetEnemyCellAndStop(long handle)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return -1;
            em.StopStep();
            var p = em.transform.position;
            var cell = GridMap.WorldToCell(new Vector2(p.x, p.y));
            return GridMap.RowColToCellId(cell.row, cell.col);
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
                _projectileSprite = HeroDefense.Engine.Host.LuaHost.LoadSprite("resources/art/projectile/arrow.png");
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
                else if (_enemies.TryGetValue(tgtHandle, out var te) && te != null) target = te.transform;
                if (target == null)
                {
                    Debug.LogWarning($"[BattleBridge] Battle_SpawnProjectile 目标 {tgtHandle} 不存在");
                    return 0;
                }

                Vector2 spawn;
                if (_units.TryGetValue(srcHandle, out var su) && su != null) spawn = su.transform.position;
                else if (_enemies.TryGetValue(srcHandle, out var se) && se != null) spawn = se.transform.position;
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

        /// <summary>P1.6: 枚举所有存活敌人（ProjectileTicker.Line 模式扫线用）。</summary>
        public static IEnumerable<KeyValuePair<long, EnemyMover>> EnumerateEnemies()
        {
            return _enemies;
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
            else if (_enemies.TryGetValue(srcHandle, out var se) && se != null) unitPos = se.transform.position;
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

        // 源单位/怪当前朝向（faceRight）：从其 SpriteRenderer.flipX 推（Battle_SetUnitFacing 维护 flipX=!faceRight）。
        // 找不到 → 默认朝右（true）。Tracking 模式用（追单位无固定方向，按 spawn 时朝向出膛）。
        private static bool ResolveSrcFaceRight(long srcHandle)
        {
            var sr = GetRenderer(srcHandle);
            return sr == null || !sr.flipX;
        }

        // 源单位/怪世界 X（落点朝向判定用）；找不到返回 0。
        private static float GetSrcWorldX(long srcHandle)
        {
            if (_units.TryGetValue(srcHandle, out var su) && su != null) return su.transform.position.x;
            if (_enemies.TryGetValue(srcHandle, out var se) && se != null) return se.transform.position.x;
            return 0f;
        }

        /// <summary>P1.6: 追单位投掷物（带死亡 fallback - ProjectileTicker 内部切 FlyToPoint）。</summary>
        public static long Battle_SpawnProjectileTracking(long srcHandle, long tgtHandle, string projectileKey, float speed)
        {
            try
            {
                Transform target = null;
                if (_units.TryGetValue(tgtHandle, out var tu) && tu != null) target = tu.transform;
                else if (_enemies.TryGetValue(tgtHandle, out var te) && te != null) target = te.transform;
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
                s.ticker.InitLine(s.handle, s.spawnPos, new Vector2(dirX, dirY), distance, width, speed > 0 ? speed : 8f);
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
                s.ticker.InitLine(s.handle, s.spawnPos, new Vector2(dirX, dirY), distance, width, speed > 0 ? speed : 8f, true);
                return s.handle;
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SpawnProjectileLineStop 失败: {e.Message}"); return 0; }
        }

        /// <summary>R6: 怪切网格步进模式（清 lane waypoints，spawn 后立即调；greedy 选格在 Lua）。</summary>
        public static void Battle_EnemyGridMode(long handle)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return;
            em.EnterGridMode();
        }

        /// <summary>R6: 怪步进一格到 (row,col)（Lua greedy 决策后调；到格回调 Enemy_OnStepDone）。</summary>
        public static bool Battle_EnemyStepToCell(long handle, int row, int col)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return false;
            float wx = Battle_CellToWorldX(row, col);
            float wy = Battle_CellToWorldY(row, col);
            em.StepTo(new Vector2(wx, wy));
            return true;
        }

        /// <summary>P2b: 怪连续移动到任意世界坐标点（围攻环位 / 沿行推进用；亚格精度）。
        /// StepTo 内部首次调用自动 EnterGridMode（清 lane 路点，转手动 StepTo 控制）。</summary>
        public static bool Battle_EnemyStepToXY(long handle, float wx, float wy)
        {
            if (!_enemies.TryGetValue(handle, out var em) || em == null) return false;
            em.StepTo(new Vector2(wx, wy));
            return true;
        }

        /// <summary>P1.6: 瞬移敌人到指定 cell（etKnockback handler 用，跳过寻路）。</summary>
        public static void Battle_SetEnemyCell(long handle, int row, int col)
        {
            try
            {
                if (!_enemies.TryGetValue(handle, out var em) || em == null) return;
                float wx = Battle_CellToWorldX(row, col);
                float wy = Battle_CellToWorldY(row, col);
                em.transform.position = new Vector3(wx, wy, em.transform.position.z);
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] SetEnemyCell 失败: {e.Message}"); }
        }

        // v2 批 1b（2026-06-14）C#⑥：击退怪 cells 格（etKnockback effect）。
        // 怪沿 lane 朝左基地推进（col 递减）→ 击退 = 反向（远离基地）= +col。
        // 批1 只做 bounds-clamp 的可见瞬移（撞己方单位的精确撞停留批3）；复用 Battle_SetEnemyCell 同一 transform 落位路径。
        // cells 四舍五入取整格；<=0 或越界 clamp 后无位移则静默。
        public static void Battle_KnockbackEnemy(long handle, float cells)
        {
            try
            {
                if (!_enemies.TryGetValue(handle, out var em) || em == null) return;
                int n = Mathf.RoundToInt(cells);
                if (n == 0) return;
                int row = Battle_GetEnemyRow(handle);
                int col = Battle_GetEnemyCol(handle);
                if (row < 1 || col < 1) return;   // 反查失败
                int newCol = Mathf.Clamp(col + n, 1, GridMap.Cols);   // 远离基地 = +col；clamp 不出界
                if (newCol == col) return;        // 已贴边 → 无可见位移
                Battle_SetEnemyCell(handle, row, newCol);
            }
            catch (System.Exception e) { Debug.LogError($"[BattleBridge] KnockbackEnemy 失败: {e.Message}"); }
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
        public static bool Battle_IsCellInCamp(int row, int col) => GridMap.IsCellInCamp(row, col);

        // T202 (2026-05-21) — 玩法模式切换 grid 视觉样式（"transparent" / "unlocked_shown"）
        // 由 GridState_Init 末尾调用，把 mode.cell_visual_style 广播给所有 cell
        public static void Battle_SetGridVisualStyle(string style)
        {
            if (GridMap.Cells == null) return;
            for (int r = 1; r <= GridMap.Rows; r++)
                for (int c = 1; c <= GridMap.Cols; c++)
                {
                    var cv = GridMap.Cells[r, c];
                    if (cv != null) cv.SetVisualStyle(style);
                }
        }

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

        public static int Battle_CalcSortingOrder(float worldY) => GridSortingService.CalcSortingOrder(worldY);

        // ============ 拖拽 UI Ghost（Bug 4 fix：让 ghost 显示在 InventoryPanel 之上）============
        // UI Ghost 是一个 UI Image 节点，挂在 BattleHud Canvas 下（与 InventoryPanel 同级），
        // 渲染顺序由 hierarchy 决定 —— 始终 SetAsLastSibling 保证在所有 panel 之上。
        // World ghost (Battle_SpawnUnit) 不再用于 inventory drag。

        private static GameObject _uiGhost;
        private static UnityEngine.UI.Image _uiGhostImage;
        private const float UI_GHOST_CURSOR_PIVOT_X = 0.5f;
        private const float UI_GHOST_CURSOR_PIVOT_Y = 0.25f;

        // 2026-07-01 用户修正：拖拽武将图以鼠标为锚点，鼠标落在图底部向上 1/4 高度处。
        // pivotX/pivotY 参数保留用于 Lua 兼容，实际 UI ghost 使用固定鼠标锚点。
        public static void Battle_ShowUIGhost(string spriteKey, float sx, float sy, float pivotX, float pivotY)
        {
            EnsureUIGhost();
            if (_uiGhost == null) return;
            // 加载 sprite（与 SetSprite 同 fallback 链）
            var sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}_idle_0.png");
            if (sprite == null) sprite = HeroDefense.Engine.Host.LuaHost.LoadSprite($"resources/art/{spriteKey}_walk_0.png");
            if (_uiGhostImage != null)
            {
                _uiGhostImage.sprite = sprite;
                _uiGhostImage.color = new Color(1f, 1f, 1f, 0.85f);
                // Round 8 fix: ghost 尺寸跟随 sprite 实际像素（1:1 像素 → 1:1 sizeDelta），
                // 让多占位卡（256×192 等）显示等同实际占位大小，而不是被钉死成 70×70。
                if (sprite != null)
                {
                    var rt = _uiGhost.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.sizeDelta = new Vector2(sprite.rect.width, sprite.rect.height);
                        rt.pivot = new Vector2(UI_GHOST_CURSOR_PIVOT_X, UI_GHOST_CURSOR_PIVOT_Y);
                    }
                }
            }
            _uiGhost.SetActive(true);
            _uiGhost.transform.SetAsLastSibling();  // 确保最上层
            SetUIGhostScreenPos(sx, sy);
        }

        public static void Battle_MoveUIGhost(float sx, float sy)
        {
            if (_uiGhost == null || !_uiGhost.activeSelf) return;
            SetUIGhostScreenPos(sx, sy);
        }

        public static void Battle_HideUIGhost()
        {
            if (_uiGhost != null) _uiGhost.SetActive(false);
        }

        // 检测屏幕坐标是否在 InventoryPanel UI rect 内 — 拖拽逻辑用来跳过战场 cell 判定
        // 缓存 InventoryPanel 引用，避免每帧 Find
        private static RectTransform _invPanelRT;
        private static Canvas _invPanelCanvas;
        private static Transform _invSlotsContainer;  // Issue 3 — Slots 容器（每个 child 是一个 slot RT）
        public static bool Battle_IsPointerOverInventory(float sx, float sy)
        {
            if (_invPanelRT == null)
            {
                var rootWindow = GameObject.Find("RootWindow");
                if (rootWindow == null) return false;
                var p = rootWindow.transform.Find("BattleHud/InventoryPanel");
                if (p == null) return false;
                _invPanelRT = p as RectTransform;
                _invPanelCanvas = p.GetComponentInParent<Canvas>();
                // Round 10 (2026-05-15) — Slots 可能直接挂 InventoryPanel 下（首次），
                // 也可能在 InventoryPanel/Viewport/Slots（InventoryController.EnsureScrollableLayout 后）
                _invSlotsContainer = p.Find("Slots");
                if (_invSlotsContainer == null) _invSlotsContainer = p.Find("Viewport/Slots");
            }
            if (_invPanelRT == null) return false;
            // InventoryPanel 必须 active（关闭时不挡）
            if (!_invPanelRT.gameObject.activeInHierarchy) return false;
            Camera cam = null;
            if (_invPanelCanvas != null && _invPanelCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                cam = _invPanelCanvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(_invPanelRT, new Vector2(sx, sy), cam);
        }

        // F3 (2026-06-01)：检测屏幕坐标是否在 ShopPanel UI rect 内 — 拖场上单位入商场回收用。
        // ShopPanel 由 ShopController 程序化建于 BattleHud 下；关闭时 SetActive(false) → activeInHierarchy 守卫天然"没开就不拦"。
        private static RectTransform _shopPanelRT;
        private static Canvas _shopPanelCanvas;
        public static bool Battle_IsPointerOverShop(float sx, float sy)
        {
            if (_shopPanelRT == null)
            {
                var rootWindow = GameObject.Find("RootWindow");
                if (rootWindow == null) return false;
                var p = rootWindow.transform.Find("BattleHud/ShopPanel");
                if (p == null) return false;
                _shopPanelRT = p as RectTransform;
                _shopPanelCanvas = p.GetComponentInParent<Canvas>();
            }
            if (_shopPanelRT == null) return false;
            if (!_shopPanelRT.gameObject.activeInHierarchy) return false;
            Camera cam = null;
            if (_shopPanelCanvas != null && _shopPanelCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                cam = _shopPanelCanvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(_shopPanelRT, new Vector2(sx, sy), cam);
        }

        // ============ Issue 3 — 背包 slot index 反查（拖拽落点 → slot 1-based 索引） ============
        // 用法：Lua drag_logic 在 OnDragEnd 时若 over_inv，调用此 API 拿到 target slot index，
        // 然后配合 source slot index 调 Stash_SwapByIndex / Merge_*_InStash 完成背包内拖动语义。
        //
        // 反查策略：遍历 InventoryPanel/Slots 子节点（每个就是一个 slot 容器 RT），
        // 用 RectTransformUtility.RectangleContainsScreenPoint 命中谁就返回 (i + 1)。
        // 返回 -1 = 不在任何 slot 内（在 panel 但点的是 panel 背景 / 空隙）。
        public static int Battle_GetInventorySlotAtScreen(float sx, float sy)
        {
            // 先确保 _invPanelRT / _invSlotsContainer 已初始化
            if (_invPanelRT == null)
            {
                // 调一次 IsPointerOverInventory 复用查找路径
                Battle_IsPointerOverInventory(sx, sy);
            }
            if (_invSlotsContainer == null) return -1;
            if (!_invPanelRT.gameObject.activeInHierarchy) return -1;
            Camera cam = null;
            if (_invPanelCanvas != null && _invPanelCanvas.renderMode == RenderMode.ScreenSpaceCamera)
                cam = _invPanelCanvas.worldCamera;
            var pt = new Vector2(sx, sy);
            int n = _invSlotsContainer.childCount;
            for (int i = 0; i < n; i++)
            {
                var slotTr = _invSlotsContainer.GetChild(i) as RectTransform;
                if (slotTr == null) continue;
                if (!slotTr.gameObject.activeInHierarchy) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(slotTr, pt, cam))
                {
                    return i + 1;  // Lua 1-based
                }
            }
            return -1;
        }

        // ============ 热更 UI 迁移（2026-06-17 HUD 簇）：库存/商场面板引用由 Lua 注册 ============
        // 旧版从 BattleHud/InventoryPanel|ShopPanel 查找；迁移后面板在 Panel_RootWindow 下、路径不同，
        // 改由 Lua 加载面板后调本 setter 注入引用。slotsListGo 传 List 控件根 → 自动解析其 ScrollRect.content 为卡容器。
        public static void Battle_SetInventoryRefs(GameObject invPanel, GameObject slotsListGo)
        {
            _invPanelRT = invPanel != null ? invPanel.transform as RectTransform : null;
            _invPanelCanvas = invPanel != null ? invPanel.GetComponentInParent<Canvas>() : null;
            _invSlotsContainer = null;
            if (slotsListGo != null)
            {
                var sr = slotsListGo.GetComponent<UnityEngine.UI.ScrollRect>();
                _invSlotsContainer = (sr != null && sr.content != null) ? sr.content : slotsListGo.transform;
            }
        }

        public static void Battle_SetShopRef(GameObject shopPanel)
        {
            _shopPanelRT = shopPanel != null ? shopPanel.transform as RectTransform : null;
            _shopPanelCanvas = shopPanel != null ? shopPanel.GetComponentInParent<Canvas>() : null;
        }

        private static void EnsureUIGhost()
        {
            if (_uiGhost != null) return;
            // 已迁热更 UI：旧 BattleHud 始终 inactive → ghost 改挂 RootWindow（挂 inactive 节点下会不可见）。
            // ghost SetAsLastSibling 保证渲染在所有 RootWindow 子面板之上。
            GameObject parent = null;
            var rootWindow = GameObject.Find("RootWindow");
            if (rootWindow != null) parent = rootWindow;
            if (parent == null) return;

            _uiGhost = new GameObject("DragGhost",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(UnityEngine.UI.Image));
            _uiGhost.transform.SetParent(parent.transform, false);
            var rt = _uiGhost.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(UI_GHOST_CURSOR_PIVOT_X, UI_GHOST_CURSOR_PIVOT_Y);
            rt.sizeDelta = new Vector2(70, 70);
            _uiGhostImage = _uiGhost.GetComponent<UnityEngine.UI.Image>();
            _uiGhostImage.raycastTarget = false;  // 不拦截事件，让 InventoryPanel 卡仍可点
            _uiGhost.SetActive(false);
        }

        private static void SetUIGhostScreenPos(float sx, float sy)
        {
            if (_uiGhost == null) return;
            var rt = _uiGhost.GetComponent<RectTransform>();
            var parentRt = rt.parent as RectTransform;
            if (parentRt == null) return;
            // Canvas Camera 路由（ScreenSpaceCamera 模式必传 worldCamera）
            Camera cam = null;
            var canvas = _uiGhost.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera) cam = canvas.worldCamera;
            Vector2 local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parentRt, new Vector2(sx, sy), cam, out local);
            rt.anchoredPosition = local;
        }

        // ============ 时间 1 方法 ============

        // 业务暂停 flag — 怪 / 攻击 / 投射物 / HitFeedback 等自检。
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

        // 权威对局时钟（秒）。v2 批 0 P0 抢修（2026-06-13）：
        //   skill_active_scheduler / buff_runtime / damage_stats 等 Lua 模块均以 Battle_GetGameTime() 为唯一时基，
        //   此前该函数全工程未定义 → 调度器/buff 容器首行 `if Battle_GetGameTime == nil then return` 整段短路
        //   → 武将主动技一次都放不出来、buff 永不到期。此处补上即恢复。
        // 返回 Time.time（受 timeScale 缩放的时钟）：Battle_SetTimeScale 暂停时 timeScale=0 → Time.time 冻结，
        //   故对局暂停期间本时钟自然停走，CD/buff 不流逝（无需额外 BattlePaused 判定）。
        // 相对比较语义：Lua 侧统一用 now+duration 存到期时间、再与 now 比，基准大小无关，只需单调 + 暂停冻结。
        public static float Battle_GetGameTime()
        {
            return Time.time;
        }

        // ============ 便利查询（给 Lua 调试 / 业务可选用） ============

        public static int Battle_GetUnitCount() => _units.Count;
        public static int Battle_GetEnemyCount() => _enemies.Count;
        public static int Battle_GetProjectileCount() => _projectiles.Count;

        // ============ Phase 2 Task 2.6：营帐 HP 查询（CampDetailController 用，避免 Controller 拆 Lua state） ============

        /// <summary>读 Lua `Camp_State.hp`。未初始化 → 0。</summary>
        public static int Battle_GetCampHp()
        {
#if XLUA
            try
            {
                var env = HeroDefense.Engine.Host.LuaHost.Env;
                if (env == null) return 0;
                return env.Global.GetInPath<int>("Camp_State.hp");
            }
            catch { return 0; }
#else
            return 0;
#endif
        }

        /// <summary>读 Lua `Camp_State.max_hp`。未初始化 → 1（避免除零）。</summary>
        public static int Battle_GetCampMaxHp()
        {
#if XLUA
            try
            {
                var env = HeroDefense.Engine.Host.LuaHost.Env;
                if (env == null) return 1;
                int v = env.Global.GetInPath<int>("Camp_State.max_hp");
                return v > 0 ? v : 1;
            }
            catch { return 1; }
#else
            return 1;
#endif
        }

        // ============ Phase 2.10 伤害统计行格式化（DamageStatsController 用，避免 Controller 直接拆 LuaTable）============

        /// <summary>
        /// luaItem = DamageStats_GetSortedList() 返回 list 内单个 row 表，含字段
        ///   handle: long, npc_id: int, lv: int, total_damage: int, dps: float
        /// 返回 "{name} lv{lv}" — 优先查 npc.txt 拿中文 name；查不到回退 "#{npc_id} lv{lv}"
        /// </summary>
        public static string FormatDamageRowName(object luaItem)
        {
#if XLUA
            if (!(luaItem is XLua.LuaTable t)) return "";
            int npcId = 0, lv = 1;
            try { npcId = t.Get<string, int>("npc_id"); } catch { /* silent */ }
            try { lv = t.Get<string, int>("lv"); } catch { /* silent */ }
            string nameCn = null;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var row = cm.GetTableInfo("npc", "id", npcId);
                    if (row != null) nameCn = cm.GetValue<string>(row, "name", null);
                }
            }
            catch { /* silent */ }
            if (string.IsNullOrEmpty(nameCn)) return $"#{npcId} lv{lv}";
            return $"{nameCn} lv{lv}";
#else
            return "";
#endif
        }

        // ============ Phase 2.7 技能卡释放（SkillCardController 用） ============

        /// <summary>
        /// 调 Lua `SkillCard_Cast(skill_id, target_row, target_col)`，
        /// 按 cast_target 在 Lua 侧分支（none/cell/enemy）。
        /// 返回 bool（成功扣 1）。Lua 不就位时返回 false。
        /// </summary>
        public static bool Battle_SkillCardCast(int skillId, int targetRow, int targetCol)
        {
#if XLUA
            try
            {
                var env = HeroDefense.Engine.Host.LuaHost.Env;
                if (env == null) return false;
                var fn = env.Global.Get<XLua.LuaFunction>("SkillCard_Cast");
                if (fn == null) return false;
                var ret = fn.Call(skillId, targetRow, targetCol);
                fn.Dispose();
                if (ret == null || ret.Length == 0) return false;
                if (ret[0] is bool b) return b;
                return ret[0] != null;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BattleBridge] Battle_SkillCardCast 失败: {e.Message}");
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// 返回 "{total_damage}  ({dps:F1}/s)" — total + dps 一行展示
        /// </summary>
        public static string FormatDamageRowDamage(object luaItem)
        {
#if XLUA
            if (!(luaItem is XLua.LuaTable t)) return "0";
            int tot = 0;
            float dps = 0f;
            try { tot = t.Get<string, int>("total_damage"); } catch { /* silent */ }
            try { dps = t.Get<string, float>("dps"); } catch { /* silent */ }
            return $"{tot}  ({dps:F1}/s)";
#else
            return "0";
#endif
        }
    }
}
