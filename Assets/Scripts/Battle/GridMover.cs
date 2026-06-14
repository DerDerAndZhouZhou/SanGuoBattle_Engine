using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 通用网格走路器（R2 统一位移器第一步，2026-06-11）。
    ///
    /// 设计（structure-redesign 块2.2 + tech-research 阶段2）：
    ///   - 玩家场上单位拖拽移动 = 沿 cell 路径逐格走（速度 GameConfig.unit_move_speed），非瞬移
    ///   - C# 负责每帧位移（性能热路径），Lua 负责路径计算 / 占格 / 到达后业务
    ///   - 到达终点 → 调 Lua 全局 Unit_OnWalkArrived(handle)（镜像 EnemyMover→Battle_OnEnemyReachCamp 模式）
    ///   - EnemyMover（怪专属：紧急区降速/到达打基地）后续并入本类（R2 收尾），先不动避免怪移动回归
    ///
    /// 0 SerializeField — waypoints/handle 由 BattleBridge.Battle_UnitWalkPath 注入，速度读 GameConfig。
    /// </summary>
    public class GridMover : MonoBehaviour
    {
        public long Handle { get; private set; }
        public bool Active { get; private set; }

        private readonly List<Vector2> _waypoints = new List<Vector2>();
        private int _wpIndex;
        private float _speed = 2.5f;

        // 四方向移动（2026-06-11 用户需求）：按当前 waypoint 段主轴判定朝向，变化时回调 Lua
        // Unit_OnWalkSegment(handle, dir) 切 walk/walk_up/walk_down + 左右镜像。0=右 1=左 2=上 3=下。
        private int _curDir = -1;

        private SpriteRenderer _sr;
        private float _lastSortY;

        private const float WAYPOINT_REACH_DIST = 0.05f;

        // 速度配置（GameConfig.unit_move_speed，缓存）
        private static bool _cfgLoaded;
        private static float _cfgSpeed = 2.5f;

        private static float ConfigSpeed()
        {
            if (_cfgLoaded) return _cfgSpeed;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var row = cm.GetTableInfo("GameConfig", "key", "unit_move_speed");
                    if (row != null) _cfgSpeed = cm.GetValue<float>(row, "value", 2.5f);
                }
                // 审查 F：speed<=0 = 永不到达(moving 卡死)/负值 MoveTowards 反向飞出屏——WPS 改表写坏是现实场景(§10)
                if (_cfgSpeed <= 0f)
                {
                    Debug.LogWarning($"[GridMover] unit_move_speed={_cfgSpeed} 非法(<=0)，回退 2.5");
                    _cfgSpeed = 2.5f;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[GridMover] 读 GameConfig.unit_move_speed 失败，沿用默认 2.5: {e.Message}");
            }
            _cfgLoaded = true;
            return _cfgSpeed;
        }

        /// <summary>开始沿 waypoints 走（世界坐标，不含当前位置；从 transform 当前位置出发）。
        /// v2 批 1b（2026-06-14）方案A：speed>0 时逐单位移速（npc.tab.move_speed，Lua 透传）；
        /// speed<=0 回退 ConfigSpeed()（GameConfig.unit_move_speed 全局兜底，批4 删）。审查 F 的 <=0 clamp 沿用 ConfigSpeed。</summary>
        public void BeginPath(long handle, List<Vector2> waypoints, float speed = 0f)
        {
            Handle = handle;
            _waypoints.Clear();
            if (waypoints != null) _waypoints.AddRange(waypoints);
            _wpIndex = 0;
            _speed = speed > 0f ? speed : ConfigSpeed();
            _curDir = -1;
            Active = _waypoints.Count > 0;
            if (_sr == null) _sr = GetComponentInChildren<SpriteRenderer>();
            if (!Active) { NotifyArrived(); return; }   // 空路径 → 立即视为到达（调用方兜底）
            UpdateSegmentDir((Vector2)transform.position, _waypoints[0]);   // 首段方向（同步，调用方已置 moving）
        }

        /// <summary>外部强停（如单位被收回/出售时复用对象）。不触发到达回调。</summary>
        public void Stop()
        {
            Active = false;
            _waypoints.Clear();
        }

        private void Update()
        {
            if (!Active) return;
            if (BattleBridge.BattlePaused) return;   // 业务暂停（timeScale=0 时 deltaTime 也为 0，双保险）
            if (_wpIndex >= _waypoints.Count) { Finish(); return; }

            var target = _waypoints[_wpIndex];
            var curPos = (Vector2)transform.position;
            var nextPos = Vector2.MoveTowards(curPos, target, _speed * Time.deltaTime);
            transform.position = new Vector3(nextPos.x, nextPos.y, 0f);

            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, nextPos.y);

            if (Vector2.Distance(nextPos, target) < WAYPOINT_REACH_DIST)
            {
                NotifyStep(target);   // 2026-06-14 到达本格 → 回调 Lua 更新"当前格"(战斗按真实格索敌 / 拖拽中刷新路径预览)
                _wpIndex++;
                if (_wpIndex >= _waypoints.Count) Finish();
                else UpdateSegmentDir(target, _waypoints[_wpIndex]);   // 拐弯 → 段方向重判
            }
        }

        /// <summary>段主轴判定（|dx|≥|dy| 横向，否则纵向；世界 Y 向上 = 屏幕上 = 行号减小）。
        /// 方向变化才回调 Lua（直线多 waypoint 不重复调）。</summary>
        private void UpdateSegmentDir(Vector2 from, Vector2 to)
        {
            var d = to - from;
            if (d.sqrMagnitude < 1e-6f) return;   // 重叠点（如起点=首 waypoint）→ 沿用当前方向
            int dir = Mathf.Abs(d.x) >= Mathf.Abs(d.y) ? (d.x >= 0f ? 0 : 1) : (d.y > 0f ? 2 : 3);
            if (dir == _curDir) return;
            _curDir = dir;
            NotifySegment(dir);
        }

        // 2026-06-14 用户：每到一格回调 Lua Unit_OnWalkStep(handle, r, c)——更新"当前格"(战斗按真实格索敌,
        // 修"走路时按 walk 目标格被攻击") + 拖拽中卡片走到新格刷新路径预览。pos=刚到达的 waypoint(cell 中心)。
        private void NotifyStep(Vector2 pos)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Unit_OnWalkStep");
                if (fn != null)
                {
                    var cell = GridMap.WorldToCell(pos);
                    fn.Call(Handle, cell.row, cell.col);
                    fn.Dispose();
                }
            }
            catch { /* 段回调非致命,静默 */ }
#endif
        }

        private void NotifySegment(int dir)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Unit_OnWalkSegment");
                if (fn != null)
                {
                    fn.Call(Handle, dir);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                // 段方向回调失败不致命（动画沿用上一段），不打 Error 干扰排障
                Debug.LogWarning($"[GridMover] CallLua Unit_OnWalkSegment 失败: {e.Message}");
            }
#endif
        }

        private void Finish()
        {
            Active = false;
            NotifyArrived();
        }

        private void NotifyArrived()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Unit_OnWalkArrived");
                if (fn != null)
                {
                    fn.Call(Handle);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GridMover] CallLua Unit_OnWalkArrived 失败: {e.Message}");
            }
#endif
        }
    }
}
