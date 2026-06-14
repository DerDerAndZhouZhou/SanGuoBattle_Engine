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
    /// 特效（增益）弹窗 — 用户 2026-05-21 重设计:展示本局已选 buff 列表。
    ///
    /// 数据流:
    ///   - 挂在 BattleHud 上(与 DamageStatsController 兄弟),始终 active
    ///   - 程序化构建全屏 modal:半透明遮罩(挡住下面按钮) + 中央 panel + Header + Close + 滚动列表
    ///   - 显示时 poll Lua `Rogue_GetActiveBuffIds()` → C# ConfigManager 查 buff.txt 拿 name/desc
    ///   - 行只显示数据(不可点),关闭按钮 / 遮罩点击均可 dismiss
    ///
    /// 0 [SerializeField]:Tag/路径 + 程序化构建。
    /// </summary>
    public class SkillCardController : MonoBehaviour
    {
        GameObject _modalRoot;       // 全屏 root,含遮罩 + 中央 panel
        Transform _listRoot;          // 中央 panel 的滚动 Content
        bool _builtOk;

        float _pollAccum;
        const float POLL_INTERVAL = 0.5f;
        string _lastHash = "";

        readonly List<int> _curBuffIds = new List<int>();

        void OnEnable()
        {
            EnsureModal();
            _lastHash = "";
        }

        void Update()
        {
            if (_modalRoot == null || !_modalRoot.activeSelf) return;
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            TrySyncFromLua();
        }

        public void Toggle()
        {
            EnsureModal();
            if (_modalRoot == null) return;
            bool show = !_modalRoot.activeSelf;
            _modalRoot.SetActive(show);
            if (show)
            {
                _modalRoot.transform.SetAsLastSibling();   // 保证盖住所有按钮
                _lastHash = "";
                _pollAccum = POLL_INTERVAL + 1f;
                TrySyncFromLua();
            }
            Debug.Log("[SkillCard] modal → " + show);
        }

        public void Hide()
        {
            if (_modalRoot != null) _modalRoot.SetActive(false);
        }

        // T216 (2026-05-21):BattleHud 销毁时同步销毁 modal,避免挂在常驻 Canvas 下残留挡 MainScene UI
        void OnDestroy()
        {
            if (_modalRoot != null)
            {
                if (Application.isPlaying) Destroy(_modalRoot);
                else DestroyImmediate(_modalRoot);
                _modalRoot = null;
            }
        }

        // 共享白色 sprite(Image 无 sprite 不渲染)
        static Sprite _whiteSprite;
        static Sprite GetWhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f);
            return _whiteSprite;
        }

        // ============ 程序化构建全屏 modal ============

        void EnsureModal()
        {
            if (_builtOk && _modalRoot != null) return;
            try
            {
                // 找 BattleHud 顶层 Canvas(挂载点)
                var canvas = GetComponentInParent<Canvas>();
                Transform mountParent = canvas != null ? canvas.transform : transform;

                _modalRoot = new GameObject("SkillCardModal",
                    typeof(RectTransform), typeof(CanvasRenderer));
                _modalRoot.transform.SetParent(mountParent, false);
                var rrt = _modalRoot.GetComponent<RectTransform>();
                rrt.anchorMin = Vector2.zero;
                rrt.anchorMax = Vector2.one;
                rrt.offsetMin = Vector2.zero;
                rrt.offsetMax = Vector2.zero;

                // 全屏半透明遮罩(挡下面按钮 — raycastTarget=true)
                // UGUI Image 没 sprite 时不渲染,必须显式设白色 sprite + 黑色半透 tint
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

                // 中央 panel(600×500)
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
                pvlg.childControlWidth = true;
                pvlg.childControlHeight = false;
                pvlg.childForceExpandWidth = true;

                // Header 行
                BuildHeader(panel.transform, "已获得增益");

                // 滚动容器
                _listRoot = BuildScrollView(panel.transform);

                _modalRoot.SetActive(false);
                _builtOk = true;
                Debug.Log("[SkillCardController] modal 程序化构建完成");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SkillCardController] EnsureModal 失败: {e.Message}");
            }
        }

        static void BuildHeader(Transform parent, string title)
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
            var tle = titleGo.GetComponent<LayoutElement>();
            tle.flexibleWidth = 1f;
            var t = titleGo.GetComponent<Text>();
            t.text = title; t.font = font; t.fontSize = 24; t.fontStyle = FontStyle.Bold;
            t.color = new Color(1f, 0.88f, 0.5f, 1f);
            t.alignment = TextAnchor.MiddleLeft;
            t.raycastTarget = false;

            // 关闭按钮(50×30)
            var closeGo = new GameObject("Close",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
            closeGo.transform.SetParent(hdr.transform, false);
            var cle = closeGo.GetComponent<LayoutElement>();
            cle.minWidth = 60f; cle.preferredWidth = 60f;
            cle.minHeight = 30f; cle.preferredHeight = 30f;
            closeGo.GetComponent<Image>().color = new Color(0.55f, 0.18f, 0.15f, 1f);
            var btn = closeGo.GetComponent<Button>();
            var ctrl = parent.GetComponentInParent<SkillCardController>();
            if (ctrl != null) btn.onClick.AddListener(ctrl.Hide);

            var closeTxtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            closeTxtGo.transform.SetParent(closeGo.transform, false);
            var crt = closeTxtGo.GetComponent<RectTransform>();
            crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
            crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            var ct = closeTxtGo.GetComponent<Text>();
            ct.text = "关闭"; ct.font = font; ct.fontSize = 18;
            ct.color = Color.white;
            ct.alignment = TextAnchor.MiddleCenter;
            ct.raycastTarget = false;
        }

        static Transform BuildScrollView(Transform parent)
        {
            // ScrollView
            var sv = new GameObject("ScrollView",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(ScrollRect), typeof(LayoutElement));
            sv.transform.SetParent(parent, false);
            sv.GetComponent<Image>().color = new Color(0.05f, 0.04f, 0.03f, 0.6f);
            var svLe = sv.GetComponent<LayoutElement>();
            svLe.flexibleHeight = 1f; svLe.minHeight = 420f;

            // Viewport
            var vp = new GameObject("Viewport",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            vp.transform.SetParent(sv.transform, false);
            var vprt = vp.GetComponent<RectTransform>();
            vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one;
            vprt.offsetMin = new Vector2(6f, 6f); vprt.offsetMax = new Vector2(-6f, -6f);
            vp.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);  // 透明但要有 Image 才能 Mask
            vp.GetComponent<Mask>().showMaskGraphic = false;

            // Content
            var content = new GameObject("Content",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(vp.transform, false);
            var crt = content.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0f, 0f);

            var cvlg = content.GetComponent<VerticalLayoutGroup>();
            cvlg.padding = new RectOffset(8, 8, 8, 8);
            cvlg.spacing = 6f;
            cvlg.childAlignment = TextAnchor.UpperLeft;
            cvlg.childControlWidth = true;
            cvlg.childControlHeight = true;
            cvlg.childForceExpandWidth = true;
            cvlg.childForceExpandHeight = false;

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

        // ============ Lua → UI ============

        void TrySyncFromLua()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null || _listRoot == null) return;
                var fn = env.Global.Get<LuaFunction>("Rogue_GetActiveBuffIds");
                if (fn == null) return;
                var ret = fn.Call();
                fn.Dispose();
                LuaTable ids = (ret != null && ret.Length > 0) ? ret[0] as LuaTable : null;

                _curBuffIds.Clear();
                if (ids != null)
                {
                    int n = ids.Length;
                    for (int i = 1; i <= n; i++) _curBuffIds.Add(ids.Get<int, int>(i));
                    ids.Dispose();
                }

                var sb = new System.Text.StringBuilder();
                foreach (var id in _curBuffIds) sb.Append(id).Append(',');
                string hash = sb.ToString();
                if (hash == _lastHash) return;
                _lastHash = hash;

                RebuildList();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SkillCard] TrySyncFromLua: {e.Message}");
            }
