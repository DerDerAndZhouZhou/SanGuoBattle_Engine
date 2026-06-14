using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Engine.Host;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 投射物每帧位移 + 命中判定（性能热路径）。
    ///
    /// P1.6 (2026-05-26) 扩展：3 种模式（按 Docs/skill-system-architecture.md §5）
    ///   - TrackUnit:    追单位 transform（原有行为）；目标死亡 → 自动切到 FlyToPoint
    ///   - FlyToPoint:   飞固定 LandingPos
    ///   - Line:         沿 LineDir 飞 LineDist；途中每个 ≤ LineWidth/2 距离的敌人触发命中（_lineHitSet 避重）
    ///
    /// 设计原则：
    ///   - C# 跑数学（位移 + 距离判定），Lua 跑业务（伤害/克制/特效）
    ///   - 命中 → 调 Lua 全局 Battle_OnProjectileHit(projHandle, targetHandle)
    ///   - 目标销毁后 transform 为 null → TrackUnit 切 FlyToPoint(最后位置)，其他模式静默销毁
    ///   - 池化由 BattleBridge 管理；本组件被回收时 Reset()
    /// </summary>
    public class ProjectileTicker : MonoBehaviour
    {
        public enum TrackModeKind
        {
            TrackUnit = 0,
            FlyToPoint = 1,
            Line = 2,
        }

        public long Handle { get; set; }
        public long TargetHandle { get; set; }
        public float Speed { get; set; } = 8f;
        public float HitThreshold { get; set; } = 0.2f;
        public float MaxLifeSec { get; set; } = 5f;

        // P1.6 新增
        public TrackModeKind TrackMode { get; set; } = TrackModeKind.TrackUnit;
        public Vector2 LandingPos { get; set; }
        public Vector2 LineDir { get; set; }
        public float LineDist { get; set; }
        public float LineWidth { get; set; }
        // R5 (2026-06-11) 连弩 D1：第一个目标挡住即停（命中即回收）；
        // 飞满 LineDist 未被阻挡 → 回调 targetHandle=0（直达基地的结算由 Lua stash 决定）
        public bool StopOnFirstHit { get; set; }
        private bool _lineAnyHit;
        private Vector2 _lineStartPos;
        private readonly HashSet<long> _lineHitSet = new HashSet<long>();

        // 目标 Transform 持有（销毁则自销 / TrackUnit 模式才用）
        public Transform Target { get; set; }

        // Step 11 池化：BattleBridge.ProjectilePool 上限 30。命中/超时/目标丢失 → 不 Destroy，
        // 而是调 BattleBridge.RecycleProjectile(handle) 回池（gameObject.SetActive(false)）。
        // OnRecycled 由 BattleBridge 设回 true 表示已交由池管理。
        public bool PooledRecycled { get; set; }

        private float _aliveTime;
        private bool _hit;

        // sortingOrder 防抖
        private SpriteRenderer _sr;
        private float _lastSortY;

        private void Awake()
        {
            _sr = GetComponentInChildren<SpriteRenderer>();
        }

        /// <summary>由 BattleBridge.SpawnProjectile 调用（TrackUnit 模式）。target 为 null → 投射物直接消失（避免脏数据）。</summary>
        public void Init(long handle, long targetHandle, Transform target, Vector2 spawnPos, float speed, float hitThreshold = 0.2f)
        {
            Handle = handle;
            TargetHandle = targetHandle;
            Target = target;
            Speed = speed;
            HitThreshold = hitThreshold;
            TrackMode = TrackModeKind.TrackUnit;
            transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
            _aliveTime = 0f;
            _hit = false;
            _lineHitSet.Clear();
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, spawnPos.y);
        }

        /// <summary>P1.6: 飞向固定落点（targetHandle=0 表示无单位目标）。</summary>
        public void InitFlyToPoint(long handle, Vector2 spawnPos, Vector2 landingPos, float speed, float hitThreshold = 0.2f)
        {
            Handle = handle;
            TargetHandle = 0;
            Target = null;
            Speed = speed;
            HitThreshold = hitThreshold;
            TrackMode = TrackModeKind.FlyToPoint;
            LandingPos = landingPos;
            transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
            _aliveTime = 0f;
            _hit = false;
            _lineHitSet.Clear();
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, spawnPos.y);
        }

        /// <summary>P1.6: 通道穿越投掷物。沿 dir 方向飞 dist 距离，width 半径内的所有敌人触发命中。
        /// R5: stopOnFirstHit=true（连弩）→ 第一个敌人命中即回收；飞满未被阻挡回调 targetHandle=0。</summary>
        public void InitLine(long handle, Vector2 spawnPos, Vector2 dir, float dist, float width, float speed, bool stopOnFirstHit = false)
        {
            Handle = handle;
            TargetHandle = 0;
            Target = null;
            Speed = speed;
            HitThreshold = 0f; // Line 模式不用
            TrackMode = TrackModeKind.Line;
            LineDir = dir.normalized;
            LineDist = dist;
            LineWidth = width;
            StopOnFirstHit = stopOnFirstHit;
            _lineAnyHit = false;
            _lineStartPos = spawnPos;
            transform.position = new Vector3(spawnPos.x, spawnPos.y, 0f);
            _aliveTime = 0f;
            _hit = false;
            _lineHitSet.Clear();
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, spawnPos.y);
        }

        public void Reset()
        {
            Handle = 0;
            TargetHandle = 0;
            Target = null;
            TrackMode = TrackModeKind.TrackUnit;
            LandingPos = Vector2.zero;
            LineDir = Vector2.zero;
            LineDist = 0f;
            LineWidth = 0f;
            StopOnFirstHit = false;
            _lineAnyHit = false;
            _aliveTime = 0f;
            _hit = false;
            _lineHitSet.Clear();
            // v2 批 1b（2026-06-14）C#⑤ #3：回池复位 rotation（FaceDir 会把投射物转向，复用时若不复位 → 残留上次角度）。
            transform.rotation = Quaternion.identity;
        }

        private void Update()
        {
            if (BattleBridge.BattlePaused) return;
            if (_hit) return;
            if (PooledRecycled) return;

            _aliveTime += Time.deltaTime;
            if (_aliveTime > MaxLifeSec)
            {
                BattleBridge.RecycleProjectile(Handle);
                return;
            }

            // P1.6 — TrackUnit: 目标死亡 → 切 FlyToPoint(Target 最后位置)
            if (TrackMode == TrackModeKind.TrackUnit && Target == null)
            {
                TrackMode = TrackModeKind.FlyToPoint;
                // LandingPos 未设 → 用当前位置 = 立即命中（避免飞出场景）
                if (LandingPos == Vector2.zero) LandingPos = (Vector2)transform.position;
            }

            switch (TrackMode)
            {
                case TrackModeKind.TrackUnit: UpdateTrackUnit(); break;
                case TrackModeKind.FlyToPoint: UpdateFlyToPoint(); break;
                case TrackModeKind.Line: UpdateLine(); break;
            }
        }

        private void UpdateTrackUnit()
        {
            if (Target == null) { BattleBridge.RecycleProjectile(Handle); return; }
            var curPos = (Vector2)transform.position;
            var tgtPos = (Vector2)Target.position;
            // 记录最后位置（万一下一帧 Target 被销毁可作 LandingPos）
            LandingPos = tgtPos;

            var nextPos = Vector2.MoveTowards(curPos, tgtPos, Speed * Time.deltaTime);
            transform.position = new Vector3(nextPos.x, nextPos.y, 0f);
            FaceDir(tgtPos - curPos);
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, nextPos.y);

            if (Vector2.Distance(nextPos, tgtPos) < HitThreshold)
            {
                _hit = true;
                CallLuaOnHit(Handle, TargetHandle);
                BattleBridge.RecycleProjectile(Handle);
            }
        }

        private void UpdateFlyToPoint()
        {
            var curPos = (Vector2)transform.position;
            var nextPos = Vector2.MoveTowards(curPos, LandingPos, Speed * Time.deltaTime);
            transform.position = new Vector3(nextPos.x, nextPos.y, 0f);
            FaceDir(LandingPos - curPos);
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, nextPos.y);

            if (Vector2.Distance(nextPos, LandingPos) < (HitThreshold > 0 ? HitThreshold : 0.1f))
            {
                _hit = true;
                CallLuaOnHit(Handle, 0); // targetHandle=0 表示落点命中（无单位目标）
                BattleBridge.RecycleProjectile(Handle);
            }
        }

        private void UpdateLine()
        {
            var curPos = (Vector2)transform.position;
            var nextPos = curPos + LineDir * Speed * Time.deltaTime;
            transform.position = new Vector3(nextPos.x, nextPos.y, 0f);
            FaceDir(LineDir);
            if (_sr != null) GridSortingService.UpdateIfChanged(_sr, ref _lastSortY, nextPos.y);

            // 飞够距离 → 回池
            if (Vector2.Distance(_lineStartPos, nextPos) >= LineDist)
            {
                _hit = true;
                // R5: 连弩飞满全程未被阻挡 → 通知 Lua（targetHandle=0；是否结算基地由 Lua stash 决定）
                if (StopOnFirstHit && !_lineAnyHit) CallLuaOnHit(Handle, 0);
                BattleBridge.RecycleProjectile(Handle);
                return;
            }

            // 扫描通道内未命中过的敌人（半径 = LineWidth/2 of 当前位置）
            float halfWidth = LineWidth * 0.5f;
            if (halfWidth <= 0f) return;
            foreach (var kv in BattleBridge.EnumerateEnemies())
            {
                long eh = kv.Key;
                if (_lineHitSet.Contains(eh)) continue;
                var et = kv.Value;
                if (et == null) continue;
                var ePos = (Vector2)et.transform.position;
                if (Vector2.Distance(ePos, nextPos) <= halfWidth)
                {
                    _lineHitSet.Add(eh);
                    _lineAnyHit = true;
                    CallLuaOnHit(Handle, eh);
                    // R5 D1: 第一个目标挡住即停（必须立即 return —— 命中结算可能杀怪改 _enemies，
                    // 继续 foreach 会 InvalidOperationException）
                    if (StopOnFirstHit)
                    {
                        _hit = true;
                        BattleBridge.RecycleProjectile(Handle);
                        return;
                    }
                }
            }
        }

        private void FaceDir(Vector2 dir)
        {
            if (dir.sqrMagnitude > 0.0001f)
            {
                float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
                transform.rotation = Quaternion.Euler(0f, 0f, angle);
            }
        }

        // v2 批 1b（2026-06-14）顺手项：缓存 Battle_OnProjectileHit LuaFunction（热路径：每发投射物命中都调）。
        // 首次取后复用，省每次 Get<LuaFunction>+Dispose 的分配。LuaHost.Shutdown 调 ResetLuaCache() 释放并置空
        // （env 重建后旧 LuaFunction 失效，必须丢弃）。
#if XLUA
        private static XLua.LuaFunction _onHitFn;

        private static void CallLuaOnHit(long projHandle, long targetHandle)
        {
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                if (_onHitFn == null) _onHitFn = env.Global.Get<XLua.LuaFunction>("Battle_OnProjectileHit");
                if (_onHitFn != null) _onHitFn.Call(projHandle, targetHandle);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ProjectileTicker] CallLua Battle_OnProjectileHit 失败: {e.Message}");
            }
        }

        /// <summary>LuaHost.Shutdown 调用：释放缓存的 LuaFunction（env 重建后旧引用失效）。</summary>
        public static void ResetLuaCache()
        {
            if (_onHitFn != null) { try { _onHitFn.Dispose(); } catch { /* env 可能已 dispose */ } _onHitFn = null; }
        }
#else
        private static void CallLuaOnHit(long projHandle, long targetHandle) { }
        public static void ResetLuaCache() { }
#endif
    }
}
