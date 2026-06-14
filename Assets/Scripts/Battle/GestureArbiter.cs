using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 共享手势仲裁器（R3a，2026-06-11 · tech-research §3 炸弹①"输入三写"的手势统一层）。
    ///
    /// 纯手势分类状态机：按下计时 + 位移 slop + double-tap 时间窗/半径比对。
    /// DragInputBridge（背包/商店 UI 卡，EventSystem UI 路）与 UnitView（场上单位，
    /// Physics2DRaycaster 路）两路共用 —— 只在各自组件里转发 Pointer 事件进来，
    /// 分类结果经委托回调出去，杜绝两套阈值/状态机漂移。
    ///
    /// 手势语义（tech-research F1 拍板）：
    ///   - 拖拽 = 按住 ≥ drag_press_threshold_ms(180) 或 位移 > drag_move_threshold_px(8)
    ///   - tap  = 快速松手（未触发拖拽），**立即回调**（不等双击窗，详情秒弹）
    ///   - 双击 = 双 tap 间隔 ≤ double_tap_window_ms(250) 且两点距 ≤ double_tap_radius_px(20)
    ///            且同一 owner（同一张卡/同一单位）；第二击回调 DoubleTapped（业务先 ClearInspectTarget）
    ///
    /// 0 SerializeField；阈值全走 GameConfig；与 Time.timeScale 无关（realtimeSinceStartup）。
    /// </summary>
    public sealed class GestureArbiter
    {
        // ============ 阈值配置（GameConfig，进程内缓存一次） ============
        private static bool _cfgLoaded;
        private static int _pressMs = 180;
        private static float _movePx = 8f;
        private static int _dtapWindowMs = 250;
        private static float _dtapRadiusPx = 20f;

        public static void EnsureConfig()
        {
            if (_cfgLoaded) return;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm != null)
                {
                    cm.LoadIfNeeded();
                    var r1 = cm.GetTableInfo("GameConfig", "key", "drag_press_threshold_ms");
                    if (r1 != null) _pressMs = cm.GetValue<int>(r1, "value", 180);
                    var r2 = cm.GetTableInfo("GameConfig", "key", "drag_move_threshold_px");
                    if (r2 != null) _movePx = cm.GetValue<float>(r2, "value", 8f);
                    var r3 = cm.GetTableInfo("GameConfig", "key", "double_tap_window_ms");
                    if (r3 != null) _dtapWindowMs = cm.GetValue<int>(r3, "value", 250);
                    var r4 = cm.GetTableInfo("GameConfig", "key", "double_tap_radius_px");
                    if (r4 != null) _dtapRadiusPx = cm.GetValue<float>(r4, "value", 20f);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[GestureArbiter] 读 GameConfig 失败，沿用默认阈值: {e.Message}");
            }
            _cfgLoaded = true;
        }

        // ============ 分类结果回调（owner 组件在构造后赋值） ============
        public System.Action<Vector2> DragStarted;
        public System.Action<Vector2> DragMoved;
        public System.Action<Vector2> DragEnded;
        public System.Action<Vector2> Tapped;
        public System.Action<Vector2> DoubleTapped;

        // ============ 双击记忆（全局静态：跨实例比对"上一次 tap 落在谁身上"） ============
        private static object _lastTapOwner;
        private static float _lastTapTimeRT;
        private static Vector2 _lastTapPos;

        // ============ 实例状态 ============
        private readonly object _owner;
        private bool _down;
        private bool _dragging;
        private Vector2 _downPos;
        private float _downTimeRT;
        private int _activePointerId = int.MinValue;

        public bool IsDragging => _dragging;
        public bool IsDown => _down;

        public GestureArbiter(object owner)
        {
            _owner = owner;
            EnsureConfig();
        }

        /// <summary>按下。重复按下（第二指）忽略。</summary>
        public void PointerDown(Vector2 pos, int pointerId)
        {
            if (_down) return;
            _down = true;
            _dragging = false;
            _downPos = pos;
            _downTimeRT = Time.realtimeSinceStartup;
            _activePointerId = pointerId;
        }

        /// <summary>指针移动（OnDrag/OnBeginDrag 转发）。viaMoveShortcut=true 表示来自
        /// EventSystem 的 BeginDrag（已超 EventSystem 自带位移阈值 → 直接算位移短路）。</summary>
        public void PointerMove(Vector2 pos, int pointerId, bool viaMoveShortcut = false)
        {
            if (!_down || pointerId != _activePointerId) return;
            if (!_dragging)
            {
                float elapsedMs = (Time.realtimeSinceStartup - _downTimeRT) * 1000f;
                if (viaMoveShortcut || elapsedMs >= _pressMs || Vector2.Distance(pos, _downPos) > _movePx)
                {
                    StartDrag(pos);
                }
            }
            if (_dragging) DragMoved?.Invoke(pos);
        }

        /// <summary>每帧轮询（owner.Update 调）：按住不动满 press 阈值 → 启动拖拽（按住即拖/看牌）。</summary>
        public void Tick()
        {
            if (!_down || _dragging) return;
            float elapsedMs = (Time.realtimeSinceStartup - _downTimeRT) * 1000f;
            if (elapsedMs >= _pressMs) StartDrag(_downPos);
        }

        /// <summary>松手：拖拽中 → DragEnded；否则按 tap/双击分类。</summary>
        public void PointerUp(Vector2 pos, int pointerId)
        {
            if (!_down || pointerId != _activePointerId) return;
            if (_dragging)
            {
                DragEnded?.Invoke(pos);
            }
            else
            {
                ClassifyTap(pos);
            }
            Reset();
        }

        /// <summary>外部取消（组件销毁/单位回收），不发任何回调。</summary>
        public void Cancel()
        {
            Reset();
        }

        private void StartDrag(Vector2 pos)
        {
            if (_dragging) return;
            _dragging = true;
            // 拖拽启动 = 本轮交互不是 tap → 不应污染双击记忆（保持上次 tap 记录不变即可）
            DragStarted?.Invoke(pos);
        }

        private void ClassifyTap(Vector2 pos)
        {
            float now = Time.realtimeSinceStartup;
            bool isDouble = ReferenceEquals(_lastTapOwner, _owner)
                && (now - _lastTapTimeRT) * 1000f <= _dtapWindowMs
                && Vector2.Distance(pos, _lastTapPos) <= _dtapRadiusPx;
            if (isDouble)
            {
                _lastTapOwner = null;   // 双击消费掉记忆，防三击连发两次 DoubleTapped
                DoubleTapped?.Invoke(pos);
            }
            else
            {
                _lastTapOwner = _owner;
                _lastTapTimeRT = now;
                _lastTapPos = pos;
                Tapped?.Invoke(pos);   // F1：tap 立即下发（详情秒弹），双击到来时业务自行先清详情
            }
        }

        private void Reset()
        {
            _down = false;
            _dragging = false;
            _activePointerId = int.MinValue;
        }

        // ============ Lua 中继（tap/双击共用；拖拽回调各组件已有，不挪动） ============

        /// <summary>调 Lua 全局 Battle_OnTap(srcIdStr, srcKind, sx, sy)。业务（弹谁详情）全在 Lua。</summary>
        public static void CallLuaTap(string srcIdStr, string srcKind, float sx, float sy)
        {
            CallLua2("Battle_OnTap", srcIdStr, srcKind, sx, sy);
        }

        /// <summary>调 Lua 全局 Battle_OnDoubleTap(srcIdStr, srcKind, sx, sy)。收回合法性门控在 Lua。</summary>
        public static void CallLuaDoubleTap(string srcIdStr, string srcKind, float sx, float sy)
        {
            CallLua2("Battle_OnDoubleTap", srcIdStr, srcKind, sx, sy);
        }

        private static void CallLua2(string fnName, string srcIdStr, string srcKind, float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>(fnName);
                if (fn != null)
                {
                    fn.Call(srcIdStr, srcKind, sx, sy);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GestureArbiter] CallLua {fnName} 失败: {e.Message}");
            }
#endif
        }
    }
}
