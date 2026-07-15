using UnityEngine;
using UnityEngine.EventSystems;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 拖拽原始 input 桥（C# 仅做"长按 / 位移检测"性能/兼容层，业务全在 Lua）。
    ///
    /// 阈值（GameConfig.txt）：
    ///   - drag_press_threshold_ms = 180   长按多少毫秒后启动拖拽
    ///   - drag_move_threshold_px  = 8     按下后位移多少 px 立即短路启动拖拽（不必等满 180ms）
    ///
    /// 输出（调 Lua 全局函数）：
    ///   - Battle_OnDragBegin(srcId, srcKind, sx, sy)
    ///   - Battle_OnDragMove(sx, sy)
    ///   - Battle_OnDragEnd(sx, sy)
    ///
    /// 业务的 srcId / srcKind（手卡 / 战场单位 / 解锁卡）通过 SetSource 由业务挂载层（如 inventory_view）注入；
    /// 若 srcKind 为空 → 仍触发拖拽，但 srcId 为 0（Lua 业务自行处理）。
    ///
    /// 0 SerializeField。本组件挂在"可被拖拽"的 GO 上（卡片预制体 / 战场单位）。
    /// 真鼠标 + 真触屏统一走 PointerEventData（CLAUDE.md hotfix-3：QA 不可只用 ExecuteEvents 自测）。
    /// </summary>
    public class DragInputBridge : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler
    {
        // ============ 配置 ============
        // R3a (2026-06-11)：阈值/手势状态机迁入共享 GestureArbiter（tap/双击/拖拽 与 UnitView 同一套），
        // 本类只剩"EventSystem 事件转发 + 业务来源信息 + Lua 拖拽回调"。
        public static void EnsureConfig()
        {
            GestureArbiter.EnsureConfig();
        }

        // ============ 业务挂载注入的来源信息 ============
        // 业务层（如 inventory_view.lua → C# Component 挂卡片时）用 SetSource 告诉桥这是哪张卡
        public long SrcId { get; set; }
        public string SrcKind { get; set; } // "inventory" / "field" / "unlock_card"
        public string SrcIdStr { get; set; } // Round 6 — unlock_card 用 card_id string（SrcId long 存不下）

        public void SetSource(long srcId, string srcKind)
        {
            SrcId = srcId;
            SrcKind = srcKind ?? "";
            SrcIdStr = null;
        }

        // Round 6 — unlock_card 卡片用 card_id (string) 作为 SrcId
        public void SetSourceWithStringId(string srcIdStr, string srcKind)
        {
            SrcId = 0;
            SrcIdStr = srcIdStr ?? "";
            SrcKind = srcKind ?? "";
        }

        // ============ 手势状态机（R3a：共享 GestureArbiter） ============
        private GestureArbiter _gesture;

        private string EffectiveSrcId()
        {
            // Round 6 — unlock_card 优先用 SrcIdStr（card_id string）；其它用 long SrcId.ToString
            return !string.IsNullOrEmpty(SrcIdStr) ? SrcIdStr : SrcId.ToString();
        }

        private void Awake()
        {
            EnsureConfig();
            _gesture = new GestureArbiter(this);
            _gesture.DragStarted = p => CallLuaOnDragBegin(EffectiveSrcId(), SrcKind ?? "", p.x, p.y);
            _gesture.DragMoved = p => CallLuaOnDragMove(p.x, p.y);
            _gesture.DragEnded = p => CallLuaOnDragEnd(p.x, p.y);
            _gesture.Tapped = p => GestureArbiter.CallLuaTap(EffectiveSrcId(), SrcKind ?? "", p.x, p.y);
            _gesture.DoubleTapped = p => GestureArbiter.CallLuaDoubleTap(EffectiveSrcId(), SrcKind ?? "", p.x, p.y);
        }

        public void OnPointerDown(PointerEventData e) { _gesture.PointerDown(e.position, e.pointerId); }

        // Unity EventSystem 位移超自带阈值才调 OnBeginDrag → 算位移短路路径
        public void OnBeginDrag(PointerEventData e) { _gesture.PointerMove(e.position, e.pointerId, viaMoveShortcut: true); }

        public void OnDrag(PointerEventData e) { _gesture.PointerMove(e.position, e.pointerId); }

        public void OnEndDrag(PointerEventData e) { _gesture.PointerUp(e.position, e.pointerId); }

        public void OnPointerUp(PointerEventData e) { _gesture.PointerUp(e.position, e.pointerId); }

        /// <summary>Update 轮询长按（兜底：按下后不动 OnDrag 不会触发）→ 按住即拖。</summary>
        private void Update()
        {
            _gesture.Tick();
        }

        // ============ Lua 回调（xLua delegate 坑：从 _env 拿 LuaFunction 调用） ============

        private static void CallLuaOnDragBegin(string srcIdStr, string srcKind, float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Battle_OnDragBegin");
                if (fn != null)
                {
                    fn.Call(srcIdStr, srcKind, sx, sy);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DragInputBridge] Battle_OnDragBegin 失败: {e.Message}");
            }
#endif
        }

        private static void CallLuaOnDragMove(float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Battle_OnDragMove");
                if (fn != null)
                {
                    fn.Call(sx, sy);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DragInputBridge] Battle_OnDragMove 失败: {e.Message}");
            }
#endif
        }

        private static void CallLuaOnDragEnd(float sx, float sy)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>("Battle_OnDragEnd");
                if (fn != null)
                {
                    fn.Call(sx, sy);
                    fn.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DragInputBridge] Battle_OnDragEnd 失败: {e.Message}");
            }
#endif
        }
    }
}