#endif
        }

        void RebuildList()
        {
            if (_listRoot == null) return;
            for (int i = _listRoot.childCount - 1; i >= 0; i--)
            {
                var ch = _listRoot.GetChild(i);
                if (Application.isPlaying) Destroy(ch.gameObject);
                else DestroyImmediate(ch.gameObject);
            }

            var cm = ConfigManager.Instance;
            cm.LoadIfNeeded();

            if (_curBuffIds.Count == 0)
            {
                BuildEmptyHint();
                return;
            }
            foreach (var id in _curBuffIds)
            {
                string name = $"buff_{id}";
                string desc = "";
                try
                {
                    var row = cm.GetTableInfo("buff", "id", id);
                    if (row != null)
                    {
                        name = cm.GetValue<string>(row, "name", name);
                        desc = cm.GetValue<string>(row, "description", "");
                    }
                }
                catch { }
                BuildBuffEntry(name, desc);
            }
        }

        void BuildEmptyHint()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var go = new GameObject("Empty", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(_listRoot, false);
            var le = go.GetComponent<LayoutElement>();
            le.minHeight = 60f; le.preferredHeight = 60f;
            var t = go.GetComponent<Text>();
            t.text = "本局还未获得任何增益";
            t.font = font; t.fontSize = 18;
            t.color = new Color(0.7f, 0.7f, 0.65f, 1f);
            t.alignment = TextAnchor.MiddleCenter;
            t.raycastTarget = false;
        }

        void BuildBuffEntry(string name, string desc)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var entry = new GameObject("Buff",
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
                typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            entry.transform.SetParent(_listRoot, false);
            entry.GetComponent<Image>().color = new Color(0.22f, 0.18f, 0.12f, 0.9f);

            var evlg = entry.GetComponent<VerticalLayoutGroup>();
            evlg.padding = new RectOffset(10, 10, 6, 6);
            evlg.spacing = 3f;
            evlg.childControlWidth = true; evlg.childControlHeight = true;
            evlg.childForceExpandWidth = true; evlg.childForceExpandHeight = false;

            var efit = entry.GetComponent<ContentSizeFitter>();
            efit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            efit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // 名称
            var nameGo = new GameObject("Name", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            nameGo.transform.SetParent(entry.transform, false);
            var nt = nameGo.GetComponent<Text>();
            nt.text = name; nt.font = font; nt.fontSize = 19; nt.fontStyle = FontStyle.Bold;
            nt.color = new Color(1f, 0.95f, 0.75f, 1f);
            nt.alignment = TextAnchor.UpperLeft;
            nt.horizontalOverflow = HorizontalWrapMode.Wrap;
            nt.verticalOverflow = VerticalWrapMode.Overflow;
            nt.raycastTarget = false;

            if (!string.IsNullOrEmpty(desc))
            {
                var descGo = new GameObject("Desc", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                descGo.transform.SetParent(entry.transform, false);
                var dt = descGo.GetComponent<Text>();
                dt.text = desc; dt.font = font; dt.fontSize = 15;
                dt.color = new Color(0.82f, 0.82f, 0.74f, 1f);
                dt.alignment = TextAnchor.UpperLeft;
                dt.horizontalOverflow = HorizontalWrapMode.Wrap;
                dt.verticalOverflow = VerticalWrapMode.Overflow;
                dt.raycastTarget = false;
            }
        }
    }
}
