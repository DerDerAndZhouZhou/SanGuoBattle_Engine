using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI
{
    /// <summary>
    /// 对局 HUD 控制器（挂在 UIWindow/RootWindow/BattleHud 上）。
    ///
    /// 数据来源：Lua 业务层（Economy_State.gold / Wave_State.current_wave / Battle_State.time_scale 等）
    /// 同步策略：每 0.2s 主动 poll 一次 Lua 状态（避免 Lua → C# 每次变化都 push 的复杂度）
    /// 按钮：OnClick → 调 Lua 全局函数 Battle_OnPauseClicked / Battle_OnSpeedToggleClicked
    ///
    /// 0 [SerializeField]：所有子节点用 Tag 查找。
    /// </summary>
    public class BattleHudController : MonoBehaviour
    {
        Text _goldText;
        Text _waveText;
        Text _speedText;
        RectTransform _expFillRT;
        Text _expText;

        Button _pauseBtn;
        Text _pauseBtnText;
        Button _speedBtn;
        Button _damageStatsBtn;   // 伤害列表按钮（用户 2026-06-02 重命名 名=Btn_damage，tag 仍 Btn_DamageStats）
        Button _skillCardBtn;     // buff 列表按钮（用户 2026-06-02 重命名 名=Btn_buff，tag 仍 Btn_SkillCard）
        // 商店按钮 Btn_shop 由 ShopController 自建于 SideBar 并自绑 Toggle（v7；此处不接管，避免双绑）

        float _pollAccum;
        const float POLL_INTERVAL = 0.2f;

        // ⚠ 已迁热更 UI：HUD 横条由 wnd_battle_hud.xml/.lua 实现（库存=wnd_inventory、商场=wnd_shop）。
        //   旧 BattleHud GO 由 BattleSceneController 始终 SetActive(false) 抑制，本控制器惰性；验证后删组件+脚本+场景节点。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI
            EnsureControllers();   // T208/T209 (2026-05-21)：保证 SkillCard / DamageStats controller 挂载（程序化构建 modal）
            ResolveChildren();
            BindButtons();
            HideDeprecatedButtons();   // 用户 2026-05-21：删 SideBar 上的"广告"按钮
            ApplyLayoutV2();           // R3c (2026-06-11)：HUD 重排（波次→经验条右 / 倍速暂停→SideBar 竖排 / 背包上移）
            PollAndUpdate();
        }

        // ============ R3c HUD 重排（2026-06-11，块4/F5；幂等——以目标父节点判重） ============
        //   1) WaveLabel：TopBar 中央 → BottomBar 经验条右侧（波次信息与经验同条带）
        //   2) SpeedToggleBtn + PauseBtn：TopBar 右上 → SideBar Btn_damage 下方竖排（操作按钮统一右列）
        //   3) InventoryPanel / ShopPanel 上移 24px（腾战场视野；Shop 由 ShopController 运行时建，延迟到 poll 内补）
        //   金币随商店显隐在 PollAndUpdate 内做（GoldLabel 仅商店开启时显示）。
        //   ⚠ 布局数值 = 视觉初稿，待用户截图拍板后微调。

        void ApplyLayoutV2()
        {
            try
            {
                var topBar = transform.Find("TopBar");
                var bottomBar = transform.Find("BottomBar");
                var sideBar = transform.Find("SideBar");

                // 1) 波次 + 倒计时 → 底栏正上方居中、放大（2026-06-15 用户：原贴屏幕底边、字小到几乎看不见 → 放大 + 上移）
                if (_waveText != null && bottomBar != null && _waveText.transform.parent != bottomBar)
                {
                    var rt = _waveText.GetComponent<RectTransform>();
                    _waveText.transform.SetParent(bottomBar, false);
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(560, 64);
                    rt.anchoredPosition = new Vector2(0, 80);    // 居中 + 上移离开屏幕底边（原 (252,0) 贴底太小）
                    _waveText.alignment = TextAnchor.MiddleCenter;
                    _waveText.resizeTextForBestFit = false;      // 防 best-fit 覆盖 fontSize
                    _waveText.fontSize = 48;                     // 32 → 48 放大
                    _waveText.horizontalOverflow = HorizontalWrapMode.Overflow;  // 防换行裁切
                }

                // 2) 倍速 + 暂停 → SideBar 竖排（Btn_damage=-107 下方，按既有 84 间距）
                if (sideBar != null)
                {
                    var sbRt = sideBar.GetComponent<RectTransform>();
                    if (sbRt != null && sbRt.sizeDelta.y < 480f)
                    {
                        // 撑高容纳 5 按钮，顶缘保持原位（原 center y=19, h=322 → 顶=180；新 h=490 → center=-65）
                        sbRt.sizeDelta = new Vector2(sbRt.sizeDelta.x, 490f);
                        sbRt.anchoredPosition = new Vector2(sbRt.anchoredPosition.x, -65f);
                    }
                    MoveBtnIntoSideBar(_speedBtn, sideBar, new Vector2(0, -191));
                    MoveBtnIntoSideBar(_pauseBtn, sideBar, new Vector2(0, -275));
                }

                // 3) 背包上移 24px（Shop 在 poll 内等 ShopController 建好后补移）
                var inv = transform.Find("InventoryPanel");
                if (inv != null)
                {
                    var rt = inv.GetComponent<RectTransform>();
                    if (rt != null && rt.anchoredPosition.y < -20f)
                        rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y + 24f);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BattleHudController] ApplyLayoutV2 失败: {e.Message}");
            }
        }

        static void MoveBtnIntoSideBar(Button btn, Transform sideBar, Vector2 ap)
        {
            if (btn == null || sideBar == null) return;
            if (btn.transform.parent == sideBar) return;   // 幂等
            var rt = btn.GetComponent<RectTransform>();
            btn.transform.SetParent(sideBar, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(96, 72);            // 与 SideBar 既有按钮同规格
            rt.anchoredPosition = ap;
        }

        void EnsureControllers()
        {
            // SkillCard / DamageStats 已迁热更 UI（wnd_skill_card / wnd_damage_stats，HUD 按钮转调 Lua toggle），不再挂 C# 控制器。
            // Shop 暂未迁（待后续批次），仍动态挂载。
            if (GetComponent<ShopController>() == null) gameObject.AddComponent<ShopController>();   // Phase4-D 商场
        }

        // 用户 2026-05-21 决定：删除 SideBar 上的"广告"按钮（Btn2_广告）。
        // scene 资产里有此节点，运行时 SetActive(false) 隐藏；其它兄弟按钮位置不变（各自固定 anchor）。
        void HideDeprecatedButtons()
        {
            var sb = transform.Find("SideBar");
            if (sb == null) return;
            var adBtn = sb.Find("Btn2_广告");
            if (adBtn != null && adBtn.gameObject.activeSelf)
            {
                adBtn.gameObject.SetActive(false);
                Debug.Log("[BattleHudController] SideBar/Btn2_广告 已隐藏");
            }
        }

        void Update()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：已迁热更 UI
            // R3c 自愈兜底：OnEnable 与 domain reload/场景激活时序偶发错过重排（实测一次）→ 首帧补一次。
            // ApplyLayoutV2 幂等（按目标父节点判重），多调无副作用。
            if (!_layoutApplied)
            {
                if (_waveText == null) ResolveChildren();
                ApplyLayoutV2();
                _layoutApplied = true;
            }

            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            PollAndUpdate();
        }
        bool _layoutApplied;

        void ResolveChildren()
        {
            _goldText = GameObject.FindWithTag("Label_Gold")?.GetComponent<Text>();
            // R3c：金币标签会被"随商店显隐"置 inactive → FindWithTag 找不到 → 子树深搜兜底（含 inactive）
            if (_goldText == null)
            {
                var t = FindDeep(transform, "GoldLabel");
                if (t != null) _goldText = t.GetComponent<Text>();
            }
            _waveText = GameObject.FindWithTag("Label_Wave")?.GetComponent<Text>();
            if (_waveText == null)
            {
                var t = FindDeep(transform, "WaveLabel");
                if (t != null) _waveText = t.GetComponent<Text>();
            }
            var speedGo = GameObject.FindWithTag("Btn_SpeedToggle");
            _speedBtn = speedGo?.GetComponent<Button>();
            _speedText = speedGo?.transform.Find("Text")?.GetComponent<Text>();
            var pauseGo = GameObject.FindWithTag("Btn_Pause");
            _pauseBtn = pauseGo?.GetComponent<Button>();
            _pauseBtnText = pauseGo?.transform.Find("Text")?.GetComponent<Text>();
            var bar = GameObject.FindWithTag("Bar_LevelExp");
            _expFillRT = bar?.transform.Find("Fill")?.GetComponent<RectTransform>();
            _expText = bar?.transform.Find("LevelText")?.GetComponent<Text>();

            // SideBar 三按钮（用户 2026-06-02 重命名）：Btn_damage 伤害列表 / Btn_buff buff列表 / Btn_shop 商店。
            // Tag 优先，Tag 未定义/找不到时退回按 GameObject 名在 BattleHud 子树深搜（兼容 tag 或 name）。
            _damageStatsBtn = FindUiButton("Btn_damage");
            _skillCardBtn   = FindUiButton("Btn_buff");
        }

        // 兼容 Tag 或 GameObject 名：先按 Tag 找（Tag 未定义会抛异常，吞掉），再退回 BattleHud 子树按名深搜。
        Button FindUiButton(string id)
        {
            GameObject go = null;
            try { go = GameObject.FindWithTag(id); } catch { go = null; }
            if (go == null)
            {
                var t = FindDeep(transform, id);
                go = t != null ? t.gameObject : null;
            }
            return go != null ? go.GetComponent<Button>() : null;
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindDeep(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }

        void BindButtons()
        {
            if (_pauseBtn != null)
            {
                _pauseBtn.onClick.RemoveAllListeners();
                _pauseBtn.onClick.AddListener(() => CallLua("Battle_OnPauseClicked"));
            }
            if (_speedBtn != null)
            {
                _speedBtn.onClick.RemoveAllListeners();
                _speedBtn.onClick.AddListener(() => CallLua("Battle_OnSpeedToggleClicked"));
            }
            // Phase 2.10 伤害统计按钮：toggle DamageStatsPanel（找不到 controller 时回退到 Tag 直接 SetActive）
            if (_damageStatsBtn != null)
            {
                _damageStatsBtn.onClick.RemoveAllListeners();
                _damageStatsBtn.onClick.AddListener(OnDamageStatsClicked);
            }
            // buff 列表按钮：toggle SkillCardPanel
            if (_skillCardBtn != null)
            {
                _skillCardBtn.onClick.RemoveAllListeners();
                _skillCardBtn.onClick.AddListener(OnSkillCardClicked);
            }
        }

        void OnSkillCardClicked()
        {
            // 已迁热更 UI：转调 Lua 全局 SkillCardWnd_Toggle（Game/lua/ui/wnd_skill_card.lua）
            CallLua("SkillCardWnd_Toggle");
        }

        void OnDamageStatsClicked()
        {
            // 已迁热更 UI：转调 Lua 全局 DmgStats_Toggle（Game/lua/ui/wnd_damage_stats.lua）
            CallLua("DmgStats_Toggle");
        }

        void PollAndUpdate()
        {
            try
            {
#if XLUA
                var env = LuaHost.Env;
                if (env == null) return;

                // R3c：金币随商店显隐（金币只在买卡场景有意义）
                // 用户 2026-06-12：金币挂进商店面板左上（标题「商场」右侧）；面板顶边由 ShopController 直接贴屏幕上沿
                var shopPanel = transform.Find("ShopPanel");
                bool shopOpen = shopPanel != null && shopPanel.gameObject.activeSelf;
                if (shopPanel != null && _goldText != null && _goldText.transform.parent != shopPanel)
                {
                    var grt = _goldText.GetComponent<RectTransform>();
                    _goldText.transform.SetParent(shopPanel, false);
                    grt.anchorMin = grt.anchorMax = new Vector2(0f, 1f);
                    grt.pivot = new Vector2(0f, 1f);
                    grt.sizeDelta = new Vector2(200f, 24f);
                    grt.anchoredPosition = new Vector2(96f, -4f);
                    _goldText.alignment = TextAnchor.UpperLeft;
                }

                int gold = env.Global.GetInPath<int>("Economy_State.gold");
                if (_goldText != null)
                {
                    // 修复(2026-06-14)：金币标签 rect 高 24 < fontSize 32 且 vOverflow=Truncate → 文字被竖直截断不渲染
                    //   = 商场内"看不到金币数量"的根因（wave 标签高 32 故正常）。一次性强制不截断 + 抬高度（含 inactive 也设）。
                    if (_goldText.verticalOverflow != VerticalWrapMode.Overflow)
                    {
                        _goldText.verticalOverflow = VerticalWrapMode.Overflow;
                        _goldText.horizontalOverflow = HorizontalWrapMode.Overflow;
                        var grt2 = _goldText.rectTransform;
                        if (grt2.sizeDelta.y < 36f)
                            grt2.sizeDelta = new Vector2(Mathf.Max(220f, grt2.sizeDelta.x), 40f);
                    }
                    _goldText.text = $"金币: {gold}";
                    if (_goldText.gameObject.activeSelf != shopOpen) _goldText.gameObject.SetActive(shopOpen);
                }

                int curWave = env.Global.GetInPath<int>("Wave_State.current_wave");
                int totalWaves = env.Global.GetInPath<int>("Wave_State.total_waves");
                if (totalWaves == 0) totalWaves = 5;
                // T204：prep_remaining > 0 时显示倒计时（"波次 1/5  下一波 15s"），否则正常文本
                float prepLeft = env.Global.GetInPath<float>("Wave_State.prep_remaining");
                if (_waveText != null)
                {
                    if (prepLeft > 0f)
                        _waveText.text = $"波次 {curWave}/{totalWaves}   下一波 {Mathf.CeilToInt(prepLeft)}s";
                    else
                        _waveText.text = $"波次 {curWave}/{totalWaves}";
                }

                float ts = env.Global.GetInPath<float>("Battle_State.time_scale");
                if (ts < 0.5f) ts = 1f;
                if (_speedText != null) _speedText.text = $"{ts:F0}x";

                bool paused = env.Global.GetInPath<bool>("Battle_State.paused");
                if (_pauseBtnText != null) _pauseBtnText.text = paused ? "继续" : "暂停";
                if (_pauseBtn != null)
                {
                    var img = _pauseBtn.GetComponent<Image>();
                    if (img != null) img.color = paused ? new Color(0.9f, 0.6f, 0.3f) : new Color(0.85f, 0.5f, 0.5f);
                }

                int level = env.Global.GetInPath<int>("Battle_State.level_in_battle");
                int exp = env.Global.GetInPath<int>("Battle_State.level_exp");
                int expMax = (level <= 0) ? 100 : level * 100;
                float pct = expMax > 0 ? Mathf.Clamp01((float)exp / expMax) : 0f;
                if (_expFillRT != null)
                {
                    var max = _expFillRT.anchorMax;
                    max.x = pct;
                    _expFillRT.anchorMax = max;
                }
                if (_expText != null) _expText.text = $"Lv.{level}   {exp}/{expMax} EXP";
#endif
            }
            catch { /* 启动早期 Lua 可能未就位，silent */ }
        }

        void CallLua(string fnName)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<XLua.LuaFunction>(fnName);
                if (fn != null) { fn.Call(); fn.Dispose(); }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleHudController] {fnName} 失败: {e.Message}");
            }
#endif
        }
    }
}
