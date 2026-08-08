using UnityEngine;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 逐帧 sprite 翻播（MVP 阶段：idle/walk/attack/death 3-4 帧/单位）。
    ///
    /// 设计原则（CLAUDE.md §1）：
    ///   - 不是协程（避 GC）：用 LateUpdate 累计 dt 切帧
    ///   - 0 SerializeField：业务调 Play(state, frames, fps) 时传入帧数组
    ///   - 不写"何时切动作"的业务逻辑（仅播放帧），何时 idle / attack / death 由 Lua 决定
    ///
    /// 兼容 HitFeedback Hit-Stop：响应 OnHitStopBegin / OnHitStopEnd SendMessage 暂停。
    /// </summary>
    public class SpriteAnimator : MonoBehaviour
    {
        // ============ 运行时帧表 ============
        private SpriteRenderer _sr;
        private string _curState;
        private Sprite[] _curFrames;
        private float _curFps = 16f;  // 2026-05-28: 8→16 (用户：8 FPS 视觉卡顿)
        private float[] _curDurations;
        private System.Action<int> _onFrameEnter;
        private int _curFrameIdx;
        private float _accumTime;
        private bool _looping = true;
        private bool _playing;
        private BundleClip _bundleClip;
        private float _stateTime;
        private float _bundleFrameStartTime;

        // 持续态记忆：最近一次循环动画（idle*/walk*）。一次性动画（attack*）播完后自动切回此态，
        // 否则单位会僵在攻击末帧。die* 例外：播完停在末帧（尸体）。
        private string _restState;
        private Sprite[] _restFrames;
        private float _restFps = 16f;  // 2026-05-28: 8→16 同步
        private float[] _restDurations;
        private System.Action<int> _restOnFrameEnter;
        private BundleClip _restBundleClip;
        private RuntimeOverlayClip[] _restRuntimeOverlayClips;

        // 正式普通攻击开始时冻结“刚由 MatchView 写入”的横向战斗姿态。攻击完成后
        // 优先回到这份快照，避免旧 walk/rest 状态在一次性动作结束时被误恢复。
        private string _attackRestState;
        private Sprite[] _attackRestFrames;
        private float _attackRestFps = 16f;
        private float[] _attackRestDurations;
        private System.Action<int> _attackRestOnFrameEnter;
        private BundleClip _attackRestBundleClip;
        private RuntimeOverlayClip[] _attackRestRuntimeOverlayClips;

        // bundle 变换轨运行态。节点只在 PlayBundle 时扩容，LateUpdate 只做数组求值。
        private Transform _spriteRoot;
        private UnitView _unitView;
        private Transform[] _overlayRoots;
        private SpriteRenderer[] _overlayRenderers;
        private RuntimeOverlayClip[] _runtimeOverlayClips;
        private Transform[] _runtimeOverlayRoots;
        private SpriteRenderer[] _runtimeOverlayRenderers;
        private Vector3 _fallbackBundleBaseLocalPos;
        private bool _fallbackBundleBaseCached;
        private float _baseAlpha = 1f;
        private float _lastSelfAlpha = 1f;
        private bool _selfApplied;

        private static bool IsDeathState(string state)
        {
            return state == "die"
                || state == "die_left"
                || state == "die_right";
        }

        private static bool IsCombatRestState(string state)
        {
            return state == "combat_idle" || state == "combat_idle_left";
        }

        private static bool IsFormalAttackState(string state)
        {
            return state == "attack" || state == "attack_left";
        }

        private void ClearAttackRest()
        {
            _attackRestState = null;
            _attackRestFrames = null;
            _attackRestDurations = null;
            _attackRestOnFrameEnter = null;
            _attackRestBundleClip = null;
            _attackRestRuntimeOverlayClips = null;
        }

        private void CaptureCombatRestForAttack(string state)
        {
            ClearAttackRest();
            if (!IsFormalAttackState(state)
                || !IsCombatRestState(_restState)
                || _restFrames == null
                || _restFrames.Length == 0)
                return;

            _attackRestState = _restState;
            _attackRestFrames = _restFrames;
            _attackRestFps = _restFps;
            _attackRestDurations = _restDurations;
            _attackRestOnFrameEnter = _restOnFrameEnter;
            _attackRestBundleClip = _restBundleClip;
            _attackRestRuntimeOverlayClips = _restRuntimeOverlayClips;
        }

        private bool RestoreCapturedCombatRest()
        {
            if (_attackRestFrames == null || _attackRestFrames.Length == 0) return false;
            string state = _attackRestState;
            var frames = _attackRestFrames;
            float fps = _attackRestFps;
            var durations = _attackRestDurations;
            var onFrameEnter = _attackRestOnFrameEnter;
            var bundleClip = _attackRestBundleClip;
            var runtimeOverlays = _attackRestRuntimeOverlayClips;
            ClearAttackRest();

            if (bundleClip != null)
                PlayBundle(state, bundleClip, runtimeOverlays);
            else if (durations != null)
                PlayTimed(state, frames, durations, true, onFrameEnter);
            else
                Play(state, frames, fps, true);
            return true;
        }

        /// <summary>sprite 基础 key（如 "hero/guan_yu"），
        /// BattleBridge.Battle_PlayAnim 拼成 "{key}_{state}_{frame}.png" 加载帧列表。</summary>
        public string SpriteBaseKey;

        /// <summary>动画类型（2026-05-29 Q1 新增）：
        ///   "atFrame" = 序列帧动画（当前默认，逐张 PNG 切播）
        ///   "atSpine" = Spine 骨骼动画（数据驱动，需 spine-unity SDK，目前 stub 状态 fallback 到 frame）
        /// 由 BattleBridge.Battle_SpawnUnit 在 spawn 时按 npc.tab.anim_type 配置注入。
        /// 未配置时默认 atFrame。</summary>
        public string AnimType = "atFrame";

        // Hit-Stop 兼容（HitFeedback.cs OnHitStopBegin/End SendMessage）
        private bool _hitStopped;

        private void Awake()
        {
            _sr = GetComponentInChildren<SpriteRenderer>();
            _spriteRoot = _sr != null ? _sr.transform : null;
            _unitView = GetComponent<UnitView>();
            if (_sr != null) _baseAlpha = _sr.color.a;
        }

        /// <summary>开始播放某状态。frames 可为 null/空 → 静止显示当前 sprite。
        /// fps 默认 16（2026-05-28 从 8 提到 16，解决视觉卡顿）。
        /// 注意：现 idle/walk 6 帧、attack 8 帧素材，fps=16 下 attack 一轮 = 0.5 秒（之前 1 秒），动作节奏视觉变快。</summary>
        public void Play(string stateName, Sprite[] frames, float fps = 16f, bool looping = true)
        {
            LeaveBundleMode();
            _curState = stateName;
            _curFrames = frames;
            _curFps = Mathf.Max(0.1f, fps);
            _curDurations = null;
            _onFrameEnter = null;
            _curFrameIdx = 0;
            _accumTime = 0f;
            _looping = looping;
            _playing = frames != null && frames.Length > 0;
            if (!looping) CaptureCombatRestForAttack(stateName);
            else ClearAttackRest();

            // 记住最近一次循环态，供一次性动画播完后回切
            if (looping && _playing)
            {
                _restState = stateName;
                _restFrames = frames;
                _restFps = _curFps;
                _restDurations = null;
                _restOnFrameEnter = null;
                _restBundleClip = null;
                _restRuntimeOverlayClips = null;
            }

            if (_playing && _sr != null)
            {
                _sr.sprite = frames[0];
            }
        }

        /// <summary>按每个序列条目的独立时长播放。进入条目时回调其数组索引（含首帧和循环回卷）。</summary>
        public void PlayTimed(string state, Sprite[] frames, float[] durations, bool looping,
            System.Action<int> onFrameEnter = null)
        {
            if (!AreDurationsValid(frames, durations))
            {
                // 调用方数据异常时保持播放器可用；BattleBridge 会在更上层记录配置 warning 并走均摊路径。
                Play(state, frames, 16f, looping);
                return;
            }

            LeaveBundleMode();
            StartTimed(state, frames, durations, looping, onFrameEnter, null, null);
        }

        /// <summary>播放动画包时间线。帧推进与 PlayTimed 共用同一内核。</summary>
        public void PlayBundle(string state, BundleClip clip,
            RuntimeOverlayClip[] runtimeOverlays = null)
        {
            if (clip == null || !AreDurationsValid(clip.Frames, clip.Durations))
            {
                Play(state, clip != null ? clip.Frames : null, 16f,
                    clip != null && clip.Looping);
                return;
            }

            LeaveBundleMode();
            _bundleClip = clip;
            _stateTime = 0f;
            _bundleFrameStartTime = 0f;
            EnsureOverlayCapacity(clip.Tracks != null ? clip.Tracks.Length : 0);
            _runtimeOverlayClips = runtimeOverlays;
            EnsureRuntimeOverlayCapacity(runtimeOverlays);
            ApplyAnimHpHidden(clip.HideHp);
            StartTimed(state, clip.Frames, clip.Durations, clip.Looping,
                clip.OnFrameEnter, clip, runtimeOverlays);
            EvaluateRuntimeOverlays();
        }

        private void StartTimed(string state, Sprite[] frames, float[] durations, bool looping,
            System.Action<int> onFrameEnter, BundleClip bundleClip,
            RuntimeOverlayClip[] runtimeOverlays)
        {
            _curState = state;
            _curFrames = frames;
            _curFps = 16f;
            _curDurations = durations;
            _onFrameEnter = onFrameEnter;
            _curFrameIdx = 0;
            _accumTime = 0f;
            _looping = looping;
            _playing = true;
            if (!looping) CaptureCombatRestForAttack(state);
            else ClearAttackRest();

            if (looping)
            {
                _restState = state;
                _restFrames = frames;
                _restFps = 16f;
                _restDurations = durations;
                _restOnFrameEnter = onFrameEnter;
                _restBundleClip = bundleClip;
                _restRuntimeOverlayClips = runtimeOverlays;
            }

            EnterFrame(0);
        }

        private static bool AreDurationsValid(Sprite[] frames, float[] durations)
        {
            if (frames == null || frames.Length == 0
                || durations == null || durations.Length != frames.Length)
                return false;

            for (int i = 0; i < durations.Length; i++)
            {
                float value = durations[i];
                if (value <= 0f || float.IsNaN(value) || float.IsInfinity(value))
                    return false;
            }
            return true;
        }

        private void EnterFrame(int frameIndex)
        {
            if (_curFrames == null || frameIndex < 0 || frameIndex >= _curFrames.Length) return;
            if (_sr != null) _sr.sprite = _curFrames[frameIndex];
            _onFrameEnter?.Invoke(frameIndex);
        }

        public void Stop()
        {
            _playing = false;
            _curState = null;
            _curFrames = null;
            _curDurations = null;
            _onFrameEnter = null;
            ClearAttackRest();
            LeaveBundleMode();
        }

        public void ClearRuntimeOverlays()
        {
            _runtimeOverlayClips = null;
            _restRuntimeOverlayClips = null;
            HideRuntimeOverlays();
        }

        public void ClearRuntimeOverlaySlot(int slotIndex)
        {
            if (slotIndex < 0) return;
            if (_runtimeOverlayClips != null && slotIndex < _runtimeOverlayClips.Length)
                _runtimeOverlayClips[slotIndex] = null;
            if (_restRuntimeOverlayClips != null && slotIndex < _restRuntimeOverlayClips.Length)
                _restRuntimeOverlayClips[slotIndex] = null;
            if (_runtimeOverlayRenderers != null && slotIndex < _runtimeOverlayRenderers.Length)
            {
                var renderer = _runtimeOverlayRenderers[slotIndex];
                if (renderer != null)
                {
                    renderer.enabled = false;
                    renderer.sprite = null;
                }
            }
        }

        public string CurrentState => _curState;
        public bool IsPlaying => _playing;
        public bool InBundleMode => _bundleClip != null;
        public int CanvasW => _bundleClip != null ? _bundleClip.CanvasW : 0;
        public int CanvasH => _bundleClip != null ? _bundleClip.CanvasH : 0;

        /// <summary>UnitView.SetAlpha 同步基础透明度；self_keys 只乘动画分量。</summary>
        public void SetBaseAlpha(float alpha)
        {
            ResolveRenderer();
            _baseAlpha = Mathf.Clamp01(alpha);
            if (_sr == null) return;
            var color = _sr.color;
            color.a = _selfApplied ? _baseAlpha * _lastSelfAlpha : _baseAlpha;
            _sr.color = color;
        }

        /// <summary>给 HitFeedback Hit-Stop 用。</summary>
        public void OnHitStopBegin() { _hitStopped = true; }
        public void OnHitStopEnd() { _hitStopped = false; }

        private void LateUpdate()
        {
            if (!_playing || _hitStopped) return;
            if (_curFrames == null || _curFrames.Length == 0) return;
            // 保持旧 Play() 的单帧行为；timed 单帧仍需按 dur 完成/回卷并触发事件。
            if (_curFrames.Length == 1 && _curDurations == null) return;
            if (_sr == null)
            {
                // bundle 在 PlayBundle 时已解析 renderer；热路径不做组件查找。
                if (_bundleClip != null) return;
                _sr = GetComponentInChildren<SpriteRenderer>();
                if (_sr == null) return;
            }

            _accumTime += Time.deltaTime;
            float frameDur = _curDurations != null
                ? _curDurations[_curFrameIdx]
                : 1f / _curFps;
            if (_accumTime >= frameDur)
            {
                // 一次跳一帧（避免大 dt 时跳多帧造成动画失真，宁可漏帧也保线性）
                _accumTime -= frameDur;
                if (_bundleClip != null) _bundleFrameStartTime += frameDur;
                _curFrameIdx++;
                if (_curFrameIdx >= _curFrames.Length)
                {
                    if (_looping)
                    {
                        _curFrameIdx = 0;
                        _bundleFrameStartTime = 0f;
                    }
                    else
                    {
                        // 一次性动画播完：attack* 回到持续态（idle*/walk*）；die* 停在末帧（尸体）
                        _curFrameIdx = _curFrames.Length - 1;
                        _playing = false;
                        _sr.sprite = _curFrames[_curFrameIdx];
                        if (_bundleClip != null)
                        {
                            _stateTime = _bundleClip.TotalDuration;
                            EvaluateBundleVisuals();
                            EvaluateRuntimeOverlays();
                        }
                        if (!IsDeathState(_curState) && RestoreCapturedCombatRest())
                            return;
                        if (!IsDeathState(_curState) && _curState != _restState
                            && _restFrames != null && _restFrames.Length > 0)
                        {
                            if (_restBundleClip != null)
                                PlayBundle(_restState, _restBundleClip, _restRuntimeOverlayClips);
                            else if (_restDurations != null)
                                PlayTimed(_restState, _restFrames, _restDurations, true, _restOnFrameEnter);
                            else
                                Play(_restState, _restFrames, _restFps, true);
                        }
                        return;
                    }
                }
                EnterFrame(_curFrameIdx);
            }

            if (_bundleClip == null) return;
            float currentDur = _curDurations[_curFrameIdx];
            _stateTime = _bundleFrameStartTime + Mathf.Min(_accumTime, currentDur);
            if (_looping && _bundleClip.TotalDuration > 0f
                && _stateTime >= _bundleClip.TotalDuration)
            {
                _stateTime = Mathf.Repeat(_stateTime, _bundleClip.TotalDuration);
            }
            EvaluateBundleVisuals();
            EvaluateRuntimeOverlays();
        }

        private void ResolveRenderer()
        {
            if (_sr == null) _sr = GetComponentInChildren<SpriteRenderer>();
            if (_spriteRoot == null && _sr != null) _spriteRoot = _sr.transform;
            if (_unitView == null) _unitView = GetComponent<UnitView>();
        }

        private void EnsureOverlayCapacity(int required)
        {
            ResolveRenderer();
            HideOverlays();
            if (required <= 0 || _spriteRoot == null) return;

            int oldCount = _overlayRoots != null ? _overlayRoots.Length : 0;
            if (oldCount < required)
            {
                var roots = new Transform[required];
                var renderers = new SpriteRenderer[required];
                for (int i = 0; i < oldCount; i++)
                {
                    roots[i] = _overlayRoots[i];
                    renderers[i] = _overlayRenderers[i];
                }
                _overlayRoots = roots;
                _overlayRenderers = renderers;
            }

            for (int i = 0; i < required; i++)
            {
                if (_overlayRenderers[i] == null)
                {
                    var go = new GameObject($"fx_overlay_{i}", typeof(SpriteRenderer));
                    var root = go.transform;
                    root.SetParent(_spriteRoot, false);
                    root.localPosition = Vector3.zero;
                    root.localScale = Vector3.one;
                    _overlayRoots[i] = root;
                    _overlayRenderers[i] = go.GetComponent<SpriteRenderer>();
                }
                // 同 order 的轨按数组/兄弟顺序创建，后项保持在后。
                _overlayRoots[i].SetSiblingIndex(i);
                _overlayRenderers[i].enabled = false;
            }
        }

        private void HideOverlays()
        {
            if (_overlayRenderers == null) return;
            for (int i = 0; i < _overlayRenderers.Length; i++)
            {
                var renderer = _overlayRenderers[i];
                if (renderer == null) continue;
                renderer.enabled = false;
                renderer.sprite = null;
            }
        }

        private void EnsureRuntimeOverlayCapacity(RuntimeOverlayClip[] clips)
        {
            ResolveRenderer();
            HideRuntimeOverlays();
            if (clips == null || clips.Length == 0 || _spriteRoot == null) return;

            int oldCount = _runtimeOverlayRoots != null ? _runtimeOverlayRoots.Length : 0;
            if (oldCount < clips.Length)
            {
                var roots = new Transform[clips.Length];
                var renderers = new SpriteRenderer[clips.Length];
                for (int i = 0; i < oldCount; i++)
                {
                    roots[i] = _runtimeOverlayRoots[i];
                    renderers[i] = _runtimeOverlayRenderers[i];
                }
                _runtimeOverlayRoots = roots;
                _runtimeOverlayRenderers = renderers;
            }

            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null) continue;
                if (_runtimeOverlayRenderers[i] == null)
                {
                    var go = new GameObject($"fx_runtime_{i + 1}", typeof(SpriteRenderer));
                    var root = go.transform;
                    root.SetParent(_spriteRoot, false);
                    root.localPosition = Vector3.zero;
                    root.localScale = Vector3.one;
                    _runtimeOverlayRoots[i] = root;
                    _runtimeOverlayRenderers[i] = go.GetComponent<SpriteRenderer>();
                }
                _runtimeOverlayRenderers[i].enabled = false;
            }
        }

        private void HideRuntimeOverlays()
        {
            if (_runtimeOverlayRenderers == null) return;
            for (int i = 0; i < _runtimeOverlayRenderers.Length; i++)
            {
                var renderer = _runtimeOverlayRenderers[i];
                if (renderer == null) continue;
                renderer.enabled = false;
                renderer.sprite = null;
            }
        }

        private void LeaveBundleMode()
        {
            if (_bundleClip != null && _selfApplied && _spriteRoot != null)
            {
                if (_unitView != null)
                {
                    var basePos = _unitView.SpriteBaseLocalPos;
                    _spriteRoot.localPosition =
                        new Vector3(basePos.x, basePos.y, _spriteRoot.localPosition.z);
                }
                else if (_fallbackBundleBaseCached)
                {
                    _spriteRoot.localPosition =
                        new Vector3(_fallbackBundleBaseLocalPos.x, _fallbackBundleBaseLocalPos.y,
                            _spriteRoot.localPosition.z);
                }

                if (_sr != null)
                {
                    var color = _sr.color;
                    color.a = _baseAlpha;
                    _sr.color = color;
                }
            }

            HideOverlays();
            HideRuntimeOverlays();
            _runtimeOverlayClips = null;
            ApplyAnimHpHidden(false);
            _bundleClip = null;
            _stateTime = 0f;
            _bundleFrameStartTime = 0f;
            _fallbackBundleBaseCached = false;
            _selfApplied = false;
            _lastSelfAlpha = 1f;
        }

        private Vector3 ResolveBundleBaseLocalPosition()
        {
            if (_unitView != null) return _unitView.SpriteBaseLocalPos;
            if (!_fallbackBundleBaseCached)
            {
                _fallbackBundleBaseLocalPos = _spriteRoot.localPosition;
                _fallbackBundleBaseCached = true;
            }
            return _fallbackBundleBaseLocalPos;
        }

        private void EvaluateBundleVisuals()
        {
            if (_bundleClip == null || _spriteRoot == null || _sr == null) return;

            var selfKeys = _bundleClip.SelfKeys;
            if (selfKeys != null && selfKeys.Length > 0)
            {
                EvaluateSelfKeys(selfKeys, _stateTime, out float dx, out float dy, out float alpha);
                var basePos = ResolveBundleBaseLocalPosition();
                var visualScale = _spriteRoot.localScale;
                _spriteRoot.localPosition = new Vector3(
                    basePos.x + dx * 0.01f * visualScale.x,
                    basePos.y + dy * 0.01f * visualScale.y,
                    _spriteRoot.localPosition.z);

                _lastSelfAlpha = alpha;
                _selfApplied = true;
                var color = _sr.color;
                color.a = _baseAlpha * alpha;
                _sr.color = color;
            }

            var tracks = _bundleClip.Tracks;
            if (tracks == null || _overlayRenderers == null) return;
            for (int i = 0; i < tracks.Length; i++)
                EvaluateTrack(i, tracks[i], _stateTime);
        }

        private void EvaluateRuntimeOverlays()
        {
            if (_runtimeOverlayClips == null || _runtimeOverlayRenderers == null || _sr == null)
                return;

            for (int i = 0; i < _runtimeOverlayClips.Length; i++)
            {
                var clip = _runtimeOverlayClips[i];
                var renderer = i < _runtimeOverlayRenderers.Length
                    ? _runtimeOverlayRenderers[i]
                    : null;
                if (renderer == null) continue;
                if (clip == null || clip.Frames == null || clip.Durations == null
                    || clip.Frames.Length == 0 || clip.Durations.Length != clip.Frames.Length
                    || clip.TotalDuration <= 0f)
                {
                    renderer.enabled = false;
                    renderer.sprite = null;
                    continue;
                }

                float time = _stateTime;
                if (clip.Looping)
                {
                    time = Mathf.Repeat(time, clip.TotalDuration);
                }
                else if (time >= clip.TotalDuration)
                {
                    renderer.enabled = false;
                    renderer.sprite = null;
                    continue;
                }

                int frameIndex = 0;
                float frameEnd = clip.Durations[0];
                while (frameIndex + 1 < clip.Frames.Length && time >= frameEnd)
                {
                    frameIndex++;
                    frameEnd += clip.Durations[frameIndex];
                }

                renderer.sprite = clip.Frames[frameIndex];
                renderer.flipX = _sr.flipX;
                renderer.sortingLayerID = _sr.sortingLayerID;
                renderer.sortingOrder = _sr.sortingOrder + (clip.Above ? 2 : -2);
                var color = renderer.color;
                color.r = 1f;
                color.g = 1f;
                color.b = 1f;
                color.a = 1f;
                renderer.color = color;
                renderer.enabled = renderer.sprite != null;
            }
        }

        private void ApplyAnimHpHidden(bool hidden)
        {
            ResolveRenderer();
            if (_unitView != null)
                _unitView.SetHpBarAnimHidden(hidden);
        }

        private static void EvaluateSelfKeys(BundleSelfKey[] keys, float time,
            out float dx, out float dy, out float alpha)
        {
            if (keys.Length == 1 || time <= keys[0].Time)
            {
                dx = keys[0].Dx;
                dy = keys[0].Dy;
                alpha = keys[0].Alpha;
                return;
            }

            int next = 1;
            while (next < keys.Length && time >= keys[next].Time) next++;
            if (next >= keys.Length)
            {
                var last = keys[keys.Length - 1];
                dx = last.Dx;
                dy = last.Dy;
                alpha = last.Alpha;
                return;
            }

            var from = keys[next - 1];
            var to = keys[next];
            float span = to.Time - from.Time;
            float t = span > 0f ? Mathf.Clamp01((time - from.Time) / span) : 1f;
            dx = Mathf.Lerp(from.Dx, to.Dx, t);
            dy = Mathf.Lerp(from.Dy, to.Dy, t);
            alpha = Mathf.Lerp(from.Alpha, to.Alpha, t);
        }

        private void EvaluateTrack(int trackIndex, BundleTrackClip track, float time)
        {
            if (trackIndex < 0 || trackIndex >= _overlayRenderers.Length) return;
            var renderer = _overlayRenderers[trackIndex];
            var root = _overlayRoots[trackIndex];
            if (renderer == null || root == null || track == null
                || track.Times == null || track.Times.Length == 0
                || time < track.Times[0] || time >= track.EndTime)
            {
                if (renderer != null) renderer.enabled = false;
                return;
            }

            int current = 0;
            while (current + 1 < track.Times.Length && time >= track.Times[current + 1])
                current++;

            float dx = track.Dx[current];
            float dy = track.Dy[current];
            float scaleX = track.ScaleX[current];
            float scaleY = track.ScaleY[current];
            float alpha = track.Alpha[current];
            if (current + 1 < track.Times.Length)
            {
                float span = track.Times[current + 1] - track.Times[current];
                float t = span > 0f
                    ? Mathf.Clamp01((time - track.Times[current]) / span)
                    : 1f;
                dx = Mathf.Lerp(dx, track.Dx[current + 1], t);
                dy = Mathf.Lerp(dy, track.Dy[current + 1], t);
                scaleX = Mathf.Lerp(scaleX, track.ScaleX[current + 1], t);
                scaleY = Mathf.Lerp(scaleY, track.ScaleY[current + 1], t);
                alpha = Mathf.Lerp(alpha, track.Alpha[current + 1], t);
            }

            bool flipped = _sr.flipX;
            if (flipped) dx = -dx;
            renderer.sprite = track.Sprites[current];
            renderer.flipX = flipped;
            renderer.sortingLayerID = _sr.sortingLayerID;
            renderer.sortingOrder = _sr.sortingOrder + (track.Above ? 1 : -1);
            var color = renderer.color;
            color.r = 1f;
            color.g = 1f;
            color.b = 1f;
            color.a = alpha;
            renderer.color = color;
            root.localPosition = new Vector3(dx * 0.01f, dy * 0.01f, 0f);
            root.localScale = new Vector3(scaleX, scaleY, 1f);
            renderer.enabled = renderer.sprite != null;
        }
    }
}
