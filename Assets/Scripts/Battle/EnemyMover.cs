using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 怪物 waypoint 跟随器（性能热路径）。
    ///
    /// 设计原则（CLAUDE.md §1）：
    ///   - C# 负责"每帧位移"性能热路径，避免 Lua 每帧 tick 200 单位
    ///   - 业务（什么时候 spawn / 死亡 / 元素克制）由 Lua 决定
    ///   - 到达营帐 / 路点切换 → 调 Lua 全局函数 Battle_OnEnemyReachCamp
    ///   - 紧急区降速：col ≤ speed_emergency_zone_cells 时 speed × speed_emergency_scale
    ///
    /// 0 SerializeField — Speed 由 BattleBridge.SetEnemySpeed 注入，waypoints 由 Init 注入。
    /// </summary>
    public class EnemyMover : MonoBehaviour
    {
        public long Handle { get; set; }
        public float Speed { get; set; } = 1f;
        public int Atk { get; set; } = 1;

        // 攻击停顿：怪进入攻击状态时由 Lua 经 Battle_SetEnemyHalted 置 true → 暂停位移。
        // 与 Speed/freeze 独立：怪只有在 !Halted && !Paused && Speed>0 时才移动。
        public bool Halted { get; set; }

        private readonly List<Vector2> _waypoints = new List<Vector2>();
        private int _wpIndex;
        private bool _arrived; // 已到达营帐（防止重复回调 Lua）

        // R6 (2026-06-11) 网格步进模式（块3.2）：greedy 选格由 Lua 决策（权威整数坐标在 Lua），
        // C# 只做"单格 MoveTowards"；到格 → 回调 Lua Enemy_OnStepDone(handle) → Lua 决定下一格。
        // lane waypoint 模式保留作回退开关（GameConfig.enemy_move_mode=lane）。
        private bool _gridMode;
        private bool _stepActive;
        private Vector2 _stepTarget;

        // 紧急区降速配置（从 GameConfig 读，缓存）
        private static bool _cfgLoaded;
        private static int _emergencyZoneCells = 3;
        private static float _emergencyScale = 0.5f;

        // sortingOrder 防抖
        private SpriteRenderer _sr;
        private float _lastSortY;

        // 命中阈值（到达 waypoint 判定）
        private const float WAYPOINT_REACH_DIST = 0.05f;

        // Step 11 性能优化：屏外 culling。
        //   - 不停"位移"（怪屏外仍向营帐走 — 否则永远不到营帐 = 卡死）
        //   - 但暂停 SpriteAnimator 帧切（OnBecameInvisible hook 到 SpriteAnimator.OnHitStopBegin）
        //   - sortingOrder 仅 Y 变才重算（GridSortingService.UpdateIfChanged 已防抖）
        public bool IsOnScreen { get; private set; } = true;
        private SpriteAnimator _animator;

        // 头顶血条（2026-06-03）：BattleBridge.Battle_SpawnEnemyAtRow 构建并 BindHpBar 注入。
        //   fill 用 localScale.x 表示 hp 比例（与 UnitView 同款，左 pivot → 从左缘缩放）。
        //   2026-07-01：怪物运行图高度与武将同步，血条改用固定画布底部锚点偏移，不再按每帧可见 bounds 重排。
        private Transform _hpBar;
        private Transform _hpBarFill;
        private float _hpFillMaxScaleX = 1f;
        private bool _hpBarPlaced;

        private void Awake()
        {
            _sr = GetComponentInChildren<SpriteRenderer>();
            _animator = GetComponent<SpriteAnimator>();
            EnsureConfig();
        }

        // OnBecameInvisible / OnBecameVisible 由 Unity 主动调（需要场上有 Renderer + 有 Camera）。
        // 屏外时暂停帧切（复用 SpriteAnimator Hit-Stop hook），但位移/到达判定继续跑。
        private void OnBecameInvisible()
        {
            IsOnScreen = false;
            if (_animator != null) _animator.OnHitStopBegin();
        }
        private void OnBecameVisible()
        {
            IsOnScreen = true;
            if (_animator != null) _animator.OnHitStopEnd();
        }

        /// <summary>由 BattleBridge.SpawnEnemy 调用。waypoints = 该 lane 的全部世界坐标点。</summary>
        public void Init(long handle, float speed, List<Vector2> waypoints, Vector2 spawnPos)
        {
            Handle = handle;
            Speed = speed;
            _waypoints.Clear();
            if (waypoints != null) _waypoints.AddRange(waypoints);
            _wpIndex = 0;
            // 玩法重构 P2a：通关怪可在网格内生成（spawn_mode=last_col/right_random）。怪朝左基地推进(X 递减)，
            //   跳过出生点右侧(X>=spawnX)的前导 waypoint，避免先回头向右再左折。始终保留末点(基地)。
            while (_wpIndex < _waypoints.Count - 1 &&
                   _waypoints[_wpIndex].x >= spawnPos.x - WAYPOINT_REACH_DIST)
            {
                _wpIndex++;
            }
            _arrived = false;
            transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, spawnPos.y);
        }

        public void SetWaypoints(List<Vector2> waypoints)
        {
            _waypoints.Clear();
            if (waypoints != null) _waypoints.AddRange(waypoints);
            _wpIndex = 0;
        }

        /// <summary>R6: 切网格步进模式（清 lane waypoints，怪不再沿 lane 自走）。spawn 后由 Lua 立即调。</summary>
        public void EnterGridMode()
        {
            _gridMode = true;
            _stepActive = false;
            _waypoints.Clear();
        }

        /// <summary>R6: 步进到一个目标点（Lua greedy 选格后调，每次一格）。</summary>
        public void StepTo(Vector2 target)
        {
            if (!_gridMode) EnterGridMode();
            _stepTarget = target;
            _stepActive = true;
        }

        /// <summary>网格步进中被撞停/接战时调用：停在当前 transform 位置，权威 cell 由 Lua 反查后同步。</summary>
        public void StopStep()
        {
            _stepActive = false;
        }

        // ============ 头顶血条（2026-06-03） ============

        /// <summary>BattleBridge spawn 后注入头顶血条（hp_bar 下含 fill 子节点）。</summary>
        public void BindHpBar(GameObject hpBar)
        {
            if (hpBar == null) return;
            _hpBar = hpBar.transform;
            var fill = _hpBar.Find("fill");
            if (fill != null)
            {
                _hpBarFill = fill;
                _hpFillMaxScaleX = fill.localScale.x;
            }
            _hpBarPlaced = false;   // 等 sprite 就绪后在 Update 里按实际高度重定位
        }

        /// <summary>hp 比例 0..1（Lua Enemy_TakeDamage → Battle_SetEnemyHpBar 调）。</summary>
        public void SetHp(float pct)
        {
            if (_hpBarFill == null) return;
            pct = Mathf.Clamp01(pct);
            var s = _hpBarFill.localScale;
            s.x = _hpFillMaxScaleX * pct;
            _hpBarFill.localScale = s;
        }

        public void RefreshHpBarLayout()
        {
            _hpBarPlaced = false;
            TryPlaceHpBar();
        }

        /// <summary>sprite 就绪后按固定画布锚点刷新血条尺寸/层级。</summary>
        private void TryPlaceHpBar()
        {
            if (_hpBarPlaced || _hpBar == null || _sr == null || _sr.sprite == null) return;
            _hpFillMaxScaleX = BattleBridge.LayoutHpBar(_hpBar, _sr);
            _hpBarPlaced = true;
        }

        private static void EnsureConfig()
        {
            if (_cfgLoaded) return;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var rowZone = cm.GetTableInfo("GameConfig", "key", "speed_emergency_zone_cells");
                    if (rowZone != null) _emergencyZoneCells = cm.GetValue<int>(rowZone, "value", 3);
                    var rowScale = cm.GetTableInfo("GameConfig", "key", "speed_emergency_scale");
                    if (rowScale != null) _emergencyScale = cm.GetValue<float>(rowScale, "value", 0.5f);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[EnemyMover] 读 GameConfig 失败，沿用默认值: {e.Message}");
            }
            _cfgLoaded = true;
        }

        private void Update()
        {
            if (!_hpBarPlaced) TryPlaceHpBar();     // 头顶血条一次性定位（sprite 就绪后；与暂停/到达无关）
            if (BattleBridge.BattlePaused) return;  // 业务暂停 flag — 比 Time.timeScale 干净
            if (Halted) return;                      // 攻击中 → 停止位移

            // R6 网格步进模式：单格 MoveTowards（紧急区降速沿用），到格回调 Lua 再等下一步指令
            if (_gridMode)
            {
                if (!_stepActive) return;
                var gCur = (Vector2)transform.position;
                int gCol = GridMap.WorldToCellCol(gCur.x);
                float gSpeed = (gCol <= _emergencyZoneCells) ? Speed * _emergencyScale : Speed;
                var gNext = Vector2.MoveTowards(gCur, _stepTarget, gSpeed * Time.deltaTime);
                transform.position = new Vector3(gNext.x, gNext.y, 0f);
                if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, gNext.y);
                if (Vector2.Distance(gNext, _stepTarget) < WAYPOINT_REACH_DIST)
                {
                    _stepActive = false;
                    CallLuaStepDone(Handle);
                }
                return;
            }

            if (_arrived) return;
            if (_waypoints.Count == 0) return;
            if (_wpIndex >= _waypoints.Count) return;

            // 屏外 culling（Step 11 性能优化）— 这里 EnemyMover 仍移动（让怪不在屏内继续走），
            // 但若需 hard culling，可加 UnitView IsOnScreen 判断后跳过。
            // MVP 阶段不跳，避免怪卡死。

            var target = _waypoints[_wpIndex];
            var curPos = (Vector2)transform.position;

            // 紧急区降速：当前 col ≤ _emergencyZoneCells（靠近左侧营帐）
            int curCol = GridMap.WorldToCellCol(curPos.x);
            float effSpeed = (curCol <= _emergencyZoneCells)
                ? Speed * _emergencyScale
                : Speed;

            var nextPos = Vector2.MoveTowards(curPos, target, effSpeed * Time.deltaTime);
            transform.position = new Vector3(nextPos.x, nextPos.y, 0f);

            // sortingOrder 更新（Y 变才重算）
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, nextPos.y);

            if (Vector2.Distance(nextPos, target) < WAYPOINT_REACH_DIST)
            {
                _wpIndex++;
                if (_wpIndex >= _waypoints.Count)
                {
                    OnArrived();
                }
            }
        }

        private void OnArrived()
        {
            if (_arrived) return;
            _arrived = true;
            // 调 Lua 全局：Battle_OnEnemyReachCamp(handle, atk)
            CallLuaOnReachCamp(Handle, Atk);
        }

        private static void CallLuaOnReachCamp(long handle, int atk)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Battle_OnEnemyReachCamp");
                if (fn != null)
                {
                    fn.Call(handle, atk);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EnemyMover] CallLua Battle_OnEnemyReachCamp 失败: {e.Message}");
            }
#endif
        }

        // R6: 步进到格回调（Lua 落格后更新权威坐标 + 下个 AI tick 再决策）
        private static void CallLuaStepDone(long handle)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Enemy_OnStepDone");
                if (fn != null)
                {
                    fn.Call(handle);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[EnemyMover] CallLua Enemy_OnStepDone 失败: {e.Message}");
            }
#endif
        }
    }
}
