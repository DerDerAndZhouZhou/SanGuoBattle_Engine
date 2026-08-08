using System;
using System.Collections.Generic;
using HeroDefense.Config;
using HeroDefense.Utils;
using UnityEngine;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 一个逻辑队伍根节点上的纯视觉兵模控制器。
    ///
    /// 不创建 Battle handle、Collider、UnitView、GridMover 或 Lua 回调。兵模、残影和
    /// 假投射物都只跟随已存在的英雄根节点，任何消失、命中或动画事件均不参与 Match 结算。
    /// </summary>
    public sealed class SquadVisualController : MonoBehaviour
    {
        private const int DefaultReflowMs = 150;
        private const int DefaultRefillFadeMs = 150;
        private const int DefaultDeathHoldMs = 960;
        private const int DefaultAttackDurationMs = 800;
        private const int DefaultAttackHitBp = 5000;
        private const int DefaultProjectileSpeedMilli = 6000;
        private const int MaxVisualModels = 5;

        private sealed class FormationSlot
        {
            public int SlotIndex;
            public int ForwardMilli;
            public int LateralMilli;
            public int SortingBias;
        }

        private sealed class VisualModel
        {
            public int StableSlot;
            public int FormationIndex;
            public GameObject Container;
            public Transform SpriteRoot;
            public SpriteRenderer Renderer;
            public SpriteAnimator Animator;
            public Vector3 MoveStart;
            public Vector3 MoveTarget;
            public float MoveStartTime;
            public float MoveDuration;
            public float FadeStartAlpha;
            public float FadeTargetAlpha;
            public float FadeStartTime;
            public float FadeDuration;
            public float DestroyAt;
            public bool IsGhost;
        }

        private sealed class PendingShot
        {
            public float DueTime;
            public Transform Target;
            public string ProjectileKey;
        }

        private sealed class VisualProjectile
        {
            public GameObject GameObject;
            public SpriteRenderer Renderer;
            public Transform Target;
            public float Speed;
        }

        private static readonly Dictionary<int, FormationSlot[]> FormationByCount =
            new Dictionary<int, FormationSlot[]>();
        private static bool _formationLoaded;
        private static bool _formationValid;
        private static bool _formationErrorLogged;

        private readonly List<VisualModel> _models = new List<VisualModel>();
        private readonly List<VisualModel> _ghosts = new List<VisualModel>();
        private readonly List<PendingShot> _pendingShots = new List<PendingShot>();
        private readonly List<VisualProjectile> _visualProjectiles =
            new List<VisualProjectile>();
        private readonly HashSet<int> _playedPulseIds = new HashSet<int>();

        private UnitView _rootView;
        private SpriteRenderer _heroRenderer;
        private SpriteAnimator _heroAnimator;
        private string _troopBaseKey;
        private string _troopAnimType;
        private int _team;
        private bool _ranged;
        private bool _bound;
        private bool _heroDetached;
        private string _facing = "down";
        private string _stateName = "idle_down";
        private int _defaultReflowMs = DefaultReflowMs;
        private int _defaultRefillFadeMs = DefaultRefillFadeMs;
        private int _defaultDeathHoldMs = DefaultDeathHoldMs;
        private int _attackDurationFallbackMs = DefaultAttackDurationMs;
        private int _attackHitFallbackBp = DefaultAttackHitBp;
        private float _visualProjectileSpeed = DefaultProjectileSpeedMilli / 1000f;

        /// <summary>配置兵模 NPC 和阵营，但不预生成任何兵模。</summary>
        public bool Bind(int troopNpcId, int team)
        {
            if (team != 0 && team != 1)
            {
                Debug.LogWarning($"[SquadVisualController] Bind team 非法: {team}");
                return false;
            }
            if (!TryLoadFormation()) return false;
            if (!BattleBridge.TryGetVisualNpcInfo(
                    troopNpcId,
                    out string troopBaseKey,
                    out string troopAnimType))
            {
                Debug.LogWarning(
                    $"[SquadVisualController] Bind 找不到兵模 NPC 表现数据: {troopNpcId}");
                return false;
            }

            Clear();
            _rootView = GetComponent<UnitView>();
            if (_rootView == null)
            {
                Debug.LogWarning("[SquadVisualController] Bind 必须挂在 UnitView 根节点");
                return false;
            }

            _heroRenderer = _rootView.Sr != null
                ? _rootView.Sr
                : GetComponentInChildren<SpriteRenderer>();
            _heroAnimator = GetComponent<SpriteAnimator>();
            _troopBaseKey = troopBaseKey;
            _troopAnimType = troopAnimType;
            _team = team;
            _ranged = ResolveTroopIsRanged(troopNpcId);
            LoadPresentationConfig();
            _facing = team == 0 ? "right" : "left";
            _stateName = "idle_down";
            _bound = true;
            return true;
        }

        /// <summary>补足或直接收缩兵模，适用于出生与非战损状态同步。</summary>
        public bool SetFormation(int visualCount, string facing, int reflowMs)
        {
            if (!IsBound() || !TryNormalizeVisualCount(visualCount)) return false;
            if (!TryNormalizeFacing(facing, out string normalizedFacing)) return false;

            _facing = normalizedFacing;
            if (visualCount < _models.Count)
            {
                _models.Sort(CompareStableSlot);
                while (_models.Count > visualCount)
                {
                    int index = _models.Count - 1;
                    DestroyModel(_models[index]);
                    _models.RemoveAt(index);
                }
            }
            else
            {
                while (_models.Count < visualCount)
                    _models.Add(CreateModel(FindNextStableSlot()));
            }

            ReassignFormationIndices();
            ReflowModels(reflowMs);
            return true;
        }

        /// <summary>让所有当前兵模播放同一正式状态，不影响任何 Match 数据。</summary>
        public bool PlayState(string stateName, float speedMult)
        {
            if (!IsBound() || string.IsNullOrEmpty(stateName)) return false;
            string normalizedState = BattleBridge.NormalizeVisualActionState(stateName);
            if (string.IsNullOrEmpty(normalizedState)) return false;

            bool facingChanged = TryInferFacing(normalizedState, _facing, out string inferredFacing)
                && inferredFacing != _facing;
            _facing = inferredFacing;
            _stateName = normalizedState;
            if (IsAttackVisualCancelledBy(normalizedState)) ClearAttackVisuals();
            if (facingChanged) ReflowModels(_defaultReflowMs);

            bool faceRight = IsFacingRight(_facing);
            bool any = false;
            for (int i = 0; i < _models.Count; i++)
            {
                var model = _models[i];
                if (model == null || model.Animator == null) continue;
                any |= BattleBridge.PlayVisualAnimation(
                    model.Animator,
                    model.Renderer,
                    normalizedState,
                    speedMult,
                    faceRight);
            }
            return any || _models.Count == 0;
        }

        /// <summary>
        /// 兵种假攻击。重复 pulseId 幂等；动画、发射和命中均不会触发 Lua 或伤害。
        /// </summary>
        public bool PlayAttack(
            string stateName,
            float speedMult,
            long targetHandle,
            string projectileKey,
            int pulseId)
        {
            if (!IsBound() || pulseId <= 0 || string.IsNullOrEmpty(stateName)) return false;
            if (_playedPulseIds.Contains(pulseId)) return true;

            string normalizedState = BattleBridge.NormalizeVisualActionState(stateName);
            if (normalizedState != "attack" && normalizedState != "attack_left") return false;
            _playedPulseIds.Add(pulseId);

            string attackFacing = normalizedState == "attack_left" ? "left" : "right";
            _facing = attackFacing;
            _stateName = normalizedState;
            ReflowModels(_defaultReflowMs);

            string combatRest = attackFacing == "left"
                ? "combat_idle_left"
                : "combat_idle";
            bool faceRight = attackFacing == "right";
            bool any = false;
            for (int i = 0; i < _models.Count; i++)
            {
                var model = _models[i];
                if (model == null || model.Animator == null) continue;
                // 先写 rest，再播 one-shot，SpriteAnimator 才能在末帧可靠回到正确战斗 idle。
                BattleBridge.PlayVisualAnimation(
                    model.Animator,
                    model.Renderer,
                    combatRest,
                    speedMult,
                    faceRight);
                any |= BattleBridge.PlayVisualAnimation(
                    model.Animator,
                    model.Renderer,
                    normalizedState,
                    speedMult,
                    faceRight);
            }

            if (_ranged
                && _models.Count > 0
                && BattleBridge.TryGetUnitVisualTransform(targetHandle, out Transform target))
            {
                int durationMs = BattleBridge.Battle_GetAnimStateDurationMs(
                    _troopBaseKey,
                    normalizedState,
                    _attackDurationFallbackMs);
                int hitBp = BattleBridge.Battle_GetAnimEventRatioBp(
                    _troopBaseKey,
                    normalizedState,
                    "hit",
                    _attackHitFallbackBp);
                float normalizedSpeed = NormalizeSpeed(speedMult);
                float delay = Mathf.Max(
                    0f,
                    durationMs / 1000f / normalizedSpeed * hitBp / 10000f);
                _pendingShots.Add(new PendingShot
                {
                    DueTime = Time.unscaledTime + delay,
                    Target = target,
                    ProjectileKey = projectileKey ?? string.Empty,
                });
            }
            return any || _models.Count == 0;
        }

        /// <summary>按伤害来源优先挑选外露兵模，将其脱离根节点为死亡残影。</summary>
        public bool ApplyCasualties(
            int newVisualCount,
            int deathVisualCount,
            string sourceDirection,
            string deathState,
            int reflowMs,
            int deathHoldMs)
        {
            if (!IsBound()
                || !TryNormalizeVisualCount(newVisualCount)
                || deathVisualCount < 0
                || newVisualCount > _models.Count)
                return false;

            int removable = _models.Count - newVisualCount;
            if (deathVisualCount != removable) return false;

            string normalizedDeath = BattleBridge.NormalizeVisualActionState(deathState);
            if (normalizedDeath != "die" && normalizedDeath != "die_left") return false;

            var victims = SelectCasualties(deathVisualCount, sourceDirection);
            for (int i = 0; i < victims.Count; i++)
            {
                var victim = victims[i];
                _models.Remove(victim);
                MakeTroopGhost(victim, normalizedDeath, deathHoldMs);
            }

            // 可见人数减少但未跨档时也允许同步；其余节点立即开始补位，不等待残影结束。
            ReassignFormationIndices();
            ReflowModels(reflowMs);
            return true;
        }

        /// <summary>补兵只添加新的视觉模型并淡入，不会倒放死亡动画。</summary>
        public bool Refill(int newVisualCount, int fadeMs, string stateName)
        {
            if (!IsBound()
                || !TryNormalizeVisualCount(newVisualCount)
                || newVisualCount < _models.Count
                || string.IsNullOrEmpty(stateName))
                return false;

            string normalizedState = BattleBridge.NormalizeVisualActionState(stateName);
            if (string.IsNullOrEmpty(normalizedState)) return false;
            int effectiveFadeMs = fadeMs >= 0 ? fadeMs : _defaultRefillFadeMs;

            int startCount = _models.Count;
            while (_models.Count < newVisualCount)
            {
                var model = CreateModel(FindNextStableSlot());
                SetModelAlpha(model, 0f);
                BeginFade(model, 0f, 1f, effectiveFadeMs);
                _models.Add(model);
            }

            _stateName = normalizedState;
            TryInferFacing(normalizedState, _facing, out _facing);
            bool faceRight = IsFacingRight(_facing);
            for (int i = startCount; i < _models.Count; i++)
            {
                var model = _models[i];
                BattleBridge.PlayVisualAnimation(
                    model.Animator,
                    model.Renderer,
                    normalizedState,
                    1f,
                    faceRight);
            }

            ReassignFormationIndices();
            ReflowModels(_defaultReflowMs);
            return true;
        }

        /// <summary>复制英雄当前图为无交互尸体，根节点继续承载可撤回的兵模。</summary>
        public bool DetachHeroAndRetreat(string deathState, int deathHoldMs)
        {
            if (!IsBound()) return false;
            if (_heroDetached) return true;

            string normalizedDeath = BattleBridge.NormalizeVisualActionState(deathState);
            if (normalizedDeath != "die" && normalizedDeath != "die_left") return false;
            if (_heroRenderer == null) return false;

            ClearAttackVisuals();

            var corpse = new GameObject("hero_death_ghost");
            corpse.transform.position = _heroRenderer.transform.position;
            corpse.transform.rotation = _heroRenderer.transform.rotation;
            corpse.transform.localScale = _heroRenderer.transform.lossyScale;
            var corpseRenderer = corpse.AddComponent<SpriteRenderer>();
            corpseRenderer.sprite = _heroRenderer.sprite;
            corpseRenderer.color = _heroRenderer.color;
            corpseRenderer.flipX = _heroRenderer.flipX;
            corpseRenderer.sortingLayerID = _heroRenderer.sortingLayerID;
            corpseRenderer.sortingOrder = _heroRenderer.sortingOrder;
            var corpseAnimator = corpse.AddComponent<SpriteAnimator>();
            corpseAnimator.SpriteBaseKey = _heroAnimator != null
                ? _heroAnimator.SpriteBaseKey
                : string.Empty;
            corpseAnimator.AnimType = _heroAnimator != null
                ? _heroAnimator.AnimType
                : "atFrame";
            BattleBridge.PlayVisualAnimation(
                corpseAnimator,
                corpseRenderer,
                normalizedDeath,
                1f,
                normalizedDeath != "die_left");

            var corpseModel = new VisualModel
            {
                Container = corpse,
                SpriteRoot = corpse.transform,
                Renderer = corpseRenderer,
                Animator = corpseAnimator,
                IsGhost = true,
                DestroyAt = Time.unscaledTime + ResolveDeathHoldSeconds(
                    corpseAnimator.SpriteBaseKey,
                    normalizedDeath,
                    deathHoldMs),
            };
            _ghosts.Add(corpseModel);

            _heroDetached = true;
            _heroRenderer.enabled = false;
            if (_heroAnimator != null) _heroAnimator.Stop();
            if (_rootView != null) _rootView.SetHpBarVisible(false);
            return true;
        }

        public int GetVisualCount()
        {
            RemoveDestroyedEntries();
            return _models.Count;
        }

        public int GetGhostCount()
        {
            RemoveDestroyedEntries();
            return _ghosts.Count;
        }

        /// <summary>清空所有纯表现对象；根节点、句柄和 Match 状态不在这里管理。</summary>
        public void Clear()
        {
            for (int i = 0; i < _models.Count; i++) DestroyModel(_models[i]);
            for (int i = 0; i < _ghosts.Count; i++) DestroyModel(_ghosts[i]);

            _models.Clear();
            _ghosts.Clear();
            ClearAttackVisuals();
            _playedPulseIds.Clear();
            if (_heroRenderer != null) _heroRenderer.enabled = true;
            if (_rootView != null) _rootView.SetHpBarVisible(true);
            _bound = false;
            _heroDetached = false;
        }

        private void OnDestroy()
        {
            Clear();
        }

        private void Update()
        {
            if (!_bound) return;
            float now = Time.unscaledTime;
            UpdateModels(now);
            UpdateGhosts(now);
            UpdatePendingShots(now);
            UpdateVisualProjectiles();
        }

        private bool IsBound()
        {
            return _bound && _rootView != null && !string.IsNullOrEmpty(_troopBaseKey);
        }

        private static bool TryLoadFormation()
        {
            if (_formationLoaded) return _formationValid;
            _formationLoaded = true;
            FormationByCount.Clear();
            try
            {
                var manager = ConfigManager.Instance;
                if (manager == null)
                    return FailFormation("ConfigManager unavailable");
                manager.LoadIfNeeded();
                var rows = manager.GetTableList("squad_visual");
                if (rows == null || rows.Count != 15)
                    return FailFormation("squad_visual must contain exactly 15 rows");

                for (int count = 1; count <= MaxVisualModels; count++)
                    FormationByCount[count] = new FormationSlot[count];

                var seen = new HashSet<string>();
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    int count = manager.GetValue<int>(row, "visual_count", 0);
                    int slot = manager.GetValue<int>(row, "slot_index", 0);
                    if (count < 1 || count > MaxVisualModels || slot < 1 || slot > count)
                        return FailFormation($"row[{i}] count/slot out of range");
                    string key = count + "|" + slot;
                    if (!seen.Add(key)) return FailFormation($"duplicate count/slot {key}");

                    FormationByCount[count][slot - 1] = new FormationSlot
                    {
                        SlotIndex = slot,
                        ForwardMilli = manager.GetValue<int>(row, "forward_offset_milli", 0),
                        LateralMilli = manager.GetValue<int>(row, "lateral_offset_milli", 0),
                        SortingBias = manager.GetValue<int>(row, "sorting_bias", 0),
                    };
                }

                for (int count = 1; count <= MaxVisualModels; count++)
                {
                    var slots = FormationByCount[count];
                    for (int i = 0; i < slots.Length; i++)
                    {
                        if (slots[i] == null || slots[i].SlotIndex != i + 1)
                            return FailFormation($"count {count} slots must be continuous");
                    }
                }
                _formationValid = true;
                return true;
            }
            catch (Exception exception)
            {
                return FailFormation(exception.Message);
            }
        }

        private static bool FailFormation(string reason)
        {
            _formationValid = false;
            if (!_formationErrorLogged)
            {
                _formationErrorLogged = true;
                Debug.LogError($"[SquadVisualController] squad_visual invalid: {reason}");
            }
            return false;
        }

        private void LoadPresentationConfig()
        {
            try
            {
                var manager = ConfigManager.Instance;
                if (manager == null) return;
                manager.LoadIfNeeded();
                _defaultReflowMs = ReadConfigInt(
                    manager, "GameConfig", "squad_visual_reflow_ms", DefaultReflowMs);
                _defaultRefillFadeMs = ReadConfigInt(
                    manager, "GameConfig", "squad_visual_refill_fade_ms", DefaultRefillFadeMs);
                _defaultDeathHoldMs = ReadConfigInt(
                    manager, "GameConfig", "squad_visual_death_hold_ms", DefaultDeathHoldMs);
                _attackDurationFallbackMs = ReadConfigInt(
                    manager, "match_rule", "anim_attack_duration_fallback_ms", DefaultAttackDurationMs);
                _attackHitFallbackBp = ReadConfigInt(
                    manager, "match_rule", "anim_attack_hit_fallback_bp", DefaultAttackHitBp);
                int speedMilli = ReadConfigInt(
                    manager,
                    "GameConfig",
                    "troop_visual_projectile_speed_milli",
                    DefaultProjectileSpeedMilli);
                _visualProjectileSpeed = Mathf.Max(0.001f, speedMilli / 1000f);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[SquadVisualController] presentation config fallback: {exception.Message}");
            }
        }

        private static int ReadConfigInt(
            ConfigManager manager,
            string tableName,
            string key,
            int fallback)
        {
            var row = manager.GetTableInfo(tableName, "key", key);
            int value = manager.GetValue<int>(row, "value", fallback);
            return value > 0 ? value : fallback;
        }

        private static bool ResolveTroopIsRanged(int troopNpcId)
        {
            try
            {
                var manager = ConfigManager.Instance;
                if (manager == null) return false;
                manager.LoadIfNeeded();
                var troop = manager.GetTableInfo("troop", "npc_id", troopNpcId);
                return manager.GetValue<int>(troop, "attack_range", 1) > 1;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNormalizeVisualCount(int visualCount)
        {
            return visualCount >= 0 && visualCount <= MaxVisualModels;
        }

        private static bool TryNormalizeFacing(string facing, out string normalized)
        {
            normalized = facing == null ? string.Empty : facing.Trim().ToLowerInvariant();
            return normalized == "right"
                || normalized == "left"
                || normalized == "up"
                || normalized == "down";
        }

        private static bool TryInferFacing(string stateName, string fallback, out string facing)
        {
            if (stateName == "idle_down" || stateName == "walk_down")
            {
                facing = "down";
                return true;
            }
            if (stateName == "walk_up")
            {
                facing = "up";
                return true;
            }
            if (stateName == "walk_left"
                || stateName == "combat_idle_left"
                || stateName == "attack_left"
                || stateName == "die_left")
            {
                facing = "left";
                return true;
            }
            if (stateName == "walk"
                || stateName == "combat_idle"
                || stateName == "attack"
                || stateName == "die")
            {
                facing = "right";
                return true;
            }
            facing = fallback;
            return false;
        }

        private static bool IsFacingRight(string facing)
        {
            return facing != "left";
        }

        private static bool IsAttackVisualCancelledBy(string stateName)
        {
            return stateName == "idle_down"
                || stateName == "walk"
                || stateName == "walk_left"
                || stateName == "walk_up"
                || stateName == "walk_down"
                || stateName == "die"
                || stateName == "die_left";
        }

        private static float NormalizeSpeed(float speedMult)
        {
            return speedMult > 0f && !float.IsNaN(speedMult) && !float.IsInfinity(speedMult)
                ? speedMult
                : 1f;
        }

        private VisualModel CreateModel(int stableSlot)
        {
            var container = new GameObject($"troop_visual_{stableSlot}");
            container.transform.SetParent(transform, false);
            var spriteRoot = new GameObject("sprite_root");
            spriteRoot.transform.SetParent(container.transform, false);
            var renderer = spriteRoot.AddComponent<SpriteRenderer>();
            if (_heroRenderer != null)
            {
                renderer.sortingLayerID = _heroRenderer.sortingLayerID;
                spriteRoot.transform.localScale = _heroRenderer.transform.localScale;
            }
            else
            {
                renderer.sortingLayerName = _team == 1
                    ? HDSortingLayers.Enemy
                    : HDSortingLayers.Tower;
            }

            var animator = spriteRoot.AddComponent<SpriteAnimator>();
            animator.SpriteBaseKey = _troopBaseKey;
            animator.AnimType = _troopAnimType;
            var model = new VisualModel
            {
                StableSlot = stableSlot,
                FormationIndex = _models.Count + 1,
                Container = container,
                SpriteRoot = spriteRoot.transform,
                Renderer = renderer,
                Animator = animator,
            };
            BattleBridge.PlayVisualAnimation(
                animator,
                renderer,
                _stateName,
                1f,
                IsFacingRight(_facing));
            return model;
        }

        private int FindNextStableSlot()
        {
            for (int slot = 1; slot <= MaxVisualModels; slot++)
            {
                bool used = false;
                for (int i = 0; i < _models.Count; i++)
                {
                    if (_models[i].StableSlot == slot)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used) return slot;
            }
            return MaxVisualModels;
        }

        private static int CompareStableSlot(VisualModel a, VisualModel b)
        {
            return a.StableSlot.CompareTo(b.StableSlot);
        }

        private void ReassignFormationIndices()
        {
            _models.Sort(CompareStableSlot);
            for (int i = 0; i < _models.Count; i++)
                _models[i].FormationIndex = i + 1;
        }

        private void ReflowModels(int reflowMs)
        {
            if (_models.Count == 0) return;
            if (!FormationByCount.TryGetValue(_models.Count, out var slots)) return;
            float now = Time.unscaledTime;
            for (int i = 0; i < _models.Count; i++)
            {
                var model = _models[i];
                var slot = slots[model.FormationIndex - 1];
                BeginMove(model, ResolveLocalPosition(slot), reflowMs, now);
                UpdateModelSorting(model, slot);
            }
        }

        private Vector3 ResolveLocalPosition(FormationSlot slot)
        {
            float forward = slot.ForwardMilli / 1000f;
            float lateral = slot.LateralMilli / 1000f;
            Vector2 forwardAxis;
            Vector2 lateralAxis;
            switch (_facing)
            {
                case "left":
                    forwardAxis = Vector2.left;
                    lateralAxis = Vector2.down;
                    break;
                case "up":
                    forwardAxis = Vector2.up;
                    lateralAxis = Vector2.left;
                    break;
                case "down":
                    forwardAxis = Vector2.down;
                    lateralAxis = Vector2.right;
                    break;
                default:
                    forwardAxis = Vector2.right;
                    lateralAxis = Vector2.up;
                    break;
            }
            float x = (forwardAxis.x * forward + lateralAxis.x * lateral)
                * Mathf.Max(0.001f, GridMap.CellSizeX);
            float y = (forwardAxis.y * forward + lateralAxis.y * lateral)
                * Mathf.Max(0.001f, GridMap.CellSizeY);
            return new Vector3(x, y, 0f);
        }

        private static void BeginMove(
            VisualModel model,
            Vector3 target,
            int reflowMs,
            float now)
        {
            if (model == null || model.Container == null) return;
            model.MoveStart = model.Container.transform.localPosition;
            model.MoveTarget = target;
            model.MoveStartTime = now;
            model.MoveDuration = Mathf.Max(0, reflowMs) / 1000f;
            if (model.MoveDuration <= 0f)
                model.Container.transform.localPosition = target;
        }

        private static void BeginFade(
            VisualModel model,
            float from,
            float to,
            int fadeMs)
        {
            if (model == null) return;
            model.FadeStartAlpha = Mathf.Clamp01(from);
            model.FadeTargetAlpha = Mathf.Clamp01(to);
            model.FadeStartTime = Time.unscaledTime;
            model.FadeDuration = Mathf.Max(0, fadeMs) / 1000f;
            if (model.FadeDuration <= 0f) SetModelAlpha(model, model.FadeTargetAlpha);
        }

        private static void SetModelAlpha(VisualModel model, float alpha)
        {
            if (model == null || model.Renderer == null) return;
            var color = model.Renderer.color;
            color.a = Mathf.Clamp01(alpha);
            model.Renderer.color = color;
            if (model.Animator != null) model.Animator.SetBaseAlpha(color.a);
        }

        private void UpdateModels(float now)
        {
            for (int i = _models.Count - 1; i >= 0; i--)
            {
                var model = _models[i];
                if (model == null || model.Container == null)
                {
                    _models.RemoveAt(i);
                    continue;
                }
                UpdateModelTransform(model, now);
                UpdateModelSorting(model, GetSlot(model));
            }
        }

        private void UpdateGhosts(float now)
        {
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                var ghost = _ghosts[i];
                if (ghost == null || ghost.Container == null || now >= ghost.DestroyAt)
                {
                    DestroyModel(ghost);
                    _ghosts.RemoveAt(i);
                    continue;
                }
                UpdateModelTransform(ghost, now);
            }
        }

        private static void UpdateModelTransform(VisualModel model, float now)
        {
            if (model == null || model.Container == null) return;
            if (model.MoveDuration > 0f)
            {
                float t = Mathf.Clamp01((now - model.MoveStartTime) / model.MoveDuration);
                model.Container.transform.localPosition = Vector3.Lerp(
                    model.MoveStart,
                    model.MoveTarget,
                    t);
                if (t >= 1f) model.MoveDuration = 0f;
            }
            if (model.FadeDuration > 0f)
            {
                float t = Mathf.Clamp01((now - model.FadeStartTime) / model.FadeDuration);
                SetModelAlpha(
                    model,
                    Mathf.Lerp(model.FadeStartAlpha, model.FadeTargetAlpha, t));
                if (t >= 1f) model.FadeDuration = 0f;
            }
        }

        private FormationSlot GetSlot(VisualModel model)
        {
            if (model == null
                || !FormationByCount.TryGetValue(_models.Count, out var slots)
                || model.FormationIndex < 1
                || model.FormationIndex > slots.Length)
                return null;
            return slots[model.FormationIndex - 1];
        }

        private void UpdateModelSorting(VisualModel model, FormationSlot slot)
        {
            if (model == null || model.Renderer == null) return;
            int heroOrder = _heroRenderer != null ? _heroRenderer.sortingOrder : 0;
            int bias = slot != null ? slot.SortingBias : 0;
            int localDepth = model.Container != null
                ? Mathf.RoundToInt(-model.Container.transform.localPosition.y * 100f)
                : 0;
            // 英雄固定处于所有兵模之前；局部纵深和表内 bias 只决定兵模彼此遮挡。
            model.Renderer.sortingOrder = Mathf.Min(heroOrder - 1, heroOrder + localDepth + bias);
            if (_heroRenderer != null)
                model.Renderer.sortingLayerID = _heroRenderer.sortingLayerID;
        }

        private List<VisualModel> SelectCasualties(int count, string sourceDirection)
        {
            var candidates = new List<VisualModel>(_models);
            if (TryNormalizeFacing(sourceDirection, out string direction))
            {
                Vector2 axis = direction == "left"
                    ? Vector2.left
                    : direction == "right"
                        ? Vector2.right
                        : direction == "up"
                            ? Vector2.up
                            : Vector2.down;
                candidates.Sort((a, b) =>
                {
                    float aScore = Vector2.Dot(
                        a.Container != null
                            ? (Vector2)a.Container.transform.localPosition
                            : Vector2.zero,
                        axis);
                    float bScore = Vector2.Dot(
                        b.Container != null
                            ? (Vector2)b.Container.transform.localPosition
                            : Vector2.zero,
                        axis);
                    int score = bScore.CompareTo(aScore);
                    return score != 0 ? score : a.StableSlot.CompareTo(b.StableSlot);
                });
            }
            else
            {
                candidates.Sort(CompareStableSlot);
            }
            if (candidates.Count > count)
                candidates.RemoveRange(count, candidates.Count - count);
            return candidates;
        }

        private void MakeTroopGhost(VisualModel model, string deathState, int deathHoldMs)
        {
            if (model == null || model.Container == null) return;
            model.Container.transform.SetParent(null, true);
            model.IsGhost = true;
            model.MoveDuration = 0f;
            model.FadeDuration = 0f;
            bool faceRight = deathState != "die_left";
            BattleBridge.PlayVisualAnimation(
                model.Animator,
                model.Renderer,
                deathState,
                1f,
                faceRight);
            model.DestroyAt = Time.unscaledTime + ResolveDeathHoldSeconds(
                _troopBaseKey,
                deathState,
                deathHoldMs);
            _ghosts.Add(model);
        }

        private float ResolveDeathHoldSeconds(
            string baseKey,
            string deathState,
            int deathHoldMs)
        {
            int fallback = deathHoldMs > 0 ? deathHoldMs : _defaultDeathHoldMs;
            int duration = BattleBridge.Battle_GetAnimStateDurationMs(
                baseKey,
                deathState,
                fallback);
            return Mathf.Max(0f, duration / 1000f);
        }

        private void UpdatePendingShots(float now)
        {
            for (int i = _pendingShots.Count - 1; i >= 0; i--)
            {
                var pending = _pendingShots[i];
                if (pending == null || pending.Target == null)
                {
                    _pendingShots.RemoveAt(i);
                    continue;
                }
                if (now < pending.DueTime) continue;
                for (int modelIndex = 0; modelIndex < _models.Count; modelIndex++)
                {
                    var model = _models[modelIndex];
                    if (model != null
                        && model.SpriteRoot != null
                        && model.Renderer != null
                        && model.Renderer.enabled
                        && model.Renderer.color.a > 0.001f)
                        SpawnVisualProjectile(model.SpriteRoot.position, pending.Target, pending.ProjectileKey);
                }
                _pendingShots.RemoveAt(i);
            }
        }

        private void SpawnVisualProjectile(Vector3 origin, Transform target, string projectileKey)
        {
            if (target == null) return;
            var projectile = new GameObject("troop_visual_projectile");
            projectile.transform.position = origin;
            var renderer = projectile.AddComponent<SpriteRenderer>();
            renderer.sprite = BattleBridge.GetVisualProjectileSprite(projectileKey);
            renderer.sortingLayerName = HDSortingLayers.Projectile;
            renderer.sortingOrder = GridSortingService.CalcSortingOrder(origin.y);
            _visualProjectiles.Add(new VisualProjectile
            {
                GameObject = projectile,
                Renderer = renderer,
                Target = target,
                Speed = _visualProjectileSpeed,
            });
        }

        private void UpdateVisualProjectiles()
        {
            for (int i = _visualProjectiles.Count - 1; i >= 0; i--)
            {
                var projectile = _visualProjectiles[i];
                if (projectile == null
                    || projectile.GameObject == null
                    || projectile.Target == null)
                {
                    DestroyVisualProjectileAt(i);
                    continue;
                }
                var current = projectile.GameObject.transform.position;
                var target = projectile.Target.position;
                var next = Vector3.MoveTowards(
                    current,
                    target,
                    projectile.Speed * Time.deltaTime);
                projectile.GameObject.transform.position = next;
                var direction = target - current;
                if (projectile.Renderer != null)
                {
                    projectile.Renderer.flipX = direction.x < 0f;
                    projectile.Renderer.sortingOrder = GridSortingService.CalcSortingOrder(next.y);
                }
                if ((next - target).sqrMagnitude <= 0.0001f)
                    DestroyVisualProjectileAt(i);
            }
        }

        private void DestroyVisualProjectileAt(int index)
        {
            if (index < 0 || index >= _visualProjectiles.Count) return;
            var projectile = _visualProjectiles[index];
            if (projectile != null && projectile.GameObject != null)
                Destroy(projectile.GameObject);
            _visualProjectiles.RemoveAt(index);
        }

        private void ClearAttackVisuals()
        {
            _pendingShots.Clear();
            for (int i = _visualProjectiles.Count - 1; i >= 0; i--)
                DestroyVisualProjectileAt(i);
        }

        private void RemoveDestroyedEntries()
        {
            _models.RemoveAll(model => model == null || model.Container == null);
            _ghosts.RemoveAll(model => model == null || model.Container == null);
        }

        private static void DestroyModel(VisualModel model)
        {
            if (model != null && model.Container != null)
                Destroy(model.Container);
        }
    }
}
