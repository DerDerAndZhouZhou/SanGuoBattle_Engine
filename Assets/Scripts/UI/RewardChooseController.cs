using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.UI
{
    /// <summary>
    /// 升级 3 选 1 奖励弹窗控制器（挂在 BattleHud 上，管理子节点 RewardChoosePanel）。
    ///
    /// 数据流：
    ///   - 0.2s poll Rogue_State.current_choices
    ///   - 非 nil → 显示 panel + 填 3 个按钮 (id/name/rarity/desc) + 启 10s 倒计时
    ///   - = nil → 隐藏 panel
    ///   - 按钮 OnClick → 调 Lua Rogue_PickChoice(1/2/3)
    ///   - 倒计时由 Lua 端 Timer_After 兜底（自动选第一项），C# 仅显示
    ///
    /// 控制器挂在 BattleHud 上而非 RewardChoosePanel 上，因为：
    ///   - RewardChoosePanel 默认 inactive，挂在上面则 OnEnable / Update 不触发
    ///   - BattleHud 切对局时变 active → 控制器开始 poll
    ///
    /// 0 [SerializeField]：全部路径查找
    /// </summary>
    public class RewardChooseController : MonoBehaviour
    {
        GameObject _panel;
        Text _titleText, _countdownText;
        Button[] _choiceBtns = new Button[3];
        Text[] _choiceNameTexts = new Text[3];
        Text[] _choiceDescTexts = new Text[3];

        float _pollAccum;
        const float POLL_INTERVAL = 0.2f;

        bool _isShown;
        int _lastChoicesHash;

        // ⚠ 已迁移到热更 UI：升级三选一现由 Game/ui/wnd_reward_choose.xml + lua/ui/wnd_reward_choose.lua 实现
        //   （wnd_reward_choose.lua 轮询 Rogue_State.current_choices 渲染，选项→Rogue_PickChoice）。本控制器置惰性，
        //   场景 RewardChoosePanel 保持 inactive，验证通过后将彻底移除组件 + 删脚本 + 清场景节点。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：升级三选一已迁热更 UI（见上）
            // this 挂在 BattleHud 上；查找子节点 RewardChoosePanel
            _panel = transform.Find("RewardChoosePanel")?.gameObject;
            ResolveChildren();
            BindButtons();
            if (_panel != null) _panel.SetActive(false); // 初始隐藏
        }

        void Update()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：渲染改由 wnd_reward_choose.lua 轮询驱动
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            TrySyncFromLua();
            if (_isShown) RefreshCountdown();
        }

        // 倒计时显示直接读权威 Lua 计时器（Rogue_GetChoiceTimeLeft = 真实 Timer 的 delay-elapsed），
        // 与自动选择计时器同源；暂停时该 Timer 随 LuaManager dt=0 冻结 → 显示也一起冻结，不再独立漂移。
        void RefreshCountdown()
        {
#if XLUA
            if (_countdownText == null) return;
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Rogue_GetChoiceTimeLeft");
                if (fn == null) return;
                var r = fn.Call();
                fn.Dispose();
                float left = (r != null && r.Length > 0 && r[0] != null)
                    ? System.Convert.ToSingle(r[0]) : 0f;
                _countdownText.text = string.Format("{0:0.0}s", Mathf.Max(0f, left));
            }
            catch { /* silent */ }
