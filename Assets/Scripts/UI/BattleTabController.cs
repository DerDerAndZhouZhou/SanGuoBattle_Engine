using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI
{
    /// <summary>
    /// Battle Tab 入口控制器（挂在 RootWindow/MainWindow/BattleTab 上）。
    ///
    /// 薄控制器，两块职责：
    ///   1. 关卡选择窗口（LevelSelectWindow）—— 缩略图 + 关卡名 + 通关状态 + 左右切换箭头。
    ///      渲染数据全部 poll Lua `LevelSelect_*`（battle_manager.lua），C# 不持有关卡状态。
    ///   2. 「开始战斗」按钮 → Lua `LevelSelect_StartSelected()` 进入【当前选中】关卡。
    ///
    /// 0 SerializeField：所有节点按子节点名查找（UIFinder.FindChildByName）。
    /// </summary>
    public class BattleTabController : MonoBehaviour
    {
        Button _startBtn;
        Button _prevBtn;
        Button _nextBtn;
        Image  _thumbImg;
        Text   _nameText;
        Text   _statusText;
        bool   _wired;

        // 0.2s 轮询 Lua 状态（避免 Lua 未就位时 OnEnable 一次性刷的拿不到值 → 按钮 interactable 卡死的 bug，2026-05-21）
        float _pollAccum;
        const float POLL_INTERVAL = 0.2f;

        void Start() => Wire();

        // Tab 切换可能在 Start 之前 / 之后 → 再绑一次（_wired 守卫幂等）
        void OnEnable()
        {
            Wire();
            // 每次 Tab 显示时刷新窗口（选中关可能在局内已推进）
            RefreshLevelSelect();
            _pollAccum = 0f;
        }

        void Update()
        {
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0f;
            RefreshLevelSelect();
        }

        void Wire()
        {
            if (_wired) return;

            _startBtn   = UIFinder.FindChildByName<Button>(transform, "StartBattleButton");
            _prevBtn    = UIFinder.FindChildByName<Button>(transform, "PrevLevelButton");
            _nextBtn    = UIFinder.FindChildByName<Button>(transform, "NextLevelButton");
            _thumbImg   = UIFinder.FindChildByName<Image>(transform, "LevelThumb");
            _nameText   = UIFinder.FindChildByName<Text>(transform, "LevelNameText");
            _statusText = UIFinder.FindChildByName<Text>(transform, "LevelStatusText");

            if (_startBtn == null)
            {
                Debug.LogWarning("[BattleTabController] StartBattleButton 未找到");
                return;
            }
            _startBtn.onClick.AddListener(OnStartBattle);

            if (_prevBtn != null) _prevBtn.onClick.AddListener(OnPrevLevel);
            if (_nextBtn != null) _nextBtn.onClick.AddListener(OnNextLevel);

            _wired = true;
            Debug.Log("[BattleTabController] LevelSelectWindow + StartBattleButton 已绑定");
        }

        void OnStartBattle()
        {
            Debug.Log("[BattleTabController] 开始战斗 → 进入选中关卡");
            LuaHost.CallGlobal("LevelSelect_StartSelected");
        }

        void OnPrevLevel()
        {
            LuaHost.CallGlobal("LevelSelect_Prev");
            RefreshLevelSelect();
        }

        void OnNextLevel()
        {
            LuaHost.CallGlobal("LevelSelect_Next");
            RefreshLevelSelect();
        }

        /// <summary>poll Lua LevelSelect_* → 刷新缩略图 / 关卡名 / 通关状态 / 箭头可点状态。</summary>
        void RefreshLevelSelect()
        {
#if XLUA
            var env = LuaHost.Env;
            if (env == null) return;
            try
            {
                // 缩略图（支持 .png / .jpg 双扩展名回落，用户可能直接放 jpg）
                if (_thumbImg != null)
                {
                    string thumbKey = CallString("LevelSelect_GetThumbKey");
                    if (!string.IsNullOrEmpty(thumbKey))
                    {
                        var sp = LuaHost.LoadSprite("art/bg/" + thumbKey + ".png", logMissing: false)
                              ?? LuaHost.LoadSprite("art/bg/" + thumbKey + ".jpg", logMissing: false);
                        if (sp != null) _thumbImg.sprite = sp;
                    }
                }

                // 关卡名
                if (_nameText != null)
                {
                    string name = CallString("LevelSelect_GetName");
                    if (!string.IsNullOrEmpty(name)) _nameText.text = name;
                }

                // 通关状态文字
                if (_statusText != null)
                {
                    bool cleared = CallBool("LevelSelect_IsSelectedCleared");
                    _statusText.text = cleared ? "已通关" : "未通关";
                    _statusText.color = cleared
                        ? new Color(0.45f, 0.85f, 0.45f, 1f)
                        : new Color(0.85f, 0.85f, 0.85f, 1f);
                }

                // 箭头边界禁用
                if (_prevBtn != null) _prevBtn.interactable = CallBool("LevelSelect_HasPrev");
                if (_nextBtn != null) _nextBtn.interactable = CallBool("LevelSelect_HasNext");
            }
            catch (System.Exception e)
            {
                // 启动早期 Lua 可能未就位 → silent，下次 OnEnable 再刷
                Debug.LogWarning("[BattleTabController] RefreshLevelSelect: " + e.Message);
            }
#endif
        }

#if XLUA
        // 调 Lua 全局函数取 string 返回值（缺失 / 异常 → ""）
        static string CallString(string fnName)
        {
            var env = LuaHost.Env;
            if (env == null) return "";
            var fn = env.Global.Get<XLua.LuaFunction>(fnName);
            if (fn == null) return "";
            try
            {
                var ret = fn.Call();
                if (ret != null && ret.Length > 0 && ret[0] != null) return ret[0].ToString();
                return "";
            }
            finally { fn.Dispose(); }
        }

        // 调 Lua 全局函数取 bool 返回值（缺失 / 异常 → false）
        static bool CallBool(string fnName)
        {
            var env = LuaHost.Env;
            if (env == null) return false;
            var fn = env.Global.Get<XLua.LuaFunction>(fnName);
            if (fn == null) return false;
            try
            {
                var ret = fn.Call();
                if (ret != null && ret.Length > 0 && ret[0] is bool b) return b;
                return false;
            }
            finally { fn.Dispose(); }
        }
#endif
    }
}
