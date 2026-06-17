using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Battle;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.UI
{
    /// <summary>
    /// 伤害统计弹窗(Phase 2.10) — 2026-05-21 重设计:程序化构建全屏 modal。
    ///
    /// 数据流:
    ///   - 挂在 BattleHud 上,Toggle 切换显示
    ///   - 显示时 0.5s poll Lua DamageStats_GetSortedList() 重建滚动行
    ///   - 行 NameLabel + DamageLabel 由 BattleBridge.FormatDamageRow* 拼字符串
    ///   - 全屏遮罩挡按钮 + 中央 panel + 关闭按钮 + 滚动列表
    ///
    /// 0 [SerializeField]:全程序化构建。
    /// </summary>
    public class DamageStatsController : MonoBehaviour
    {
        GameObject _modalRoot;
        Transform _listRoot;
        bool _builtOk;

        float _pollAccum;
        const float POLL_INTERVAL = 0.5f;
        string _lastHash = "";

        // ⚠ 已迁移到热更 UI：伤害统计现由 Game/ui/wnd_damage_stats.xml + lua/ui/wnd_damage_stats.lua 实现
        //   （HUD 伤害按钮 onClick 转调 Lua DmgStats_Toggle）。本控制器置惰性、不再程序化构建 modal，
        //   验证通过后将彻底移除本组件 + 删脚本（迁移收尾步）。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI
            EnsureModal();
            _lastHash = "";
        }

        void Update()
        {
            if (_modalRoot == null || !_modalRoot.activeSelf) return;
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            Refresh();
        }

        public void Toggle()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI（HUD 改调 Lua DmgStats_Toggle）
            EnsureModal();
            if (_modalRoot == null) return;
            bool show = !_modalRoot.activeSelf;
            _modalRoot.SetActive(show);
            if (show)
            {
                _modalRoot.transform.SetAsLastSibling();
                _lastHash = "";
                _pollAccum = POLL_INTERVAL + 1f;
                Refresh();
            }
            Debug.Log("[DamageStats] modal → " + show);
        }

        public void Hide()
        {
            if (_modalRoot != null) _modalRoot.SetActive(false);
        }

        // T216:BattleHud 销毁时同步销毁 modal,避免挂在常驻 Canvas 下残留挡 MainScene UI
        void OnDestroy()
        {
            if (_modalRoot != null)
            {
                if (Application.isPlaying) Destroy(_modalRoot);
                else DestroyImmediate(_modalRoot);
                _modalRoot = null;
            }
        }

        static Sprite _whiteSprite;
        static Sprite GetWhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f);
            return _whiteSprite;
        }

        // ============ 程序化构建 ============

        void EnsureModal()
        {
            if (_builtOk && _modalRoot != null) return;
            try
            {
                var canvas = GetComponentInParent<Canvas>();
                Transform mountParent = canvas != null ? canvas.transform : transform;

                _modalRoot = new GameObject("DamageStatsModal",
                    typeof(RectTransform), typeof(CanvasRenderer));
                _modalRoot.transform.SetParent(mountParent, false);
                var rrt = _modalRoot.GetComponent<RectTransform>();
                rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one;
                rrt.offsetMin = Vector2.zero; rrt.offsetMax = Vector2.zero;

                // 全屏遮罩 — Image 必须设 sprite 才渲染
                var overlay = new GameObject("Overlay",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                overlay.transform.SetParent(_modalRoot.transform, false);
                var ort = overlay.GetComponent<RectTransform>();
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one;
                ort.offsetMin = Vector2.zero; ort.offsetMax = Vector2.zero;
                var oImg = overlay.GetComponent<Image>();
                oImg.sprite = GetWhiteSprite();
                oImg.color = new Color(0f, 0f, 0f, 0.65f);
                oImg.raycastTarget = true;
                overlay.GetComponent<Button>().onClick.AddListener(Hide);

                // 中央 panel
                var panel = new GameObject("Panel",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                    typeof(VerticalLayoutGroup));
                panel.GetComponent<Image>().sprite = GetWhiteSprite();
                panel.transform.SetParent(_modalRoot.transform, false);
                var prt = panel.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f);
                prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0.5f, 0.5f);
                prt.anchoredPosition = Vector2.zero;
                prt.sizeDelta = new Vector2(620f, 540f);
                panel.GetComponent<Image>().color = new Color(0.12f, 0.10f, 0.07f, 0.97f);

                var pvlg = panel.GetComponent<VerticalLayoutGroup>();
                pvlg.padding = new RectOffset(16, 16, 14, 14);
                pvlg.spacing = 8f;
                pvlg.childAlignment = TextAnchor.UpperLeft;
                pvlg.childControlWidth = true; pvlg.childControlHeight = false;
                pvlg.childForceExpandWidth = true;

                BuildHeader(panel.transform, "伤害统计");
                _listRoot = BuildScrollView(panel.transform);

                _modalRoot.SetActive(false);
                _builtOk = true;
                Debug.Log("[DamageStatsController] modal 程序化构建完成");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[DamageStatsController] EnsureModal 失败: {e.Message}");
            }
        }

        void BuildHeader(Transform parent, string title)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var hdr = new GameObject("Header",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(HorizontalLayoutGroup));
            hdr.transform.SetParent(parent, false);
            var le = hdr.AddComponent<LayoutElement>();
            le.minHeight = 38f; le.preferredHeight = 38f;
            var hlg = hdr.GetComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;

            var titleGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            titleGo.transform.SetParent(hdr.transform, false);
            titleGo.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var t = titleGo.GetComponent<Text>();
            t.text = title; t.font = font; t.fontSize = 24; t.fontStyle = FontStyle.Bold;
            t.color = new Color(1f, 0.88f, 0.5f, 1f);
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;

            var closeGo = new GameObject("Close",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
            closeGo.transform.SetParent(hdr.transform, false);
            var cle = closeGo.GetComponent<LayoutElement>();
            cle.minWidth = 60f; cle.preferredWidth = 60f;
            cle.minHeight = 30f; cle.preferredHeight = 30f;
            closeGo.GetComponent<Image>().color = new Color(0.55f, 0.18f, 0.15f, 1f);
            closeGo.GetComponent<Button>().onClick.AddListener(Hide);

            var closeTxtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            closeTxtGo.transform.SetParent(closeGo.transform, false);
            var crt = closeTxtGo.GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            var ct = closeTxtGo.GetComponent<Text>();
            ct.text = "关闭"; ct.font = font; ct.fontSize = 18;
            ct.color = Color.white; ct.alignment = TextAnchor.MiddleCenter;
            ct.raycastTarget = false;
        }

        Transform BuildScrollView(Transform parent)
        {
            var sv = new GameObject("ScrollView",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(ScrollRect), typeof(LayoutElement));
            sv.transform.SetParent(parent, false);
            sv.GetComponent<Image>().color = new Color(0.05f, 0.04f, 0.03f, 0.6f);
            sv.GetComponent<LayoutElement>().flexibleHeight = 1f;
            sv.GetComponent<LayoutElement>().minHeight = 420f;

            var vp = new GameObject("Viewport",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            vp.transform.SetParent(sv.transform, false);
            var vprt = vp.GetComponent<RectTransform>();
            vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one;
            vprt.offsetMin = new Vector2(6f, 6f); vprt.offsetMax = new Vector2(-6f, -6f);
            vp.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);
            vp.GetComponent<Mask>().showMaskGraphic = false;

            var content = new GameObject("Content",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(vp.transform, false);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = Vector2.zero;

            var cvlg = content.GetComponent<VerticalLayoutGroup>();
            cvlg.padding = new RectOffset(8, 8, 8, 8);
            cvlg.spacing = 4f;
            cvlg.childAlignment = TextAnchor.UpperLeft;
            cvlg.childControlWidth = true; cvlg.childControlHeight = true;
            cvlg.childForceExpandWidth = true; cvlg.childForceExpandHeight = false;

            var cfit = content.GetComponent<ContentSizeFitter>();
            cfit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            cfit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = sv.GetComponent<ScrollRect>();
            scroll.viewport = vprt;
            scroll.content = crt;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            return content.transform;
        }

        // ============ Refresh ============

        void Refresh()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null || _listRoot == null) return;
                var fn = env.Global.Get<LuaFunction>("DamageStats_GetSortedList");
                if (fn == null) return;
                object[] ret = fn.Call();
                fn.Dispose();
                if (ret == null || ret.Length == 0) return;
                var list = ret[0] as LuaTable;
                if (list == null) return;

                int n = list.Length;
                var items = new List<LuaTable>(n);
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i <= n; i++)
                {
                    var row = list.Get<int, LuaTable>(i);
                    if (row == null) continue;
                    items.Add(row);
                    long h = 0; int tot = 0;
                    try { h = row.Get<string, long>("handle"); } catch { }
                    try { tot = row.Get<string, int>("total_damage"); } catch { }
                    sb.Append(h).Append(':').Append(tot).Append(';');
                }
                list.Dispose();
                string hash = sb.ToString();
                if (hash == _lastHash)
                {
                    foreach (var it in items) it.Dispose();
                    return;
                }
                _lastHash = hash;

                for (int i = _listRoot.childCount - 1; i >= 0; i--)
                {
                    var c = _listRoot.GetChild(i);
                    if (c == null) continue;
                    if (Application.isPlaying) Destroy(c.gameObject);
                    else DestroyImmediate(c.gameObject);
                }

                if (items.Count == 0)
                {
                    BuildEmptyHint();
                    return;
                }
                foreach (var item in items)
                {
                    string name = BattleBridge.FormatDamageRowName(item);
                    string dmg = BattleBridge.FormatDamageRowDamage(item);
                    BuildDamageRow(name, dmg);
                    item.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[DamageStats] Refresh: {e.Message}");
            }
