using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Utils;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 飘字管理器（单例 MonoBehaviour）。
    ///
    /// 表现层底层服务（CLAUDE.md §1.1）：仅 Spawn(worldX, worldY, value, type)，
    /// 不判断"何时该 spawn"（业务由 Lua 调）。
    ///
    /// 设计要点：
    ///   - TMP World Canvas 池 150（GameConfig.damage_num_pool_size）
    ///   - KMB 格式化（999/1.5K/1.5M）
    ///   - 寿命 0.6s（GameConfig.damage_num_life）
    ///   - 同 cell 0.05s 内 ≥2 字 X 错开 ±20px
    ///
    /// 0 [SerializeField]：池大小、寿命都从 GameConfig.txt 读；
    /// World Canvas 启动时自动创建（不放场景）。
    ///
    /// 注意：MVP 阶段不强依赖 TextMeshPro（com.unity.textmeshpro 包不一定就位）。
    /// 优先用 TMP，回退到 Unity 内置 UI.Text/3D TextMesh。
    ///
    /// type:
    ///   0 = phys_white        普通物理伤害（白 28px）
    ///   1 = magic_purple      魔法伤害（紫 28px）
    ///   2 = crit_gold_40px    暴击（金 40px）
    ///   3 = heal_green        治疗（绿）
    ///   4 = miss_grey         未命中"MISS"（灰）
    ///   5 = elem_fire         火属性（橙红）
    ///   6 = elem_water        水属性（蓝青）
    ///   7 = elem_thunder      雷属性（亮黄）
    ///   8 = elem_poison_dot   毒伤害（深绿）
    /// </summary>
    public class DamageNumberManager : MonoBehaviour
    {
        // ============ 单例 ============
        private static DamageNumberManager _instance;
        public static DamageNumberManager Instance
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
            var go = new GameObject("[DamageNumberManager]");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DamageNumberManager>();
        }

        // ============ 配置 ============
        private int _poolSize = 150;
        private float _lifeSeconds = 0.6f;
        private const float RISE_DISTANCE_WORLD = 0.6f;   // 上飘 60px / PPU=100 = 0.6 world
        private const float JITTER_WINDOW_SECONDS = 0.05f;
        private const float JITTER_OFFSET_WORLD = 0.2f;   // ±20px

        // ============ 池 ============
        private Queue<DamageNumberView> _pool;
        private List<DamageNumberView> _active;
        private Canvas _worldCanvas;
        private Transform _canvasTransform;

        // ============ 同 cell 错开抖动跟踪 ============
        // key = "{xRound}_{yRound}" (10px 取整) → (lastTime, count)
        private struct CellJitter { public float lastTime; public int count; }
        private readonly Dictionary<long, CellJitter> _jitterTrack = new Dictionary<long, CellJitter>();

        // ============ 颜色 + 字号 配置（type → color, size） ============
        private static readonly Color[] TYPE_COLORS = new Color[]
        {
            new Color(1f, 1f, 1f, 1f),               // 0 white
            new Color(0.7f, 0.4f, 1f, 1f),           // 1 magic purple
            new Color(1f, 0.84f, 0.2f, 1f),          // 2 crit gold
            new Color(0.4f, 1f, 0.4f, 1f),           // 3 heal green
            new Color(0.6f, 0.6f, 0.6f, 1f),         // 4 miss grey
            new Color(1f, 0.5f, 0.2f, 1f),           // 5 elem fire
            new Color(0.3f, 0.8f, 1f, 1f),           // 6 elem water
            new Color(1f, 1f, 0.4f, 1f),             // 7 elem thunder
            new Color(0.3f, 0.7f, 0.3f, 1f),         // 8 elem poison
            new Color(0.25f, 1f, 0.3f, 1f),          // 9 dealt 亮绿（造成伤害方）
            new Color(1f, 0.25f, 0.25f, 1f),         // 10 taken 红（被击方）
        };
        private static readonly int[] TYPE_SIZES = new int[] { 28, 28, 40, 28, 24, 28, 28, 28, 24, 30, 30 };

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
            LoadConfig();
            BuildCanvas();
            BuildPool();
        }

        private void LoadConfig()
        {
            try
            {
                var cm = ConfigManager.Instance;
                cm.LoadIfNeeded();
                var r1 = cm.GetTableInfo("GameConfig", "key", "damage_num_pool_size");
                if (r1 != null) _poolSize = cm.GetValue<int>(r1, "value", 150);
                var r2 = cm.GetTableInfo("GameConfig", "key", "damage_num_life");
                if (r2 != null) _lifeSeconds = cm.GetValue<float>(r2, "value", 0.6f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DamageNumberManager] 读 GameConfig 失败，沿用默认: {e.Message}");
            }
        }

        private void BuildCanvas()
        {
            // World Space Canvas（位于场景中央，飘字位置由 Spawn 时设）
            var cv = new GameObject("[DamageNumberCanvas]");
            cv.transform.SetParent(transform, false);
            _worldCanvas = cv.AddComponent<Canvas>();
            _worldCanvas.renderMode = RenderMode.WorldSpace;
            _worldCanvas.sortingLayerName = HDSortingLayers.UIWorld;
            _worldCanvas.sortingOrder = 200; // 高于战场单位
            cv.AddComponent<UnityEngine.UI.CanvasScaler>();
            _canvasTransform = cv.transform;
        }

        private void BuildPool()
        {
            _pool = new Queue<DamageNumberView>(_poolSize);
            _active = new List<DamageNumberView>(_poolSize);
            for (int i = 0; i < _poolSize; i++)
            {
                var view = DamageNumberView.CreateNew(_canvasTransform);
                view.Hide();
                _pool.Enqueue(view);
            }
        }

        // ============ 主入口 ============
        public void Spawn(float worldX, float worldY, int value, int type)
        {
            // type 索引 clamp
            if (type < 0 || type >= TYPE_COLORS.Length) type = 0;

            // 同 cell 错开（10px 取整 = 0.1 world）
            float jitterX = ApplyCellJitter(worldX, worldY);

            // 池取
            DamageNumberView view;
            if (_pool.Count > 0)
            {
                view = _pool.Dequeue();
            }
            else
            {
                // 池满 → 拿最早的 active 强制回收（不爆栈）
                if (_active.Count > 0)
                {
                    view = _active[0];
                    _active.RemoveAt(0);
                }
                else
                {
                    view = DamageNumberView.CreateNew(_canvasTransform);
                }
            }

            view.Show(worldX + jitterX, worldY, FormatKMB(value, type), TYPE_COLORS[type], TYPE_SIZES[type], _lifeSeconds, RISE_DISTANCE_WORLD);
            _active.Add(view);
        }

        // ============ 同 cell 错开：返回应在 X 上额外加的偏移 ============
        private float ApplyCellJitter(float wx, float wy)
        {
            // 10px = 0.1 world 网格归并
            int kx = Mathf.RoundToInt(wx * 10f);
            int ky = Mathf.RoundToInt(wy * 10f);
            long key = ((long)kx << 32) | (uint)ky;

            float now = Time.time;
            if (_jitterTrack.TryGetValue(key, out var prev))
            {
                if (now - prev.lastTime < JITTER_WINDOW_SECONDS)
                {
                    int n = prev.count + 1;
                    _jitterTrack[key] = new CellJitter { lastTime = now, count = n };
                    // 交替 +20 / -20 / +40 / -40 ...
                    bool pos = (n & 1) == 1;
                    int magnitude = (n + 1) / 2;
                    return (pos ? 1f : -1f) * JITTER_OFFSET_WORLD * magnitude;
                }
            }
            _jitterTrack[key] = new CellJitter { lastTime = now, count = 1 };
            return 0f;
        }

        // ============ KMB 格式化 ============
        private string FormatKMB(int v, int type)
        {
            if (type == 4) return "MISS";

            int absV = Mathf.Abs(v);
            string sign = (v < 0) ? "-" : (type == 3 ? "+" : "");
            if (absV >= 1000000) return sign + (absV / 1000000f).ToString("F1") + "M";
            if (absV >= 1000)    return sign + (absV / 1000f).ToString("F1") + "K";
            return sign + absV.ToString();
        }

        // ============ 每帧 update active list ============
        private void Update()
        {
            if (_active == null) return; // 重复 Awake 被 Destroy 的副本，初始化未跑完
            float dt = Time.deltaTime;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var view = _active[i];
                if (view == null || view.IsDead)
                {
                    _active.RemoveAt(i);
                    if (view != null)
                    {
                        view.Hide();
                        if (_pool.Count < _poolSize) _pool.Enqueue(view);
                    }
                    continue;
                }
                view.Tick(dt);
            }
        }
    }

    // ===============================================================
    // DamageNumberView：单条飘字 view（不依赖 TMP，沿用 Unity 内置 TextMesh）
    // ===============================================================
    public class DamageNumberView
    {
        public GameObject GO { get; private set; }
        private TextMesh _text;
        private Transform _tr;
        private float _life;
        private float _maxLife;
        private float _riseDistance;
        private Vector3 _origin;

        public bool IsDead => _life >= _maxLife;

        public static DamageNumberView CreateNew(Transform parent)
        {
            var go = new GameObject("DamageNum");
            go.transform.SetParent(parent, false);

            var tm = go.AddComponent<TextMesh>();
            tm.text = "";
            tm.fontSize = 28;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.05f; // 缩放小一些避免过大
            tm.color = Color.white;

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sortingLayerName = HDSortingLayers.UIWorld;
                mr.sortingOrder = 210;
            }

            var view = new DamageNumberView
            {
                GO = go,
                _text = tm,
                _tr = go.transform,
            };
            return view;
        }

        public void Show(float worldX, float worldY, string text, Color color, int size, float life, float riseDistance)
        {
            GO.SetActive(true);
            _origin = new Vector3(worldX, worldY, 0f);
            _tr.position = _origin;
            _text.text = text;
            _text.fontSize = size;
            _text.color = new Color(color.r, color.g, color.b, 1f);
            _life = 0f;
            _maxLife = life;
            _riseDistance = riseDistance;
        }

        public void Hide()
        {
            if (GO != null) GO.SetActive(false);
        }

        public void Tick(float dt)
        {
            _life += dt;
            float p = Mathf.Clamp01(_life / _maxLife);
            // 上飘 + alpha 衰减
            _tr.position = _origin + new Vector3(0f, _riseDistance * p, 0f);
            var c = _text.color;
            // 前 60% 时间满 alpha，后 40% lerp 0
            float alpha = (p < 0.6f) ? 1f : (1f - (p - 0.6f) / 0.4f);
            _text.color = new Color(c.r, c.g, c.b, alpha);
        }
    }
}

