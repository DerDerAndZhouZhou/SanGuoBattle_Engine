using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 打击四件套静态服务：闪白 / Hit-Stop / 屏震 / 击退。
    ///
    /// 设计原则（CLAUDE.md §1.1）：
    ///   - 表现层底层服务（不写"何时该播"业务，仅播放）
    ///   - 业务 Lua 通过 LuaHost 暴露的全局函数 HitFeedback_Play(handle, type) 调用
    ///   - 0 [SerializeField]：所有数值来自 GameConfig.txt
    ///
    /// type:
    ///   0 = hit_normal       → 闪白 80ms
    ///   1 = hit_crit         → 闪白 80ms + Hit-Stop 50ms + 屏震 ±5px×150ms
    ///   2 = hit_die          → 闪白 80ms + 击退 10px×100ms
    ///   3 = screen_shake_global  → 仅屏震（拖拽红色态、Boss 入场等）
    ///
    /// 句柄表：Agent C 的 BattleBridge.Battle_SpawnUnit 会调用 RegisterHandle 把 handle→GO 注册进来；
    ///         本文件独立可编译，不直接依赖 Agent C 未就位 API（运行期才查表）。
    /// </summary>
    public static class HitFeedback
    {
        // ============ 句柄表（独立维护，不依赖 Agent C 文件就位） ============
        private static readonly Dictionary<long, GameObject> _handles = new Dictionary<long, GameObject>();

        /// <summary>由 Battle 框架（如 BattleBridge.Battle_SpawnUnit）登记 handle → GO。</summary>
        public static void RegisterHandle(long handle, GameObject go)
        {
            if (handle == 0 || go == null) return;
            _handles[handle] = go;
        }

        /// <summary>由 Battle 框架（如 BattleBridge.Battle_DespawnUnit）撤销 handle。</summary>
        public static void UnregisterHandle(long handle)
        {
            _handles.Remove(handle);
        }

        public static GameObject GetHandleGO(long handle)
        {
            return _handles.TryGetValue(handle, out var go) ? go : null;
        }

        // ============ 配置缓存（首次调用时从 GameConfig.txt 读，避免每帧 lookup） ============
        private static bool _cfgLoaded;
        private static float _hitFlashDuration = 0.08f;
        private static int _hitStopFrames = 3;
        private static float _screenShakeAmp = 5f;     // px
        private static float _screenShakeDur = 0.15f;
        private static float _knockbackDistance = 10f; // px
        private static float _knockbackDur = 0.1f;

        private static void EnsureConfig()
        {
            if (_cfgLoaded) return;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return;
                cm.LoadIfNeeded();
                _hitFlashDuration   = ReadFloat(cm, "hit_flash_duration",   0.08f);
                _hitStopFrames      = ReadInt  (cm, "hit_stop_frames",      3);
                _screenShakeAmp     = ReadFloat(cm, "screen_shake_amp",     5f);
                _screenShakeDur     = ReadFloat(cm, "screen_shake_dur",     0.15f);
                _knockbackDistance  = ReadFloat(cm, "knockback_distance",   10f);
                _knockbackDur       = ReadFloat(cm, "knockback_dur",        0.1f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HitFeedback] 读 GameConfig 失败，沿用默认值: {e.Message}");
            }
            _cfgLoaded = true;
        }

        private static float ReadFloat(ConfigManager cm, string key, float def)
        {
            var row = cm.GetTableInfo("GameConfig", "key", key);
            if (row == null) return def;
            return cm.GetValue<float>(row, "value", def);
        }
        private static int ReadInt(ConfigManager cm, string key, int def)
        {
            var row = cm.GetTableInfo("GameConfig", "key", key);
            if (row == null) return def;
            return cm.GetValue<int>(row, "value", def);
        }

        // ============ runner（隐藏的 MonoBehaviour 协程载体） ============
        private class FeedbackRunner : MonoBehaviour { }
        private static FeedbackRunner _runner;
        private static FeedbackRunner GetRunner()
        {
            if (_runner != null) return _runner;
            var go = new GameObject("[HitFeedbackRunner]");
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<FeedbackRunner>();
            return _runner;
        }

        // ============ Shader uniform ID（避免每次 Shader.PropertyToID 开销） ============
        private static readonly int FlashColorID = Shader.PropertyToID("_FlashColor");
        private static readonly int FlashAmountID = Shader.PropertyToID("_FlashAmount");
        // R5/F3 (2026-06-11)：闪白改走 MaterialPropertyBlock（sr.material 每次访问实例化材质 →
        // 与 Battle_SetEnemyHsv 共享材质的方案冲突且逐怪 DrawCall）。单线程复用一个 MPB。
        private static MaterialPropertyBlock _flashMpb;

        // ============ 主入口 ============
        public static void Play(long handle, int type)
        {
            EnsureConfig();
            var runner = GetRunner();

            // type=3 全局屏震不需要 handle
            if (type == 3)
            {
                runner.StartCoroutine(CoScreenShake(_screenShakeAmp, _screenShakeDur));
                return;
            }

            var go = GetHandleGO(handle);
            if (go == null)
            {
                // 业务 Lua 早期或单位已死，silent return 减少 log spam
                return;
            }

            // 1. 闪白（所有 type 0/1/2 都走）
            runner.StartCoroutine(CoFlash(go, _hitFlashDuration));

            // 2. Hit-Stop（仅 type=1 暴击）
            if (type == 1)
            {
                runner.StartCoroutine(CoHitStop(go, _hitStopFrames));
                runner.StartCoroutine(CoScreenShake(_screenShakeAmp, _screenShakeDur));
            }

            // 3. 击退（仅 type=2 死亡：怪受击向后位移）
            if (type == 2)
            {
                // 默认向 +X 方向击退（怪从右→左，被击退即向右）；
                // 业务可后续在 BattleBridge 上扩出方向参数，这里 MVP 实现先走 +X
                runner.StartCoroutine(CoKnockback(go, Vector3.right, _knockbackDistance, _knockbackDur));
            }
        }

        // ============ 协程 1：闪白 ============
        private static IEnumerator CoFlash(GameObject go, float duration)
        {
            if (go == null) yield break;

            var sr = go.GetComponentInChildren<SpriteRenderer>();
            if (sr == null) yield break;

            // 走 shader uniform（要求 material 含 _FlashAmount，即 HitFlash/SpriteHsvShift shader）；
            // 不支持则直接结束（默认 sprite shader 无此 uniform，旧实现同样无视觉效果）。
            // R5/F3：sharedMaterial 判定 + MPB 写参数，绝不触碰 sr.material（避免实例化）。
            bool hasShaderUniform = sr.sharedMaterial != null && sr.sharedMaterial.HasProperty(FlashAmountID);
            if (!hasShaderUniform) yield break;

            if (_flashMpb == null) _flashMpb = new MaterialPropertyBlock();
            // 先 Get 再改再 Set：保留 sprite _MainTex 与 HSV 暗化参数
            sr.GetPropertyBlock(_flashMpb);
            _flashMpb.SetColor(FlashColorID, Color.white);
            _flashMpb.SetFloat(FlashAmountID, 1f);
            sr.SetPropertyBlock(_flashMpb);

            float t = 0f;
            while (t < duration && go != null)
            {
                if (BattleBridge.BattlePaused) { yield return null; continue; }  // 暂停冻结
                if (sr == null) yield break;
                float a = 1f - (t / duration);
                sr.GetPropertyBlock(_flashMpb);
                _flashMpb.SetFloat(FlashAmountID, a);
                sr.SetPropertyBlock(_flashMpb);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (sr != null)
            {
                sr.GetPropertyBlock(_flashMpb);
                _flashMpb.SetFloat(FlashAmountID, 0f);
                sr.SetPropertyBlock(_flashMpb);
            }
        }

        // ============ 协程 2：Hit-Stop（暂停目标动画 N 帧） ============
        private static IEnumerator CoHitStop(GameObject go, int frames)
        {
            if (go == null) yield break;

            // MVP：暂停 SpriteAnimator + Animator（如有）；用本地 timeScale 替代而非全局 Time.timeScale
            // Agent C 的 SpriteAnimator 暂未就位 → 走 SendMessage（如不存在 silent）
            var hold = go.GetComponent<MonoBehaviour>();
            go.SendMessage("OnHitStopBegin", SendMessageOptions.DontRequireReceiver);

            for (int i = 0; i < frames; )
            {
                if (!BattleBridge.BattlePaused) i++;  // 暂停时不推进帧计数
                yield return null;
            }

            if (go != null) go.SendMessage("OnHitStopEnd", SendMessageOptions.DontRequireReceiver);
        }

        // ============ 协程 3：屏震（camera local-pos shake） ============
        private static bool _isShaking;
        private static IEnumerator CoScreenShake(float ampPx, float duration)
        {
            // 防同一帧多次重入加幅
            if (_isShaking) yield break;
            _isShaking = true;

            var cam = Camera.main;
            if (cam == null) { _isShaking = false; yield break; }

            var origin = cam.transform.localPosition;
            // px → world：PxToWorld.PPU = 100
            float ampWorld = ampPx / 100f;

            float t = 0f;
            while (t < duration)
            {
                if (cam == null) break;
                if (BattleBridge.BattlePaused) { yield return null; continue; }  // 暂停冻结
                // 衰减 + 随机偏移
                float decay = 1f - (t / duration);
                float offsetX = (Random.value * 2f - 1f) * ampWorld * decay;
                float offsetY = (Random.value * 2f - 1f) * ampWorld * decay;
                cam.transform.localPosition = origin + new Vector3(offsetX, offsetY, 0f);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (cam != null) cam.transform.localPosition = origin;
            _isShaking = false;
        }

        // ============ 协程 4：击退（目标 transform.position lerp 出去再回） ============
        private static IEnumerator CoKnockback(GameObject go, Vector3 dir, float distancePx, float duration)
        {
            if (go == null) yield break;

            var tr = go.transform;
            var origin = tr.position;
            float distWorld = distancePx / 100f; // px → world

            // 一次性脉冲：origin → origin + dir*distance → origin（前 50% 出，后 50% 回）
            float t = 0f;
            while (t < duration && go != null)
            {
                if (BattleBridge.BattlePaused) { yield return null; continue; }  // 暂停冻结
                float p = t / duration;
                float lerp = (p < 0.5f) ? (p * 2f) : (2f - p * 2f);
                if (tr != null) tr.position = origin + dir * (distWorld * lerp);
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            if (tr != null) tr.position = origin;
        }
    }
}
