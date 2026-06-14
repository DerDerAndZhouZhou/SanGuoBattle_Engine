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
    /// 背包控制器（v6 重构 2026-06-02）。挂在 UIWindow/RootWindow/BattleHud 上。
    ///
    /// v6 改动（用户拍板）：
    ///   - 从"顶部横带 + 开关"改为"左侧竖条常显"（背景左缘→左基地之间，顶部信息栏→底部）。无开关、删背包按钮。
    ///   - 单列纵向 + 垂直 scroll。容量随基地升级增长（Stash 端控制；UI 仅渲染当前卡列表）。
    ///   - 每张卡右侧额外显示"出售价格"（Lua Shop_GetSellValue，复用回收 ratio；红卡=0 不显）。
    ///   - 其余显示（立绘 / 名 / lv 角标 / 定位标签）同原网格语义。
    ///
    /// 数据流：poll Stash_State.cards → 卡片 GameObject 重建。0 [SerializeField]：全 Tag 查找 + 程序化建卡。
    /// </summary>
    public class InventoryController : MonoBehaviour
    {
        GameObject _panel;
        Transform _slotsContainer;
        RectTransform _viewportRt;
        ScrollRect _scrollRect;
        GridLayoutGroup _gridLayout;
        ContentSizeFitter _sizeFitter;
        bool _layoutEnsured;

        // v6 左侧竖条：单列纵向。宽 = 竖条宽 - 内边距；高 = 容纳立绘 + 名 + 售价。
        const float INV_STRIP_W = 210f;   // 竖条总宽（与 ShopController 对齐）
        const float TOP_BAR_H   = 80f;    // 顶部信息栏高（竖条从其下方开始）
        const int   GRID_COLS   = 1;      // v6：单列
        const float CELL_W      = 178f;
        const float CELL_H      = 132f;
        const float CELL_SPACING_X = 0f;
        const float CELL_SPACING_Y = 8f;
        const float VIEWPORT_PAD_LEFT   = 6f;
        const float VIEWPORT_PAD_RIGHT  = 6f;
        const float VIEWPORT_PAD_TOP    = 6f;
        const float VIEWPORT_PAD_BOTTOM = 6f;

        float _pollAccum;
        const float POLL_INTERVAL = 0.3f;
        string _lastHash = "";

        void OnEnable()
        {
            ResolveChildren();
            EnsureLeftStripLayout();
            TrySyncFromLua();
        }

        void Update()
        {
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            TrySyncFromLua();
        }

        void ResolveChildren()
        {
            var root = transform; // BattleHud
            _panel = root.Find("InventoryPanel")?.gameObject;
            if (_panel != null)
            {
                _slotsContainer = _panel.transform.Find("Slots");
                if (_slotsContainer == null) _slotsContainer = _panel.transform.Find("Viewport/Slots");
            }
            // v6：背包常显 → 删除残留入口按钮（背包开关 / 刷新 / 看广告 均废弃）
            DestroyStaleButton("Btn_Inventory");
            DestroyStaleButton("Btn_Refresh");
            DestroyStaleButton("Btn_Ad");
        }

        void DestroyStaleButton(string tag)
        {
            GameObject stale = null;
            try { stale = GameObject.FindWithTag(tag); } catch { return; }   // tag 未定义时容错
            if (stale != null)
            {
                if (Application.isPlaying) Destroy(stale); else DestroyImmediate(stale);
                Debug.Log("[InventoryController] 删除残留按钮 " + tag);
            }
        }

        // ============ Lua → UI 同步 ============

        class CardRec
        {
            public string kind;       // "unit"
            public int npcId;
            public int lv;
            public string source;     // v6：售价计算用（shop/buy/gacha/drop/...）
        }
        readonly List<CardRec> _curCards = new List<CardRec>();

        void TrySyncFromLua()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var stash = env.Global.Get<LuaTable>("Stash_State");
                if (stash == null) return;
                var cards = stash.Get<LuaTable>("cards");
                stash.Dispose();
                if (cards == null) return;

                _curCards.Clear();
                int n = cards.Length;
                for (int i = 1; i <= n; i++)
                {
                    var c = cards.Get<int, LuaTable>(i);
                    if (c == null) continue;
                    string kind = c.Get<string, string>("kind") ?? "unit";
                    var rec = new CardRec { kind = kind, source = c.Get<string, string>("source") ?? "" };
                    rec.npcId = c.Get<string, int>("npc_id");
                    rec.lv = c.Get<string, int>("lv");
                    _curCards.Add(rec);
                    c.Dispose();
                }
                cards.Dispose();

                var sb = new System.Text.StringBuilder();
                foreach (var c in _curCards)
                {
                    sb.Append("N:").Append(c.npcId).Append(',').Append(c.lv).Append(';');
                }
                string hash = sb.ToString();
                if (hash == _lastHash) return;
                _lastHash = hash;
                RebuildSlots();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Inv] TrySyncFromLua: {e.Message}");
            }
