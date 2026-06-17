using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using HeroDefense.Core;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.UI
{
    /// <summary>
    /// 对局结算弹窗控制器（挂在 BattleHud 上，管理子节点 ResultPanel + RevivePanel）。
    ///
    /// 核心循环 P0-3（2026-05-18）：薄控制器，业务全在 Lua battle_manager.lua（Result_*）。
    ///
    /// 数据流（poll 0.3s）：
    ///   - 读 Battle_State.result_payload（整包 table，Lua Result_Build*Payload 填）
    ///   - payload.revive_available=true → 显 RevivePanel（失败首次，复活机会）
    ///   - payload.kind="victory"/"failed"  → 显 ResultPanel（结算页）
    ///   - payload==nil → 隐藏全部
    ///
    /// payload 字段全是标量 + 1 个 rewards 行数组（行内也是标量），C# 不做奖励计算。
    ///
    /// P1-5（2026-05-18）：rewards 行支持 kind=="task" —— 渲染进度文字 + 行内
    ///   Btn_Claim / Btn_DoubleAd 按钮（转 Lua Task_ClaimReward / Task_ClaimDouble）。
    ///   领取后本地即时刷新该行（_shownSig 不变 poll 不重渲）。
    ///
    /// 按钮点击全部转 Lua（Result_OnClick*）。0 SerializeField：transform.Find 路径查找。
    /// 节点缺失 → LogWarning 不崩（沿用容错风格）。
    /// </summary>
    public class BattleResultController : MonoBehaviour
    {
        // —— ResultPanel ——
        GameObject _panel;
        Text _titleText, _chapterText;
        Transform _rewardListRoot;
        GameObject _rewardRowTemplate;
        Button _btnNext, _btnBack, _btnDoubleAd, _btnRetry;

        // —— RevivePanel ——
        GameObject _revivePanel;
        Text _reviveTitle, _reviveDesc;
        Button _btnReviveAd, _btnGiveUp;

        // —— 模态遮罩（result + revive 共享，运行时构造为 BattleHud sibling）——
        // 全屏 raycastTarget 背景图：吸收所有点击 → 弹窗期间禁止操作其它面板 / 战场。
        // SetAsLastSibling + 面板再 SetAsLastSibling → 渲染在所有兄弟面板之上（HUD/背包/Pause/RewardChoose 等）。
        GameObject _modalBackdrop;
        const string MODAL_BACKDROP_NAME = "_ResultModalBackdrop";

        readonly List<GameObject> _rewardRows = new List<GameObject>();

        float _pollAccum;
        const float POLL_INTERVAL = 0.3f;
        // 当前已渲染的视图签名（避免每帧重渲）
        string _shownSig = "";

        // ⚠ 已迁移到热更 UI：结算/复活弹窗现由 Game/ui/wnd_battle_result.xml + lua/ui/wnd_battle_result.lua 实现
        //   （wnd_battle_result.lua 轮询 Battle_State.result_payload 渲染，按钮→Result_OnClick*）。本控制器置惰性、
        //   不再绑场景 ResultPanel/RevivePanel（场景节点保持 inactive），验证通过后将彻底移除组件 + 删脚本 + 清场景节点。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：结算弹窗已迁热更 UI（见上）
            ResolveNodes();
            BindButtons();
            if (_panel != null) _panel.SetActive(false);
            if (_revivePanel != null) _revivePanel.SetActive(false);
            if (_modalBackdrop != null) _modalBackdrop.SetActive(false);
            _shownSig = "";
        }

        void Update()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：渲染改由 wnd_battle_result.lua 轮询驱动
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            TrySyncFromLua();
        }

        // ============ 节点解析 ============

        void ResolveNodes()
        {
            _panel = transform.Find("ResultPanel")?.gameObject;
            if (_panel != null)
            {
                var p = _panel.transform;
                _titleText   = p.Find("Title")?.GetComponent<Text>();
                _chapterText = p.Find("ChapterText")?.GetComponent<Text>();
                _rewardListRoot = p.Find("RewardList");
                _rewardRowTemplate = _rewardListRoot != null
                    ? _rewardListRoot.Find("RewardRowTemplate")?.gameObject : null;
                _btnNext      = p.Find("Btn_Next")?.GetComponent<Button>();
                _btnBack      = p.Find("Btn_Back")?.GetComponent<Button>();
                _btnDoubleAd  = p.Find("Btn_DoubleAd")?.GetComponent<Button>();
                _btnRetry     = p.Find("Btn_Retry")?.GetComponent<Button>();
            }
            else
            {
                Debug.LogWarning("[BattleResult] 未找到 ResultPanel 子节点");
            }

            _revivePanel = transform.Find("RevivePanel")?.gameObject;
            if (_revivePanel != null)
            {
                var r = _revivePanel.transform;
                _reviveTitle = r.Find("Title")?.GetComponent<Text>();
                _reviveDesc  = r.Find("Desc")?.GetComponent<Text>();
                _btnReviveAd = r.Find("Btn_ReviveAd")?.GetComponent<Button>();
                _btnGiveUp   = r.Find("Btn_GiveUp")?.GetComponent<Button>();
            }
            else
            {
                Debug.LogWarning("[BattleResult] 未找到 RevivePanel 子节点（失败复活将不可用）");
            }
        }

        void BindButtons()
        {
            Bind(_btnNext,     () => CallLua("Result_OnClickNext"));
            Bind(_btnBack,     OnBackClicked);
            Bind(_btnDoubleAd, () => CallLua("Result_OnClickDoubleReward"));
            Bind(_btnRetry,    OnRetryClicked);
            Bind(_btnReviveAd, () => CallLua("Result_OnClickReviveAd"));
            Bind(_btnGiveUp,   () => CallLua("Result_OnClickGiveUp"));
        }

        static void Bind(Button btn, UnityEngine.Events.UnityAction action)
        {
            if (btn == null) return;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(action);
        }

        // ============ 按钮处理 ============

        void OnBackClicked()
        {
            // 先 Lua 结算入库，再卸载 GameScene 回主菜单
            CallLua("Result_OnClickClaimAndExit");
            UnloadGameScene();
        }

        void OnRetryClicked()
        {
            // Result_OnClickRetry 内部已切场景（重载 GameScene），C# 不再卸载
            CallLua("Result_OnClickRetry");
        }

        void UnloadGameScene()
        {
            // 2026-05-20 修：走 SceneLoader.ExitContentScene 同步清内部 _currentContentScene / _isLoading 状态
            // 2026-05-21 修：改用 LoadContentScene("MainScene") —— 真正切回 MainScene，否则 MainSceneController
            //   不会再触发 Start → 主菜单背景 SpriteRenderer 不再创建 → BG 消失。
            //   LoadContentScene 已包含「卸当前 + 装新」原子操作，同样清 _currentContentScene / _isLoading 状态。
            var loader = HeroDefense.Core.SceneLoader.Instance;
            if (loader != null)
            {
                loader.LoadContentScene("MainScene");
                return;
            }
            // 兜底（理论不会到这里）
            try
            {
                var gameScene = SceneManager.GetSceneByName("GameScene");
                if (gameScene.IsValid() && gameScene.isLoaded)
                    SceneManager.UnloadSceneAsync(gameScene);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleResult] unload GameScene: {e.Message}");
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
                var bs = env.Global.Get<LuaTable>("Battle_State");
                if (bs == null) { HideAll(); return; }

                LuaTable payload = null;
                try { payload = bs.Get<string, LuaTable>("result_payload"); } catch { }
                bs.Dispose();

                if (payload == null) { HideAll(); return; }

                bool reviveAvailable = GetBool(payload, "revive_available", false);
                string kind = GetStr(payload, "kind", "");

                // 视图签名：revive / kind 变化才重渲（rewards 同 kind 下不会变）
                string sig = (reviveAvailable ? "revive:" : "result:") + kind;
                if (sig == _shownSig) { payload.Dispose(); return; }
                _shownSig = sig;

                if (reviveAvailable)
                    ShowRevive(payload);
                else
                    ShowResult(payload);

                payload.Dispose();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BattleResult] sync: {e.Message}");
            }
