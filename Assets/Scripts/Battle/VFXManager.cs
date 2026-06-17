using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Engine.Host;
using HeroDefense.Utils;

namespace HeroDefense.Battle
{
    /// <summary>
    /// VFX 管理器（单例 MonoBehaviour）。
    ///
    /// 表现层底层服务（CLAUDE.md §1.1）：
    ///   - 仅 Play(vfxKey, x, y, duration) / PlayOnUnit(handle, vfxKey)
    ///   - 业务 Lua 决定"何时播什么 vfx"
    ///
    /// v3 实现路线（com.unity.modules.particlesystem 已被裁剪）：
    ///   - **sprite 序列帧 + 自定义池**（不用 ParticleSystem）
    ///   - 每个 vfx_key 一个 prefab（在 Resources/vfx/<key>.prefab 或 Game/resources/art/vfx/<key>.png 序列）
    ///   - prefab 上挂 sprite 序列帧动画组件（Agent C 的 SpriteAnimator 会承载，或本类内置简易播放器）
    ///   - 自动回收：duration 到期 / SpriteAnimator OnFinish 触发
    ///
    /// 限制：
    ///   - 总活动 VFX ≤ vfx_max_total_particles（默认 1000）
    ///   - 单 vfx_key 池上限 = 20（避免单一资源霸占）
    ///
    /// 配置来源 vfx.txt：
    ///   key, name, prefab_key, duration, loop, sort_layer
    /// </summary>
    public class VFXManager : MonoBehaviour
    {
        private static VFXManager _instance;
        public static VFXManager Instance
        {
            get
            {
                if (_instance == null) EnsureInstance();
                return _instance;
            }
        }

        public static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("[VFXManager]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VFXManager>();
        }

        // ============ 配置 ============
        private int _maxTotalActive = 1000;
        private const int PER_KEY_POOL_LIMIT = 20;
        private const float VFX_LOOP_FPS = 12f;   // loop 型 vfx 帧序列播放帧率（非 loop 按 duration 均摊）

        // ============ 池：每个 vfx_key 一个队列 ============
        private readonly Dictionary<string, Queue<GameObject>> _pools
            = new Dictionary<string, Queue<GameObject>>();

        // v2 批 0（2026-06-13）：vfx 帧序列缓存（按帧前缀；resources/art/vfx/<prefix>_<i>.png 探测到首个缺失为止）。
        private readonly Dictionary<string, Sprite[]> _frameCache = new Dictionary<string, Sprite[]>();
        private static readonly Sprite[] _emptyFrames = new Sprite[0];

        // 活动列表（跟踪生命周期）
        private class ActiveVFX
        {
            public int Id;                // v2 批 1b：实例自增 id（StopById 定点停用，cell 预警圈用）
            public GameObject GO;
            public string Key;
            public float Life;
            public float MaxLife;
            public bool Loop;
            public long FollowHandle;     // 0 = 不跟随
            public Vector3 OriginOffset;  // 跟随时相对 unit 的偏移
            public Sprite[] Frames;       // v2 批 0：帧序列（空=走旧 prefab 路线，无帧动画）
            public SpriteRenderer SR;     // 帧序列渲染目标
            public int LastFrameIdx;      // 上次设置的帧下标（避免每帧重复赋值）
        }
        private readonly List<ActiveVFX> _active = new List<ActiveVFX>();

        // v2 批 1b（2026-06-14）：活动 vfx 实例自增 id（>0；Play 返回，StopById 用）。
        private int _vfxIdCounter;
        private int NextVfxId() => ++_vfxIdCounter;