#endif
        }

        // v6 — 左侧竖条布局：ScrollRect + Viewport + 单列 GridLayoutGroup（垂直 scroll）。幂等。
        void EnsureLeftStripLayout()
        {
            if (_layoutEnsured) return;
            if (_panel == null || _slotsContainer == null) return;
            var panelRt = _panel.transform as RectTransform;
            if (panelRt == null) return;

            _panel.SetActive(true);   // v6 常显

            // 面板背景（半透明深色竖条）
            var panelImg = _panel.GetComponent<Image>();
            if (panelImg == null) panelImg = _panel.AddComponent<Image>();
            panelImg.color = new Color(0.08f, 0.07f, 0.10f, 0.86f);
            panelImg.raycastTarget = true;

            PositionPanelLeftStrip(panelRt);

            // 标题「背包」
            EnsureTitle(panelRt);

            // Viewport
            Transform parent = _slotsContainer.parent;
            RectTransform vpRt = null;
            if (parent != null && parent.name == "Viewport") vpRt = parent as RectTransform;
            if (vpRt == null)
            {
                var vpGo = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
                vpGo.transform.SetParent(_panel.transform, false);
                vpRt = vpGo.GetComponent<RectTransform>();
                vpRt.anchorMin = new Vector2(0f, 0f); vpRt.anchorMax = new Vector2(1f, 1f); vpRt.pivot = new Vector2(0.5f, 0.5f);
                vpRt.offsetMin = new Vector2(VIEWPORT_PAD_LEFT, VIEWPORT_PAD_BOTTOM);
                vpRt.offsetMax = new Vector2(-VIEWPORT_PAD_RIGHT, -(VIEWPORT_PAD_TOP + 26f));   // 顶部留 26 给标题
                var vpImg = vpGo.GetComponent<Image>();
                vpImg.color = new Color(0f, 0f, 0f, 0.001f);
                vpImg.raycastTarget = true;
                _slotsContainer.SetParent(vpRt, false);
            }
            _viewportRt = vpRt;

            _scrollRect = _panel.GetComponent<ScrollRect>();
            if (_scrollRect == null) _scrollRect = _panel.AddComponent<ScrollRect>();
            _scrollRect.content = _slotsContainer as RectTransform;
            _scrollRect.viewport = vpRt;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.movementType = ScrollRect.MovementType.Elastic;
            _scrollRect.elasticity = 0.1f;
            _scrollRect.inertia = true;
            _scrollRect.scrollSensitivity = 30f;

            var slotsRt = _slotsContainer as RectTransform;
            if (slotsRt != null)
            {
                slotsRt.anchorMin = new Vector2(0f, 1f);
                slotsRt.anchorMax = new Vector2(1f, 1f);
                slotsRt.pivot = new Vector2(0.5f, 1f);
                slotsRt.sizeDelta = new Vector2(0f, 0f);
                slotsRt.anchoredPosition = Vector2.zero;
            }

            _gridLayout = _slotsContainer.GetComponent<GridLayoutGroup>();
            if (_gridLayout == null) _gridLayout = _slotsContainer.gameObject.AddComponent<GridLayoutGroup>();
            _gridLayout.padding = new RectOffset(0, 0, 0, 0);
            _gridLayout.cellSize = new Vector2(CELL_W, CELL_H);
            _gridLayout.spacing = new Vector2(CELL_SPACING_X, CELL_SPACING_Y);
            _gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            _gridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
            _gridLayout.childAlignment = TextAnchor.UpperCenter;
            _gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _gridLayout.constraintCount = GRID_COLS;

            _sizeFitter = _slotsContainer.GetComponent<ContentSizeFitter>();
            if (_sizeFitter == null) _sizeFitter = _slotsContainer.gameObject.AddComponent<ContentSizeFitter>();
            _sizeFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            _sizeFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            for (int i = _slotsContainer.childCount - 1; i >= 0; i--)
            {
                var ch = _slotsContainer.GetChild(i);
                if (Application.isPlaying) Destroy(ch.gameObject); else DestroyImmediate(ch.gameObject);
            }

            _layoutEnsured = true;
            Debug.Log("[InventoryController] EnsureLeftStripLayout(v6) 完成（左竖条常显 + 单列纵向 scroll）");
        }

        // 左侧竖条：左缘起、宽 INV_STRIP_W、顶部让出信息栏、下到底
        void PositionPanelLeftStrip(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(8f, 8f);
            rt.offsetMax = new Vector2(8f + INV_STRIP_W, -TOP_BAR_H);
        }

        void EnsureTitle(RectTransform panelRt)
        {
            if (_panel.transform.Find("InvTitle") != null) return;
            var go = new GameObject("InvTitle", typeof(RectTransform), typeof(CanvasRenderer));
            go.transform.SetParent(_panel.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 24f); rt.anchoredPosition = new Vector2(0f, -3f);
            var t = go.AddComponent<Text>();
            t.text = "背包"; t.color = new Color(0.95f, 0.9f, 0.7f, 1f); t.fontSize = 17;
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.alignment = TextAnchor.MiddleCenter; t.raycastTarget = false;
        }

        void RebuildSlots()
        {
            if (_slotsContainer == null) return;
            for (int i = _slotsContainer.childCount - 1; i >= 0; i--)
            {
                var ch = _slotsContainer.GetChild(i);
                if (Application.isPlaying) Destroy(ch.gameObject); else DestroyImmediate(ch.gameObject);
            }
            for (int i = 0; i < _curCards.Count; i++)
            {
                var slotGo = new GameObject($"Slot_{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                slotGo.transform.SetParent(_slotsContainer, false);
                var slotImg = slotGo.GetComponent<Image>();
                slotImg.color = new Color(0.18f, 0.14f, 0.08f, 0.65f);
                slotImg.raycastTarget = true;
                BuildCard(slotGo.transform, _curCards[i]);
            }
        }

        static string LookupCardSpriteKey(int npcId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return null;
                var row = cm.GetTableInfo("npc_card_sprite", "npc_id", npcId);
                if (row == null) return null;
                string v = cm.GetValue<string>(row, "card_sprite_key", "");
                return string.IsNullOrEmpty(v) ? null : v;
            }
            catch { return null; }
        }

        // v6 — 查某卡出售价（Lua Shop_GetSellValue；红卡/不可回收 → 0）
        static int SellValueOf(int npcId, int lv, string source)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env; if (env == null) return 0;
                var fn = env.Global.Get<LuaFunction>("Shop_GetSellValue");
                if (fn == null) return 0;
                var r = fn.Call(npcId, lv, source ?? "");
                fn.Dispose();
                if (r != null && r.Length > 0) return System.Convert.ToInt32(r[0]);
            }
            catch { }
