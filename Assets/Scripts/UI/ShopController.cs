using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Config;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.UI
{
    /// <summary>
    /// 商场控制器（v6 重构 2026-06-02）。挂在 BattleHud 上（BattleHudController.EnsureControllers AddComponent）。
    ///
    /// v6 改动（用户拍板）：
    ///   - 常显顶部带（不再 SideBar 开关 / 不再与背包互斥）——背包已移到左侧竖条常显，顶部带腾给商场。
    ///   - 一次刷新 8 张卡，居中（COLS 5→8 + slots 容器居中）。
    ///   - 刷新费固定（Shop_GetRefreshCost 读 economy.shop_refresh_cost）。
    ///   - 买卡满背包→Lua 落网格随机空位；网格也满→Shop_State.last_error="no_space"，本控制器弹提示。
    ///
    /// 业务全在 Lua（shop_manager.lua，CLAUDE §1.1）；本控制器仅渲染 + 触发。0 SerializeField。
    /// </summary>
    public class ShopController : MonoBehaviour
    {
        GameObject _shopPanel;
        Transform _shopSlots;
        Text _refreshLabel;
        Text _toast;
        float _toastUntil;
        bool _built;
        bool _openedOnce;

        const int   COLS = 8;                         // v6：一次 8 张
        const float CELL_W = 150f, CELL_H = 200f, SPX = 10f;
        // 布局（与 InventoryController 左竖条对齐；Phase G 截图微调）
        const float INV_STRIP_W = 210f;               // 左侧背包竖条宽 → 商场带左边让出
        const float TOP_BAR_H   = 80f;                // 顶部信息栏高 → 商场带下移
        const float SHOP_H      = 230f;

        float _poll;
        const float POLL_INTERVAL = 0.3f;
        string _lastHash = "";

        // ⚠ 已迁热更 UI：商场现由 wnd_shop.xml/.lua 实现（顶部带 8 卡 + SideBar 商店开关 Shop_Toggle + 买/刷新/toast）。
        //   旧 BattleHud GO 始终抑制，本控制器惰性；验证后删组件+脚本+场景节点。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable() { if (MIGRATED_TO_XML) return; EnsureUI(); }

        // SideBar Btn_shop 调：toggle 顶部商场带显隐（v6 常显基础上加开关）
        public void Toggle()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI
            if (!_built) EnsureUI();
            if (_shopPanel != null)
            {
                bool show = !_shopPanel.activeSelf;
                _shopPanel.SetActive(show);
                if (show) { _lastHash = ""; SyncStock(); UpdateRefreshLabel(); }
            }
        }

        void Update()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI
            _poll += Time.unscaledDeltaTime;
            if (_poll >= POLL_INTERVAL)
            {
                _poll = 0;
                // v7 兜底：确保 SideBar「商店」开关存在（OnEnable 时 SideBar 可能未就绪 → poll 补建，idempotent）
                if (_built) { var _sb = transform.Find("SideBar"); if (_sb != null && _sb.Find("Btn_shop") == null) EnsureSidebarToggle(); }
                if (_shopPanel != null && _shopPanel.activeSelf)
                {
                    SyncStock();
                    UpdateRefreshLabel();
                }
            }
            // toast 淡出
            if (_toast != null && _toast.gameObject.activeSelf && Time.unscaledTime > _toastUntil)
                _toast.gameObject.SetActive(false);
        }

        static Font Fnt => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        void EnsureUI()
        {
            if (_built) return;

            // 常显顶部带（top-stretch；左让出背包竖条）
            // 用户 2026-06-12：面板顶边对齐屏幕上沿（R3c 后顶栏元素已迁走，不再让出 TOP_BAR_H）
            _shopPanel = new GameObject("ShopPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            _shopPanel.transform.SetParent(transform, false);
            var prt = _shopPanel.GetComponent<RectTransform>();
            prt.anchorMin = new Vector2(0f, 1f); prt.anchorMax = new Vector2(1f, 1f); prt.pivot = new Vector2(0.5f, 1f);
            prt.offsetMin = new Vector2(INV_STRIP_W, -SHOP_H);
            prt.offsetMax = new Vector2(-8f, 0f);
            _shopPanel.GetComponent<Image>().color = new Color(0.10f, 0.08f, 0.14f, 0.92f);

            // 标题「商场」（左上角小标）
            var title = AddLabel(_shopPanel.transform, "商场", 18, new Color(1f, 0.88f, 0.5f, 1f));
            var trt = title.rectTransform;
            trt.anchorMin = new Vector2(0f, 1f); trt.anchorMax = new Vector2(0f, 1f); trt.pivot = new Vector2(0f, 1f);
            trt.sizeDelta = new Vector2(80f, 24f); trt.anchoredPosition = new Vector2(8f, -4f);
            title.alignment = TextAnchor.UpperLeft;

            // 刷新按钮（右侧）
            var rbtn = BuildPanelButton(_shopPanel.transform, "Btn_ShopRefresh", "刷新", new Vector2(1f, 0.5f), new Vector2(-12f, 0f));
            if (rbtn != null) { rbtn.onClick.RemoveAllListeners(); rbtn.onClick.AddListener(OnRefreshClicked); _refreshLabel = rbtn.GetComponentInChildren<Text>(); }

            // Slots 容器（单行 grid，居中）
            var slotsGo = new GameObject("ShopSlots", typeof(RectTransform), typeof(GridLayoutGroup));
            slotsGo.transform.SetParent(_shopPanel.transform, false);
            var srt = slotsGo.GetComponent<RectTransform>();
            // 居中锚（容器宽 = 8 卡，水平居中于面板，右侧留出刷新按钮区）
            srt.anchorMin = new Vector2(0.5f, 0.5f); srt.anchorMax = new Vector2(0.5f, 0.5f); srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = new Vector2(-40f, 0f);   // 略左偏，避开右侧刷新按钮
            srt.sizeDelta = new Vector2(COLS * CELL_W + (COLS - 1) * SPX, CELL_H);
            var grid = slotsGo.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(CELL_W, CELL_H); grid.spacing = new Vector2(SPX, 0f);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft; grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount; grid.constraintCount = COLS;
            _shopSlots = slotsGo.transform;

            // toast（满格提示，居中，默认隐藏）
            _toast = AddLabel(_shopPanel.transform, "", 22, new Color(1f, 0.55f, 0.45f, 1f));
            var qrt = _toast.rectTransform;
            qrt.anchorMin = new Vector2(0.5f, 0f); qrt.anchorMax = new Vector2(0.5f, 0f); qrt.pivot = new Vector2(0.5f, 0f);
            qrt.sizeDelta = new Vector2(420f, 30f); qrt.anchoredPosition = new Vector2(0f, 4f);
            _toast.fontStyle = FontStyle.Bold;
            _toast.gameObject.SetActive(false);

            _shopPanel.SetActive(false);  // v7 默认收起，由 SideBar「商店」按钮 toggle 打开
            EnsureSidebarToggle();        // v7 在 SideBar 建「商店」开关（用户要求 Btn_shop）
            _built = true;

            // 进局先铺货（空货架免费铺一次），打开时即有卡
            if (!_openedOnce) { CallLua0("Shop_OnOpen"); _openedOnce = true; }
            _lastHash = ""; SyncStock(); UpdateRefreshLabel();
            Debug.Log("[ShopController] EnsureUI(v7) 完成（SideBar 商店开关 + 顶部带 8 卡居中 + 默认收起）");
        }

        // v7：在 SideBar 建「商店」开关按钮（Btn_shop）→ Toggle 商场带。idempotent（已存在则复用并重绑）。
        void EnsureSidebarToggle()
        {
            var sideBar = transform.Find("SideBar");
            Transform parent = sideBar != null ? sideBar : transform;
            var existing = parent.Find("Btn_shop");
            Button btn;
            if (existing != null && existing.GetComponent<Button>() != null)
            {
                btn = existing.GetComponent<Button>();
            }
            else
            {
                var go = new GameObject("Btn_shop", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                go.transform.SetParent(parent, false);
                var rt = go.GetComponent<RectTransform>();
                // 放到 SideBar 最上方按钮之上（避免与 伤害/特效 重叠）
                float topY = 0f; bool any = false;
                foreach (Transform ch in parent)
                {
                    var crt = ch as RectTransform;
                    if (crt != null && ch.gameObject != go && ch.GetComponent<Button>() != null)
                    { if (!any || crt.anchoredPosition.y > topY) { topY = crt.anchoredPosition.y; any = true; } }
                }
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(96f, 72f);
                rt.anchoredPosition = new Vector2(0f, any ? topY + 84f : 96f);
                go.GetComponent<Image>().color = new Color(0.85f, 0.62f, 0.20f, 0.95f);  // 金色（呼应"买"）
                AddLabel(go.transform, "商店", 22, Color.white);
                btn = go.GetComponent<Button>();
            }
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(Toggle);
        }

        Button BuildPanelButton(Transform parent, string name, string label, Vector2 anchor, Vector2 pos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchor; rt.anchorMax = anchor; rt.pivot = anchor;
            rt.sizeDelta = new Vector2(104f, 48f); rt.anchoredPosition = pos;
            go.GetComponent<Image>().color = new Color(0.85f, 0.62f, 0.20f, 0.95f);   // 金色（呼应"花钱"）
            AddLabel(go.transform, label, 18, Color.white);
            return go.GetComponent<Button>();
        }

        static Text AddLabel(Transform parent, string text, int size, Color col)
        {
            var go = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.AddComponent<Text>();
            t.text = text; t.color = col; t.fontSize = size; t.font = Fnt;
            t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
            return t;
        }

        void UpdateRefreshLabel()
        {
#if XLUA
            if (_refreshLabel == null) return;
            try
            {
                var env = LuaHost.Env; if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Shop_GetRefreshCost");
                if (fn != null) { var r = fn.Call(); fn.Dispose(); if (r != null && r.Length > 0) _refreshLabel.text = "刷新\n(" + System.Convert.ToInt32(r[0]) + "金)"; }
            }
            catch { }
#endif
        }

        void OnRefreshClicked() { CallLua0("Shop_Refresh"); _lastHash = ""; SyncStock(); }

        void ShowToast(string msg)
        {
            if (_toast == null) return;
            _toast.text = msg;
            _toast.gameObject.SetActive(true);
            _toastUntil = Time.unscaledTime + 1.6f;
        }

        void CallLua0(string fn)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env; if (env == null) return;
                var f = env.Global.Get<LuaFunction>(fn);
                if (f != null) { f.Call(); f.Dispose(); }
                else Debug.LogWarning("[Shop] Lua " + fn + " 未定义");
            }
            catch (System.Exception e) { Debug.LogError("[Shop] " + fn + ": " + e.Message); }
#endif
        }

        void OnBuyCard(int idx)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env; if (env == null) return;
                var f = env.Global.Get<LuaFunction>("Shop_OnBuyClicked");
                if (f == null) return;
                var res = f.Call(idx); f.Dispose();
                bool ok = res != null && res.Length > 0 && res[0] is bool && (bool)res[0];
                if (!ok)
                {
                    // 读 Shop_State.last_error 弹对应提示（gold / no_space）
                    string err = "";
                    try { var st = env.Global.Get<LuaTable>("Shop_State"); if (st != null) { err = st.Get<string, string>("last_error") ?? ""; st.Dispose(); } } catch { }
                    if (err == "no_space") ShowToast("背包与网格已满，没有空间");
                    else if (err == "gold") ShowToast("金币不足");
                    else ShowToast("无法购买");
                }
                _lastHash = ""; SyncStock();
            }
            catch (System.Exception e) { Debug.LogError("[Shop] buy: " + e.Message); }
