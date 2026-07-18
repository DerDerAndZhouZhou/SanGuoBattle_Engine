using System.Collections.Generic;
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

        // 持续态记忆：最近一次循环动画（idle/walk）。一次性动画（attack）播完后自动切回此态，
        // 否则单位/怪会僵在攻击末帧。die 例外：播完停在末帧（尸体）。
        private string _restState;
        private Sprite[] _restFrames;
        private float _restFps = 16f;  // 2026-05-28: 8→16 同步
        private float[] _restDurations;
        private System.Action<int> _restOnFrameEnter;

        /// <summary>sprite 基础 key（如 "monster/yellow_turban_grunt"），
        /// BattleBridge.Battle_PlayAnim 拼成 "{key}_{state}_{frame}.png" 加载帧列表。</summary>
        public string SpriteBaseKey;

        /// <summary>动画类型（2026-05-29 Q1 新增）：
        ///   "atFrame" = 序列帧动画（当前默认，逐张 PNG 切播）
        ///   "atSpine" = Spine 骨骼动画（数据驱动，需 spine-unity SDK，目前 stub 状态 fallback 到 frame）
        /// 由 BattleBridge.Battle_SpawnUnit 在 spawn 时按 npc.tab.anim_type 配置注入。
        /// 怪物等其他实体未配置时默认 atFrame。</summary>
        public string AnimType = "atFrame";

        // Hit-Stop 兼容（HitFeedback.cs OnHitStopBegin/End SendMessage）
        private bool _hitStopped;

        private void Awake()
        {
            _sr = GetComponentInChildren<SpriteRenderer>();
        }

        /// <summary>开始播放某状态。frames 可为 null/空 → 静止显示当前 sprite。
        /// fps 默认 16（2026-05-28 从 8 提到 16，解决视觉卡顿）。
        /// 注意：现 idle/walk 6 帧、attack 8 帧素材，fps=16 下 attack 一轮 = 0.5 秒（之前 1 秒），动作节奏视觉变快。</summary>
        public void Play(string stateName, Sprite[] frames, float fps = 16f, bool looping = true)
        {
            _curState = stateName;
            _curFrames = frames;
            _curFps = Mathf.Max(0.1f, fps);
            _curDurations = null;
            _onFrameEnter = null;
            _curFrameIdx = 0;
            _accumTime = 0f;
            _looping = looping;
            _playing = frames != null && frames.Length > 0;

            // 记住最近一次循环态，供一次性动画播完后回切
            if (looping && _playing)
            {
                _restState = stateName;
                _restFrames = frames;
                _restFps = _curFps;
                _restDurations = null;
                _restOnFrameEnter = null;
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

            _curState = state;
            _curFrames = frames;
            _curFps = 16f;
            _curDurations = durations;
            _onFrameEnter = onFrameEnter;
            _curFrameIdx = 0;
            _accumTime = 0f;
            _looping = looping;
            _playing = true;

            if (looping)
            {
                _restState = state;
                _restFrames = frames;
                _restFps = 16f;
                _restDurations = durations;
                _restOnFrameEnter = onFrameEnter;
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
        }

        public string CurrentState => _curState;
        public bool IsPlaying => _playing;

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
                _sr = GetComponentInChildren<SpriteRenderer>();
                if (_sr == null) return;
            }

            _accumTime += Time.deltaTime;
            float frameDur = _curDurations != null
                ? _curDurations[_curFrameIdx]
                : 1f / _curFps;
            if (_accumTime < frameDur) return;

            // 一次跳一帧（避免大 dt 时跳多帧造成动画失真，宁可漏帧也保线性）
            _accumTime -= frameDur;
            _curFrameIdx++;
            if (_curFrameIdx >= _curFrames.Length)
            {
                if (_looping)
                {
                    _curFrameIdx = 0;
                }
                else
                {
                    // 一次性动画播完：attack 等回到持续态（idle/walk）；die 停在末帧（尸体）
                    _curFrameIdx = _curFrames.Length - 1;
                    _playing = false;
                    _sr.sprite = _curFrames[_curFrameIdx];
                    if (_curState != "die" && _curState != _restState
                        && _restFrames != null && _restFrames.Length > 0)
                    {
                        if (_restDurations != null)
                            PlayTimed(_restState, _restFrames, _restDurations, true, _restOnFrameEnter);
                        else
                            Play(_restState, _restFrames, _restFps, true);
                    }
                    return;
                }
            }
            EnterFrame(_curFrameIdx);
        }
    }
}