        // 统计
        private int _rejectCount;
        public int ActiveCount => _active.Count;
        public int RejectCount => _rejectCount;

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", "vfx_max_total_particles");
                if (row != null) _maxTotalActive = cm.GetValue<int>(row, "value", 1000);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[VFXManager] 读 GameConfig 失败，沿用默认: {e.Message}");
            }
        }

        // ============ 主入口 1：固定位置播 vfx ============
        // v2 批 1b（2026-06-14）：返回实例 id（>0 成功；0 = 被拒/失败）。loop 型落点预警圈用 StopById(id) 定点停。
        public int Play(string vfxKey, float worldX, float worldY, float durationOverride)
        {
            if (string.IsNullOrEmpty(vfxKey)) return 0;
            if (_active.Count >= _maxTotalActive)
            {
                _rejectCount++;
                if ((_rejectCount % 50) == 1) // 每 50 次 log 1 次避免刷屏
                    Debug.LogWarning($"[VFXManager] active >= {_maxTotalActive}, reject {vfxKey} (total reject={_rejectCount})");
                return 0;
            }

            var (prefabKey, duration, loop, sortLayer) = LookupVFX(vfxKey);
            float life = (durationOverride > 0f) ? durationOverride : duration;
            // duration=0 + loop=true 视为常驻（life=∞），需上层手动 Stop
            if (life <= 0f && !loop) life = 1f; // 防 0 寿命卡死

            var frames = LoadVfxFrames(ResolveFramePrefix(vfxKey));   // v2 批 0
            var go = AcquireFromPool(vfxKey, prefabKey);
            if (go == null) return 0;

            var sr = ApplyFirstFrame(go, frames);
            ConfigureSorting(go, sortLayer);
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(worldX, worldY, 0f);
            go.SetActive(true);

            int id = NextVfxId();
            _active.Add(new ActiveVFX
            {
                Id = id,
                GO = go,
                Key = vfxKey,
                Life = 0f,
                MaxLife = life,
                Loop = loop,
                FollowHandle = 0,
                Frames = frames,
                SR = sr,
                LastFrameIdx = -1,
            });
            return id;
        }

        // ============ 主入口 2：跟随单位播 vfx ============
        // v2 批 1b（2026-06-14）：加可选 durationOverride（>0 覆盖 vfx.tab.duration；cast_vfx 用 cast_time 作时长，
        //   让吟唱特效随读条时长收尾）。0/默认 = 用配置 duration（旧调用零行为变化）。
        public void PlayOnUnit(long handle, string vfxKey, float durationOverride = 0f)
        {
            if (string.IsNullOrEmpty(vfxKey)) return;
            var unitGO = HitFeedback.GetHandleGO(handle);
            if (unitGO == null) return;

            if (_active.Count >= _maxTotalActive)
            {
                _rejectCount++;
                return;
            }

            var (prefabKey, duration, loop, sortLayer) = LookupVFX(vfxKey);
            float life = (durationOverride > 0f) ? durationOverride : duration;
            if (life <= 0f && !loop) life = 1f;

            var frames = LoadVfxFrames(ResolveFramePrefix(vfxKey));   // v2 批 0
            var go = AcquireFromPool(vfxKey, prefabKey);
            if (go == null) return;

            var sr = ApplyFirstFrame(go, frames);
            ConfigureSorting(go, sortLayer);
            go.transform.SetParent(unitGO.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.SetActive(true);

            _active.Add(new ActiveVFX
            {
                Id = NextVfxId(),
                GO = go,
                Key = vfxKey,
                Life = 0f,
                MaxLife = life,
                Loop = loop,
                FollowHandle = handle,
                OriginOffset = Vector3.zero,
                Frames = frames,
                SR = sr,
                LastFrameIdx = -1,
            });
        }

        // ============ 查 vfx.txt：返回 (prefab_key, duration, loop, sort_layer) ============
        private (string prefabKey, float duration, bool loop, int sortLayer) LookupVFX(string vfxKey)
        {
            try
            {
                var cm = ConfigManager.Instance;
                var row = cm.GetTableInfo("vfx", "id", vfxKey);
                if (row != null)
                {
                    string prefab = cm.GetValue<string>(row, "prefab_key", "vfx/default");
                    float dur = cm.GetValue<float>(row, "duration", 1.0f);
                    bool loop = cm.GetValue<bool>(row, "loop", false);
                    int sort = cm.GetValue<int>(row, "sort_layer", 100);
                    return (prefab, dur, loop, sort);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[VFXManager] LookupVFX {vfxKey} 失败: {e.Message}");
            }
            return ("vfx/default", 1.0f, false, 100);
        }

        // ============ 从池取 GO（找不到则实例化 prefab 或建占位） ============
        private GameObject AcquireFromPool(string vfxKey, string prefabKey)
        {
            if (!_pools.TryGetValue(vfxKey, out var queue))
            {
                queue = new Queue<GameObject>();
                _pools[vfxKey] = queue;
            }

            if (queue.Count > 0)
            {
                return queue.Dequeue();
            }

            // 池空 → 加载 prefab 实例化（走 LuaHost.LoadPrefab，可跑 Resources / 后续 AssetBundle）
            var prefab = LuaHost.LoadPrefab(prefabKey);
            GameObject go;
            if (prefab != null)
            {
                go = Instantiate(prefab);
            }
            else
            {
                // 占位 GO：一个空的 SpriteRenderer，避免空指针 + 让业务 log 看到
                go = new GameObject($"VFX_{vfxKey}_placeholder");
                go.AddComponent<SpriteRenderer>();
            }
            go.name = $"VFX_{vfxKey}";
            return go;
        }

        // ============ 设置 sort_layer（如果 prefab 自带 SpriteRenderer） ============
        private void ConfigureSorting(GameObject go, int sortLayer)
        {
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < srs.Length; i++)
            {
                srs[i].sortingLayerName = HDSortingLayers.VFX;
                srs[i].sortingOrder = sortLayer;
            }
        }

        // ============ Update：帧序列推进 + 寿命到期回池 ============
        // dt 用 Time.deltaTime（受 timeScale 缩放）：BattlePaused 时 timeScale=0 → dt=0 → vfx 帧与寿命随全场冻结。
        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var vfx = _active[i];
                if (vfx == null || vfx.GO == null)
                {
                    _active.RemoveAt(i);
                    continue;
                }

                vfx.Life += dt;

                // v2 批 0：帧序列推进（有帧才动；空帧=旧 prefab 路线，跳过）
                if (vfx.Frames != null && vfx.Frames.Length > 0 && vfx.SR != null)
                    AdvanceFrame(vfx);

                // loop=true 走常驻，不主动回收（业务负责显式 Stop / StopOnUnit）
                if (!vfx.Loop && vfx.Life >= vfx.MaxLife)
                {
                    ReturnToPool(vfx);
                    _active.RemoveAt(i);
                }
            }
        }

        // 帧下标：非 loop 按寿命进度均摊（duration 内播完一遍）；loop 按固定帧率循环。
        private void AdvanceFrame(ActiveVFX vfx)
        {
            int count = vfx.Frames.Length;
            int idx;
            if (vfx.Loop)
                idx = ((int)(vfx.Life * VFX_LOOP_FPS)) % count;
            else
            {
                float p = (vfx.MaxLife > 0.001f) ? (vfx.Life / vfx.MaxLife) : 1f;
                idx = Mathf.Clamp((int)(p * count), 0, count - 1);
            }
            if (idx != vfx.LastFrameIdx)
            {
                vfx.SR.sprite = vfx.Frames[idx];
                vfx.LastFrameIdx = idx;
            }
        }

        // ============ v2 批 0：帧序列加载（替代空 prefab 路线，让 37 套存量 vfx PNG 可见） ============
        // 帧前缀 = vfx.tab id（与 resources/art/vfx/<id>_<i>.png 命名一致，37 个里 34 个直接对齐）。
        // 不用 prefab_key 列：它不可靠——vfx_qinglong_yanyue 行的 prefab_key=vfx/qinglong_yanyue 丢了 vfx_ 前缀，与帧文件名不符。
        // 3 处历史命名漂移（id ≠ 帧文件名）用别名表显式映射，零改 vfx.tab / 零改 art：
        private static readonly Dictionary<string, string> _framePrefixAlias = new Dictionary<string, string>
        {
            { "hit_white",        "hit_flash_white" },
            { "hit_crit_red",     "hit_flash_red" },
            { "wave_boss_banner", "boss_warning_banner" },
        };
        private static string ResolveFramePrefix(string vfxKey)
        {
            if (!string.IsNullOrEmpty(vfxKey) && _framePrefixAlias.TryGetValue(vfxKey, out var p)) return p;
            return vfxKey;
        }

        // 探测 resources/art/vfx/<prefix>_<i>.png 从 0 递增到首个缺失；结果缓存。无帧返回空数组（→ 走旧 prefab 占位路线）。
        private Sprite[] LoadVfxFrames(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return _emptyFrames;
            if (_frameCache.TryGetValue(prefix, out var cached)) return cached;
            var list = new List<Sprite>(8);
            for (int i = 0; i < 16; i++)
            {
                var s = LuaHost.LoadSprite($"resources/art/vfx/{prefix}_{i}.png", false);
                if (s == null) break;
                list.Add(s);
            }
            var arr = list.Count > 0 ? list.ToArray() : _emptyFrames;
            _frameCache[prefix] = arr;
            return arr;
        }

        // 确保 GO 有 SpriteRenderer 并设首帧；返回 SR（无帧时返回 GO 现有 SR，可能为 null）。
        private SpriteRenderer ApplyFirstFrame(GameObject go, Sprite[] frames)
        {
            var sr = go.GetComponent<SpriteRenderer>();
            if (frames != null && frames.Length > 0)
            {
                if (sr == null) sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = frames[0];
            }
            return sr;
        }

        // ============ v2 批 0：停掉跟随某单位的全部 vfx（loop 状态圈/光环清理；buff_runtime 回滚配对调用） ============
        public void StopOnUnit(long handle)
        {
            if (handle == 0) return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var vfx = _active[i];
                if (vfx != null && vfx.FollowHandle == handle)
                {
                    ReturnToPool(vfx);
                    _active.RemoveAt(i);
                }
            }
        }

        // ============ v2 批 1b（2026-06-14）：按实例 id 停某个 vfx（Play 返回的 id；落点预警圈 loop 型定点清理） ============
        public void StopById(int id)
        {
            if (id <= 0) return;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var vfx = _active[i];
                if (vfx != null && vfx.Id == id)
                {
                    ReturnToPool(vfx);
                    _active.RemoveAt(i);
                    return;   // id 唯一
                }
            }
        }

        private void ReturnToPool(ActiveVFX vfx)
        {
            if (vfx == null || vfx.GO == null) return;
            vfx.GO.SetActive(false);
            vfx.GO.transform.SetParent(transform, false);

            if (!_pools.TryGetValue(vfx.Key, out var queue))
            {
                queue = new Queue<GameObject>();
                _pools[vfx.Key] = queue;
            }
            if (queue.Count < PER_KEY_POOL_LIMIT)
            {
                queue.Enqueue(vfx.GO);
            }
            else
            {
                Destroy(vfx.GO); // 超过单 key 池上限直接销毁
            }
        }

        // ============ Editor / Debug 接口（可由测试 agent 调用） ============
        public void ClearAll()
        {
            foreach (var vfx in _active)
            {
                if (vfx.GO != null) Destroy(vfx.GO);
            }
            _active.Clear();
            foreach (var kv in _pools)
            {
                while (kv.Value.Count > 0)
                {
                    var g = kv.Value.Dequeue();
                    if (g != null) Destroy(g);
                }
            }
            _pools.Clear();
            _rejectCount = 0;
        }

        // ============ v2 批 1b（2026-06-14）：清全部活动 vfx（场景切换/局结算/异常兜底）。LuaHost 注册的静态包装。 ============
        public static void VFX_ClearAll()
        {
            if (_instance != null) _instance.ClearAll();
        }
    }
}
