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
    /// 兵种 / 武将 / 建筑详情面板控制器（阶段 3-C / Task #179）。
    ///
    /// 阶段 3-C：完全附加式（additive），不修改任何既有功能：
    ///   - 挂在 UIWindow/RootWindow/BattleHud 上（与 InventoryController 同款）
    ///   - 每 0.3s poll Lua `Battle_GetInspectTarget()` → nil（无）或 table {kind,id,handle}
    ///       kind ∈ {"unit","monster","building"}（unit/building 查 npc.txt，monster 查 monster.txt）
    ///   - target 非 nil → C# 直读 ConfigManager 配置表取 立绘 / 名称 / 数值 → 渲染面板
    ///   - target 为 nil → 隐藏面板
    ///   - 关闭按钮 → 调 Lua `Battle_ClearInspectTarget()`
    ///
    /// 触发说明：「点击场上单位弹详情」的触发（Battle_SetInspectTarget 的调用方）需改
    /// DragInputBridge 区分点按 / 拖拽，留用户决策。本控制器为「触发就绪、待接线」——
    /// 一旦有人调 Battle_SetInspectTarget，本面板即自动显示。
    ///
    /// 0 [SerializeField]：面板与所有子节点全程序化构建（CLAUDE.md §1.2）。
    /// </summary>
    public class UnitDetailController : MonoBehaviour
    {
        GameObject _panel;
        Image _portrait;
        Text _nameText;
        Text _statsText;
        bool _builtOk;

        float _pollAccum;
        const float POLL_INTERVAL = 0.3f;

        // dirty 检测：当前已渲染的 target 标识（kind|id|handle）
        string _lastKey = "";

        void OnEnable()
        {
            EnsurePanel();
            _lastKey = "";
            TrySyncFromLua();
        }

        void Update()
        {
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            TrySyncFromLua();
        }

        // ============ 程序化构建面板 ============

        void EnsurePanel()
        {
            if (_builtOk && _panel != null) return;
            try
            {
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

                // 详情面板（半透明底；用户 2026-06-12：屏幕正中）
                _panel = new GameObject("UnitDetailPanel",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                _panel.transform.SetParent(transform, false);
                var prt = _panel.GetComponent<RectTransform>();
                prt.anchorMin = new Vector2(0.5f, 0.5f);
                prt.anchorMax = new Vector2(0.5f, 0.5f);
                prt.pivot = new Vector2(0.5f, 0.5f);
                prt.anchoredPosition = Vector2.zero;
                prt.sizeDelta = new Vector2(320f, 420f);
                _panel.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.05f, 0.88f);

                // 标题（名称）
                var nameGo = new GameObject("NameLabel", typeof(RectTransform), typeof(CanvasRenderer));
                nameGo.transform.SetParent(_panel.transform, false);
                var nrt = nameGo.GetComponent<RectTransform>();
                nrt.anchorMin = new Vector2(0f, 1f);
                nrt.anchorMax = new Vector2(1f, 1f);
                nrt.pivot = new Vector2(0.5f, 1f);
                nrt.sizeDelta = new Vector2(-16f, 40f);
                nrt.anchoredPosition = new Vector2(0f, -8f);
                _nameText = nameGo.AddComponent<Text>();
                _nameText.font = font;
                _nameText.fontSize = 22;
                _nameText.fontStyle = FontStyle.Bold;
                _nameText.color = new Color(1f, 0.9f, 0.55f, 1f);
                _nameText.alignment = TextAnchor.MiddleCenter;
                _nameText.raycastTarget = false;

                // 立绘
                var portraitGo = new GameObject("Portrait", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                portraitGo.transform.SetParent(_panel.transform, false);
                var portRt = portraitGo.GetComponent<RectTransform>();
                portRt.anchorMin = new Vector2(0.5f, 1f);
                portRt.anchorMax = new Vector2(0.5f, 1f);
                portRt.pivot = new Vector2(0.5f, 1f);
                portRt.sizeDelta = new Vector2(180f, 180f);
                portRt.anchoredPosition = new Vector2(0f, -52f);
                _portrait = portraitGo.GetComponent<Image>();
                _portrait.color = new Color(1f, 1f, 1f, 1f);
                _portrait.preserveAspect = true;
                _portrait.raycastTarget = false;

                // 数值文本
                var statsGo = new GameObject("StatsLabel", typeof(RectTransform), typeof(CanvasRenderer));
                statsGo.transform.SetParent(_panel.transform, false);
                var srt = statsGo.GetComponent<RectTransform>();
                srt.anchorMin = new Vector2(0f, 0f);
                srt.anchorMax = new Vector2(1f, 1f);
                srt.pivot = new Vector2(0.5f, 0.5f);
                srt.offsetMin = new Vector2(16f, 52f);     // 底部 52 给关闭按钮留位
                srt.offsetMax = new Vector2(-16f, -240f);  // 顶部 240 给名称 + 立绘留位
                _statsText = statsGo.AddComponent<Text>();
                _statsText.font = font;
                _statsText.fontSize = 17;
                _statsText.color = new Color(0.92f, 0.92f, 0.85f, 1f);
                _statsText.alignment = TextAnchor.UpperLeft;
                _statsText.horizontalOverflow = HorizontalWrapMode.Wrap;
                _statsText.verticalOverflow = VerticalWrapMode.Overflow;
                _statsText.raycastTarget = false;

                // 关闭按钮（底部居中）
                var closeGo = new GameObject("CloseBtn",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                closeGo.transform.SetParent(_panel.transform, false);
                var crt = closeGo.GetComponent<RectTransform>();
                crt.anchorMin = new Vector2(0.5f, 0f);
                crt.anchorMax = new Vector2(0.5f, 0f);
                crt.pivot = new Vector2(0.5f, 0f);
                crt.sizeDelta = new Vector2(160f, 38f);
                crt.anchoredPosition = new Vector2(0f, 8f);
                closeGo.GetComponent<Image>().color = new Color(0.35f, 0.22f, 0.1f, 0.95f);

                var closeTxtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
                closeTxtGo.transform.SetParent(closeGo.transform, false);
                var ctRt = closeTxtGo.GetComponent<RectTransform>();
                ctRt.anchorMin = Vector2.zero; ctRt.anchorMax = Vector2.one;
                ctRt.offsetMin = Vector2.zero; ctRt.offsetMax = Vector2.zero;
                var closeTxt = closeTxtGo.AddComponent<Text>();
                closeTxt.text = "关闭";
                closeTxt.font = font;
                closeTxt.fontSize = 18;
                closeTxt.color = new Color(1f, 0.95f, 0.7f, 1f);
                closeTxt.alignment = TextAnchor.MiddleCenter;
                closeTxt.raycastTarget = false;

                var closeBtn = closeGo.GetComponent<Button>();
                closeBtn.onClick.RemoveAllListeners();
                closeBtn.onClick.AddListener(OnCloseClick);

                _panel.SetActive(false);
                _builtOk = true;
                Debug.Log("[UnitDetailController] UnitDetailPanel 程序化构建完成");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[UnitDetailController] EnsurePanel 失败: {e.Message}");
            }
        }

        // ============ Lua → UI 同步 ============

        void TrySyncFromLua()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Battle_GetInspectTarget");
                if (fn == null) return;
                var ret = fn.Call();
                fn.Dispose();

                LuaTable target = (ret != null && ret.Length > 0) ? ret[0] as LuaTable : null;
                if (target == null)
                {
                    // 无检视目标 → 隐藏
                    if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
                    _lastKey = "";
                    return;
                }

                string kind = SafeGetString(target, "kind", "unit");
                int id = SafeGetInt(target, "id", 0);
                long handle = SafeGetLong(target, "handle", 0L);
                target.Dispose();

                string key = kind + "|" + id + "|" + handle;
                if (key == _lastKey) return;   // 同一目标已渲染，跳过
                _lastKey = key;

                Render(kind, id);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnitDetail] TrySyncFromLua: {e.Message}");
            }
#endif
        }

        // 渲染指定目标。kind=unit/building 查 npc.txt；kind=monster 查 monster.txt。
        void Render(string kind, int id)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null)
                {
                    if (_panel != null) _panel.SetActive(false);
                    return;
                }
                cm.LoadIfNeeded();

                bool isMonster = (kind == "monster");
                string table = isMonster ? "monster" : "npc";
                string nameField = isMonster ? "name_cn" : "name";

                var row = cm.GetTableInfo(table, "id", id);
                if (row == null)
                {
                    Debug.LogWarning($"[UnitDetail] {table} id={id} 未找到");
                    if (_panel != null) _panel.SetActive(false);
                    return;
                }

                string name = cm.GetValue<string>(row, nameField, $"{table}_{id}");
                string spriteKey = cm.GetValue<string>(row, "sprite_key", "");

                if (_nameText != null) _nameText.text = name;

                // 立绘：与 InventoryController 同款，尝试 idle / walk 帧 0
                if (_portrait != null)
                {
                    Sprite sprite = null;
                    if (!string.IsNullOrEmpty(spriteKey))
                    {
                        sprite = ResourceHost.LoadSprite($"art/{spriteKey}_idle_0.png")
                              ?? ResourceHost.LoadSprite($"art/{spriteKey}_walk_0.png")
                              ?? ResourceHost.LoadSprite($"art/{spriteKey}.png");
                    }
                    _portrait.sprite = sprite;
                    _portrait.enabled = (sprite != null);
                }

                // 主要数值（仅显示该表存在的字段）
                var lines = new List<string>();
                if (isMonster)
                {
                    AppendInt(lines, "生命", row, "hp");
                    AppendInt(lines, "攻击", row, "atk");
                    AppendFloat(lines, "移速", row, "speed");
                    AppendFloat(lines, "射程", row, "atk_range");
                    AppendFloat(lines, "攻击间隔", row, "atk_interval");
                }
                else
                {
                    AppendInt(lines, "生命", row, "hp");
                    AppendInt(lines, "攻击", row, "atk");
                    AppendFloat(lines, "攻速", row, "atk_speed");
                    int _hp = ConfigManager.Instance.GetValue<int>(row, "hp", 0);
                    int _atk = ConfigManager.Instance.GetValue<int>(row, "atk", 0);
                    float _aspd = ConfigManager.Instance.GetValue<float>(row, "atk_speed", 0f);
                    lines.Add($"战力：{(int)(_hp * _atk * _aspd)}");
                }
                if (_statsText != null) _statsText.text = string.Join("\n", lines);

                if (_panel != null) _panel.SetActive(true);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnitDetail] Render: {e.Message}");
                if (_panel != null) _panel.SetActive(false);
            }
        }

        void AppendInt(List<string> lines, string label, Dictionary<string, object> row, string field)
        {
            if (row.ContainsKey(field))
                lines.Add($"{label}：{ConfigManager.Instance.GetValue<int>(row, field, 0)}");
        }

        void AppendFloat(List<string> lines, string label, Dictionary<string, object> row, string field)
        {
            if (row.ContainsKey(field))
                lines.Add($"{label}：{ConfigManager.Instance.GetValue<float>(row, field, 0f):0.##}");
        }

        // ============ 关闭 ============

        void OnCloseClick()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env != null)
                {
                    var fn = env.Global.Get<LuaFunction>("Battle_ClearInspectTarget");
                    if (fn != null)
                    {
                        fn.Call();
                        fn.Dispose();
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[UnitDetail] OnCloseClick: {e.Message}");
            }
#endif
            // 立即隐藏，不必等下一轮 poll
            if (_panel != null) _panel.SetActive(false);
            _lastKey = "";
        }

#if XLUA
        static string SafeGetString(LuaTable t, string key, string def)
        {
            try { var v = t.Get<string, string>(key); return v ?? def; } catch { return def; }
        }
        static int SafeGetInt(LuaTable t, string key, int def)
        {
            try { return t.Get<string, int>(key); } catch { return def; }
        }
        static long SafeGetLong(LuaTable t, string key, long def)
        {
            try { return t.Get<string, long>(key); } catch { return def; }
        }
#endif
    }
}