#endif
        }

#if XLUA
        // ============ 复活面板 ============

        void ShowRevive(LuaTable payload)
        {
            if (_panel != null) _panel.SetActive(false);
            if (_revivePanel == null) return;

            if (_reviveTitle != null) _reviveTitle.text = GetStr(payload, "revive_title", "营地危急！");
            if (_reviveDesc != null)  _reviveDesc.text  = GetStr(payload, "revive_desc", "看广告召唤援军");
            ShowModalPanel(_revivePanel);
            Debug.Log("[BattleResult] show RevivePanel");
        }

        // ============ 结算页 ============

        void ShowResult(LuaTable payload)
        {
            if (_revivePanel != null) _revivePanel.SetActive(false);
            if (_panel == null) return;

            string kind = GetStr(payload, "kind", "victory");
            bool isVictory = kind == "victory";

            if (_titleText != null)
            {
                _titleText.text = GetStr(payload, "title", isVictory ? "凯旋！" : "营帐失守");
                _titleText.color = isVictory
                    ? new Color(1f, 0.85f, 0.3f)
                    : new Color(0.9f, 0.4f, 0.4f);
            }

            // 章末关结束文案
            if (_chapterText != null)
            {
                string ct = GetStr(payload, "chapter_finish_text", "");
                _chapterText.text = ct;
                _chapterText.gameObject.SetActive(!string.IsNullOrEmpty(ct));
            }

            RenderRewards(payload);

            // 按钮显隐
            int nextLevelId = GetInt(payload, "next_level_id", 0);
            bool canRetry = GetBool(payload, "can_retry", false);
            SetActive(_btnNext, nextLevelId > 0);
            SetActive(_btnRetry, canRetry);
            SetActive(_btnDoubleAd, true);
            SetActive(_btnBack, true);

            ShowModalPanel(_panel);
            Debug.Log($"[BattleResult] show ResultPanel kind={kind} next={nextLevelId} retry={canRetry}");
        }

        // 共享模态遮罩 + 置顶（result / revive 都走此入口）
        // 1. 确保 backdrop（全屏 raycastTarget 背景图）存在并 SetAsLastSibling
        // 2. 面板 SetAsLastSibling → 渲染在 backdrop 之上
        void ShowModalPanel(GameObject panel)
        {
            if (panel == null) return;
            var bd = EnsureModalBackdrop();
            if (bd != null)
            {
                bd.transform.SetAsLastSibling();
                bd.SetActive(true);
            }
            panel.transform.SetAsLastSibling();
            panel.SetActive(true);
        }

        // 全屏 raycastTarget 黑底图（α 0.7）。挂为 BattleHud sibling（this.transform 子节点），
        // 不动现有 ResultPanel / RevivePanel 布局。
        GameObject EnsureModalBackdrop()
        {
            if (_modalBackdrop != null) return _modalBackdrop;
            var found = transform.Find(MODAL_BACKDROP_NAME);
            if (found != null) { _modalBackdrop = found.gameObject; return _modalBackdrop; }

            var go = new GameObject(MODAL_BACKDROP_NAME, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            var img = go.GetComponent<Image>();
            img.color = new Color(0f, 0f, 0f, 0.7f);
            img.raycastTarget = true;
            go.SetActive(false);
            _modalBackdrop = go;
            return _modalBackdrop;
        }

        // 按 rewards 行数组逐行 clone 模板
        void RenderRewards(LuaTable payload)
        {
            // 清旧行
            foreach (var row in _rewardRows)
                if (row != null) Destroy(row);
            _rewardRows.Clear();

            if (_rewardListRoot == null || _rewardRowTemplate == null) return;

            LuaTable rewards = null;
            try { rewards = payload.Get<string, LuaTable>("rewards"); } catch { }
            if (rewards == null) return;

            int count = rewards.Length;
            for (int i = 1; i <= count; i++)
            {
                LuaTable row = null;
                try { row = rewards.Get<int, LuaTable>(i); } catch { }
                if (row == null) continue;

                string kind = GetStr(row, "kind", "");
                var go = Instantiate(_rewardRowTemplate, _rewardListRoot);
                go.name = $"RewardRow_{i}";
                go.SetActive(true);

                if (kind == "task")
                    RenderTaskRow(go, row);
                else
                    RenderNormalRow(go, row);

                row.Dispose();
                _rewardRows.Add(go);
            }
            rewards.Dispose();
        }

        // 普通奖励行（gold / exp / card / soldier）：纯文字
        void RenderNormalRow(GameObject go, LuaTable row)
        {
            string label = GetStr(row, "label", "");
            int amount = GetInt(row, "amount", 0);
            string cardId = GetStr(row, "card_id", "");
            string line = amount > 0 ? $"{label}  +{amount}"
                        : (!string.IsNullOrEmpty(cardId) ? $"{label}  {cardId}" : label);
            var txt = go.GetComponent<Text>() ?? go.GetComponentInChildren<Text>(true);
            if (txt != null) txt.text = line;
            // 普通行用不到的 2 个任务按钮保持 inactive
            SetChildActive(go.transform, "Btn_Claim", false);
            SetChildActive(go.transform, "Btn_DoubleAd", false);
        }

        // P1-5 任务行：进度文字 + 行内「领取」「翻倍」按钮
        void RenderTaskRow(GameObject go, LuaTable row)
        {
            string label = GetStr(row, "label", "任务");
            int progress = GetInt(row, "progress", 0);
            int target = GetInt(row, "target", 0);
            bool done = GetBool(row, "done", false);
            bool claimed = GetBool(row, "claimed", false);
            bool adDouble = GetBool(row, "ad_double", false);
            string taskId = GetStr(row, "task_id", "");

            var txt = go.GetComponent<Text>() ?? go.GetComponentInChildren<Text>(true);
            if (txt != null)
                txt.text = $"{label}  {progress}/{target}"
                         + (claimed ? "（已领）" : done ? "（可领）" : "");

            // 行内按钮：done && !claimed 才可领 / 翻倍
            var claimBtn = go.transform.Find("Btn_Claim")?.GetComponent<Button>();
            var doubleBtn = go.transform.Find("Btn_DoubleAd")?.GetComponent<Button>();

            bool canClaim = done && !claimed;
            if (claimBtn != null)
            {
                claimBtn.gameObject.SetActive(canClaim);
                claimBtn.onClick.RemoveAllListeners();
                string tid = taskId;
                claimBtn.onClick.AddListener(() => OnTaskClaim(go, tid, false));
            }
            if (doubleBtn != null)
            {
                doubleBtn.gameObject.SetActive(canClaim && adDouble);
                doubleBtn.onClick.RemoveAllListeners();
                string tid = taskId;
                doubleBtn.onClick.AddListener(() => OnTaskClaim(go, tid, true));
            }
        }

        // 任务领取：调 Lua → 本地即时刷新该行（R3：_shownSig 不变，poll 不重渲，必须本地刷新）
        void OnTaskClaim(GameObject rowGo, string taskId, bool doubleAd)
        {
            if (string.IsNullOrEmpty(taskId)) return;
            string fn = doubleAd ? "Task_ClaimDouble" : "Task_ClaimReward";
            try
            {
                var env = LuaHost.Env;
                if (env != null)
                {
                    var luaFn = env.Global.Get<LuaFunction>(fn);
                    if (luaFn != null) { luaFn.Call(taskId); luaFn.Dispose(); }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BattleResult] {fn}({taskId}): {e.Message}");
            }
            // 本地即时刷新：按钮隐藏 + 文字标「已领」
            if (rowGo != null)
            {
                SetChildActive(rowGo.transform, "Btn_Claim", false);
                SetChildActive(rowGo.transform, "Btn_DoubleAd", false);
                var txt = rowGo.GetComponent<Text>() ?? rowGo.GetComponentInChildren<Text>(true);
                if (txt != null && !txt.text.Contains("（已领）"))
                    txt.text = txt.text.Replace("（可领）", "") + "（已领）";
            }
            Debug.Log($"[BattleResult] task claim {taskId} double={doubleAd}");
        }

        static void SetChildActive(Transform parent, string childName, bool on)
        {
            var c = parent.Find(childName);
            if (c != null && c.gameObject.activeSelf != on)
                c.gameObject.SetActive(on);
        }
#endif

        void HideAll()
        {
            if (_panel != null && _panel.activeSelf) _panel.SetActive(false);
            if (_revivePanel != null && _revivePanel.activeSelf) _revivePanel.SetActive(false);
            if (_modalBackdrop != null && _modalBackdrop.activeSelf) _modalBackdrop.SetActive(false);
            _shownSig = "";
        }

        static void SetActive(Button btn, bool on)
        {
            if (btn != null && btn.gameObject.activeSelf != on)
                btn.gameObject.SetActive(on);
        }

        // ============ Lua 调用工具 ============

        static void CallLua(string fn)
        {
            LuaHost.CallGlobal(fn);
        }

#if XLUA
        static string GetStr(LuaTable t, string key, string def)
        {
            try { var v = t.Get<string, string>(key); return v ?? def; }
            catch { return def; }
        }

        static int GetInt(LuaTable t, string key, int def)
        {
            try { return t.Get<string, int>(key); }
            catch { return def; }
        }

        static bool GetBool(LuaTable t, string key, bool def)
        {
            try { return t.Get<string, bool>(key); }
            catch { return def; }
        }
#endif
    }
}