#endif
        }

        class ShopCard { public int npcId; public int lv; public int buyCost; }
        readonly List<ShopCard> _stock = new List<ShopCard>();

        void SyncStock()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env; if (env == null) return;
                var st = env.Global.Get<LuaTable>("Shop_State"); if (st == null) return;
                var stock = st.Get<LuaTable>("stock"); st.Dispose();
                if (stock == null) return;
                _stock.Clear();
                int n = stock.Length;
                for (int i = 1; i <= n; i++)
                {
                    var c = stock.Get<int, LuaTable>(i); if (c == null) continue;
                    _stock.Add(new ShopCard { npcId = c.Get<string, int>("npc_id"), lv = c.Get<string, int>("lv"), buyCost = c.Get<string, int>("buy_cost") });
                    c.Dispose();
                }
                stock.Dispose();
                var sb = new System.Text.StringBuilder();
                foreach (var c in _stock) sb.Append(c.npcId).Append(',').Append(c.buyCost).Append(';');
                string hash = sb.ToString();
                if (hash == _lastHash) return;
                _lastHash = hash;
                RebuildCards();
            }
            catch (System.Exception e) { Debug.LogWarning("[Shop] SyncStock: " + e.Message); }
#endif
        }

        void RebuildCards()
        {
            if (_shopSlots == null) return;
            for (int i = _shopSlots.childCount - 1; i >= 0; i--)
            {
                var ch = _shopSlots.GetChild(i);
                if (Application.isPlaying) Destroy(ch.gameObject); else DestroyImmediate(ch.gameObject);
            }
            for (int i = 0; i < _stock.Count; i++) BuildShopCard(i, _stock[i]);
        }

        void BuildShopCard(int idx, ShopCard sc)
        {
            try
            {
                var cm = ConfigManager.Instance; cm.LoadIfNeeded();
                var row = cm.GetTableInfo("npc", "id", sc.npcId);
                string spriteKey = row != null ? cm.GetValue<string>(row, "sprite_key", "") : "";
                string nameCn = row != null ? cm.GetValue<string>(row, "name", "npc_" + sc.npcId) : ("npc_" + sc.npcId);

                var slot = new GameObject("ShopSlot_" + idx, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                slot.transform.SetParent(_shopSlots, false);
                slot.GetComponent<Image>().color = new Color(0.18f, 0.14f, 0.08f, 0.75f);
                int captured = idx;
                slot.GetComponent<Button>().onClick.AddListener(() => OnBuyCard(captured + 1));  // Lua 1-based

                var imgGo = new GameObject("Pic", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                imgGo.transform.SetParent(slot.transform, false);
                var irt = imgGo.GetComponent<RectTransform>();
                irt.anchorMin = new Vector2(0f, 0f); irt.anchorMax = new Vector2(1f, 1f);
                irt.offsetMin = new Vector2(6f, 30f); irt.offsetMax = new Vector2(-6f, -24f);
                var img = imgGo.GetComponent<Image>(); img.raycastTarget = false;
                Sprite sp = !string.IsNullOrEmpty(spriteKey) ? ResourceHost.LoadSprite("resources/art/" + spriteKey + "_idle_0.png") : null;
                if (sp != null) img.sprite = sp; else img.color = new Color(0.3f, 0.3f, 0.3f, 0.6f);

                AddTopName(slot.transform, nameCn);
                AddBuyLabel(slot.transform, sc.buyCost);
            }
            catch (System.Exception e) { Debug.LogError("[Shop] BuildShopCard " + sc.npcId + ": " + e.Message); }
        }

        void AddTopName(Transform card, string nameCn)
        {
            var go = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(card, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 22f); rt.anchoredPosition = Vector2.zero;
            var t = go.AddComponent<Text>(); t.text = nameCn; t.color = Color.white; t.fontSize = 15; t.font = Fnt;
            t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
        }

        void AddBuyLabel(Transform card, int buyCost)
        {
            var go = new GameObject("Buy", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(card, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(0f, 26f); rt.anchoredPosition = Vector2.zero;
            go.GetComponent<Image>().color = new Color(0.85f, 0.65f, 0.15f, 0.9f);
            go.GetComponent<Image>().raycastTarget = false;
            var tg = new GameObject("T", typeof(RectTransform), typeof(CanvasRenderer));
            tg.transform.SetParent(go.transform, false);
            var trt = tg.GetComponent<RectTransform>(); trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one; trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var t = tg.AddComponent<Text>(); t.text = (buyCost > 0 ? (buyCost + " 金") : "不可购"); t.color = Color.black; t.fontSize = 15; t.fontStyle = FontStyle.Bold; t.font = Fnt;
            t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
        }
    }
}