#endif
        }

        void ResolveChildren()
        {
            if (_panel == null) return;
            var p = _panel.transform;
            _titleText = p.Find("Title")?.GetComponent<Text>();
            _countdownText = p.Find("Countdown")?.GetComponent<Text>();
            var btnRow = p.Find("Choices");
            if (btnRow == null) return;
            for (int i = 0; i < 3; i++)
            {
                var btnGo = btnRow.Find("Choice_" + i);
                if (btnGo == null) continue;
                _choiceBtns[i] = btnGo.GetComponent<Button>();
                _choiceNameTexts[i] = btnGo.Find("Name")?.GetComponent<Text>();
                _choiceDescTexts[i] = btnGo.Find("Desc")?.GetComponent<Text>();
            }
        }

        void BindButtons()
        {
            for (int i = 0; i < 3; i++)
            {
                if (_choiceBtns[i] == null) continue;
                int idx = i + 1; // Lua 1-based
                _choiceBtns[i].onClick.RemoveAllListeners();
                _choiceBtns[i].onClick.AddListener(() => OnChoiceClicked(idx));
            }
        }

        void OnChoiceClicked(int luaIndex)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Rogue_PickChoice");
                if (fn != null)
                {
                    fn.Call(luaIndex);
                    fn.Dispose();
                    TrySyncFromLua();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[RewardChoose] OnChoice {luaIndex}: {e.Message}");
            }
#endif
        }

        // ============ Lua → UI 同步 ============

        struct ChoiceRec { public int id; public string name; public string rarity; public string desc; }
        readonly List<ChoiceRec> _curChoices = new List<ChoiceRec>();

        void TrySyncFromLua()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var rogueState = env.Global.Get<LuaTable>("Rogue_State");
                if (rogueState == null) { Hide(); return; }
                var choices = rogueState.Get<LuaTable>("current_choices");
                rogueState.Dispose();

                if (choices == null)
                {
                    Hide();
                    return;
                }

                _curChoices.Clear();
                int n = choices.Length;
                int hash = 17;
                for (int i = 1; i <= n; i++)
                {
                    var row = choices.Get<int, LuaTable>(i);
                    if (row == null) continue;
                    int id = TryGetInt(row, "id");
                    string nm = TryGetString(row, "name", "?");
                    string rar = TryGetString(row, "rarity", "rCommon");
                    string desc = TryGetString(row, "description", "");
                    _curChoices.Add(new ChoiceRec { id = id, name = nm, rarity = rar, desc = desc });
                    hash = hash * 31 + id;
                    row.Dispose();
                }
                choices.Dispose();

                if (hash != _lastChoicesHash)
                {
                    _lastChoicesHash = hash;
                    Show();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[RewardChoose] sync: {e.Message}");
            }
#endif
        }

#if XLUA
        static int TryGetInt(LuaTable row, string key)
        {
            try { return row.Get<string, int>(key); } catch { return 0; }
        }
        static string TryGetString(LuaTable row, string key, string def)
        {
            try { var v = row.Get<string, string>(key); return v ?? def; } catch { return def; }
        }
#endif

        void Show()
        {
            _isShown = true;
            if (_titleText != null) _titleText.text = "升级！选择奖励";
            for (int i = 0; i < 3; i++)
            {
                bool has = i < _curChoices.Count;
                if (_choiceBtns[i] != null) _choiceBtns[i].gameObject.SetActive(has);
                if (!has) continue;
                var c = _curChoices[i];
                if (_choiceNameTexts[i] != null) _choiceNameTexts[i].text = $"{c.name}\n[{ShortRarity(c.rarity)}]";
                if (_choiceDescTexts[i] != null) _choiceDescTexts[i].text = c.desc;
            }
            if (_panel != null)
            {
                // 置顶：RewardChoosePanel 自身是全屏 raycastTarget 背景图 —— 移到最后一个兄弟
                // → 渲染在 HUD / 背包 / 其它面板之上，挡住一切点击（模态），必须选完才能操作。
                _panel.transform.SetAsLastSibling();
                _panel.SetActive(true);
            }
            Debug.Log($"[RewardChoose] show {_curChoices.Count} choices");
        }

        void Hide()
        {
            if (!_isShown) return;
            _isShown = false;
            _lastChoicesHash = 0;
            if (_panel != null) _panel.SetActive(false);
        }

        static string ShortRarity(string r)
        {
            switch (r)
            {
                case "rCommon": return "白";
                case "rRare":   return "蓝";
                case "rEpic":   return "紫";
                case "rMythic": return "橙";
                default: return r;
            }
        }
    }
}