#endif
            return 0;
        }

        static Color RoleColor(string role)
        {
            switch (role)
            {
                case "坦克": return new Color(0.20f, 0.42f, 0.85f, 0.92f);
                case "战士": return new Color(0.82f, 0.26f, 0.20f, 0.92f);
                case "法师": return new Color(0.56f, 0.30f, 0.82f, 0.92f);
                case "射手": return new Color(0.22f, 0.66f, 0.32f, 0.92f);
                case "治疗": return new Color(0.88f, 0.70f, 0.20f, 0.92f);
                default:     return new Color(0.40f, 0.40f, 0.40f, 0.92f);
            }
        }

        void BuildCard(Transform slot, CardRec rec)
        {
            try
            {
                int npcId = rec != null ? rec.npcId : 0;
                int lv = rec != null ? rec.lv : 0;
                var cm = ConfigManager.Instance;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("npc", "id", npcId);
                if (row == null) { Debug.LogWarning($"[Inv] npc id={npcId} not found"); return; }
                string spriteKey = cm.GetValue<string>(row, "sprite_key", "");
                string nameCn = cm.GetValue<string>(row, "name", $"npc_{npcId}");

                var card = new GameObject($"Card_{npcId}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(HeroDefense.Battle.DragInputBridge));
                card.transform.SetParent(slot, false);
                var crt = card.GetComponent<RectTransform>();
                crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
                crt.offsetMin = new Vector2(4, 4); crt.offsetMax = new Vector2(-4, -4);

                var img = card.GetComponent<Image>();
                string cardKey = LookupCardSpriteKey(npcId);
                Sprite sprite = null;
                if (!string.IsNullOrEmpty(cardKey)) sprite = ResourceHost.LoadSprite($"art/{cardKey}.png");
                if (sprite == null) sprite = ResourceHost.LoadSprite($"art/{spriteKey}_idle_0.png");
                if (sprite != null) img.sprite = sprite;
                img.color = new Color(1f, 1f, 1f, 1f);
                img.raycastTarget = true;

                // 名（底部）
                var nameGo = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer));
                nameGo.transform.SetParent(card.transform, false);
                var nrt = nameGo.GetComponent<RectTransform>();
                nrt.anchorMin = new Vector2(0, 0); nrt.anchorMax = new Vector2(1, 0); nrt.pivot = new Vector2(0.5f, 0);
                nrt.sizeDelta = new Vector2(0, 20); nrt.anchoredPosition = Vector2.zero;
                var nameTxt = nameGo.AddComponent<Text>();
                nameTxt.text = nameCn; nameTxt.color = Color.white; nameTxt.fontSize = 15;
                nameTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                nameTxt.alignment = TextAnchor.MiddleCenter; nameTxt.raycastTarget = false;

                // 右上角 lv 角标
                if (lv >= 1)
                {
                    var lvGo = new GameObject("LvBadge", typeof(RectTransform), typeof(CanvasRenderer));
                    lvGo.transform.SetParent(card.transform, false);
                    var lrt = lvGo.GetComponent<RectTransform>();
                    lrt.anchorMin = new Vector2(1f, 1f); lrt.anchorMax = new Vector2(1f, 1f); lrt.pivot = new Vector2(1f, 1f);
                    lrt.sizeDelta = new Vector2(40, 18); lrt.anchoredPosition = new Vector2(-2f, -2f);
                    var lvTxt = lvGo.AddComponent<Text>();
                    lvTxt.text = $"lv {lv}"; lvTxt.color = new Color(1f, 0.95f, 0.4f, 1f); lvTxt.fontSize = 13;
                    lvTxt.fontStyle = FontStyle.Bold;
                    lvTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    lvTxt.alignment = TextAnchor.UpperRight; lvTxt.raycastTarget = false;
                    var outline = lvGo.AddComponent<Outline>();
                    outline.effectColor = new Color(0f, 0f, 0f, 0.85f); outline.effectDistance = new Vector2(1f, -1f);
                }

                // 左上角定位标签
                string role = cm.GetValue<string>(row, "role", "");
                if (!string.IsNullOrEmpty(role) && role != "-")
                {
                    var roleGo = new GameObject("RoleTag", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    roleGo.transform.SetParent(card.transform, false);
                    var rrt = roleGo.GetComponent<RectTransform>();
                    rrt.anchorMin = new Vector2(0f, 1f); rrt.anchorMax = new Vector2(0f, 1f); rrt.pivot = new Vector2(0f, 1f);
                    rrt.sizeDelta = new Vector2(40f, 18f); rrt.anchoredPosition = new Vector2(2f, -2f);
                    roleGo.GetComponent<Image>().color = RoleColor(role);
                    roleGo.GetComponent<Image>().raycastTarget = false;
                    var rtGo = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer));
                    rtGo.transform.SetParent(roleGo.transform, false);
                    var rtRt = rtGo.GetComponent<RectTransform>();
                    rtRt.anchorMin = Vector2.zero; rtRt.anchorMax = Vector2.one; rtRt.offsetMin = Vector2.zero; rtRt.offsetMax = Vector2.zero;
                    var rt = rtGo.AddComponent<Text>();
                    rt.text = role; rt.color = Color.white; rt.fontSize = 13; rt.fontStyle = FontStyle.Bold;
                    rt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    rt.alignment = TextAnchor.MiddleCenter; rt.raycastTarget = false;
                }

                // v6 — 右侧出售价（复用回收 ratio；红卡=0 不显）
                int sell = SellValueOf(npcId, lv < 1 ? 1 : lv, rec != null ? rec.source : "");
                if (sell > 0)
                {
                    var sellGo = new GameObject("SellPrice", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    sellGo.transform.SetParent(card.transform, false);
                    var srt = sellGo.GetComponent<RectTransform>();
                    srt.anchorMin = new Vector2(1f, 0.5f); srt.anchorMax = new Vector2(1f, 0.5f); srt.pivot = new Vector2(1f, 0.5f);
                    srt.sizeDelta = new Vector2(34f, 46f); srt.anchoredPosition = new Vector2(-1f, 6f);
                    sellGo.GetComponent<Image>().color = new Color(0.82f, 0.62f, 0.12f, 0.92f);
                    sellGo.GetComponent<Image>().raycastTarget = false;
                    var stg = new GameObject("Txt", typeof(RectTransform), typeof(CanvasRenderer));
                    stg.transform.SetParent(sellGo.transform, false);
                    var stRt = stg.GetComponent<RectTransform>();
                    stRt.anchorMin = Vector2.zero; stRt.anchorMax = Vector2.one; stRt.offsetMin = Vector2.zero; stRt.offsetMax = Vector2.zero;
                    var stt = stg.AddComponent<Text>();
                    stt.text = $"售\n{sell}"; stt.color = Color.black; stt.fontSize = 13; stt.fontStyle = FontStyle.Bold;
                    stt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    stt.alignment = TextAnchor.MiddleCenter; stt.raycastTarget = false;
                }

                var dib = card.GetComponent<HeroDefense.Battle.DragInputBridge>();
                dib.SetSource(npcId, "inventory");
            }
            catch (System.Exception e)
            {
                int idForErr = rec != null ? rec.npcId : 0;
                Debug.LogError($"[Inv] BuildCard 失败 npc={idForErr}: {e.Message}");
            }
        }

    }
}