#endif
        }

        void BuildEmptyHint()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("Empty", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(_listRoot, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = 60f; le.preferredHeight = 60f;
            var t = go.GetComponent<Text>();
            t.text = "暂无伤害记录";
            t.font = font; t.fontSize = 18;
            t.color = new Color(0.7f, 0.7f, 0.65f, 1f);
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
        }

        void BuildDamageRow(string name, string dmg)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var row = new GameObject("Row",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(HorizontalLayoutGroup), typeof(LayoutElement));
            row.transform.SetParent(_listRoot, false);
            row.GetComponent<Image>().color = new Color(0.22f, 0.18f, 0.12f, 0.9f);
            row.GetComponent<LayoutElement>().minHeight = 32f;
            row.GetComponent<LayoutElement>().preferredHeight = 32f;

            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(10, 10, 4, 4);
            hlg.spacing = 6f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = true; hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;

            var nameGo = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            nameGo.transform.SetParent(row.transform, false);
            nameGo.GetComponent<LayoutElement>().flexibleWidth = 1f;
            var nt = nameGo.GetComponent<Text>();
            nt.text = name; nt.font = font; nt.fontSize = 16;
            nt.color = new Color(1f, 0.95f, 0.75f, 1f);
            nt.alignment = TextAnchor.MiddleLeft;
            nt.raycastTarget = false;

            var dmgGo = new GameObject("Dmg", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            dmgGo.transform.SetParent(row.transform, false);
            dmgGo.GetComponent<LayoutElement>().minWidth = 180f;
            dmgGo.GetComponent<LayoutElement>().preferredWidth = 180f;
            var dt = dmgGo.GetComponent<Text>();
            dt.text = dmg; dt.font = font; dt.fontSize = 15;
            dt.color = new Color(1f, 0.6f, 0.3f, 1f);
            dt.alignment = TextAnchor.MiddleRight;
            dt.raycastTarget = false;
        }
    }
}
