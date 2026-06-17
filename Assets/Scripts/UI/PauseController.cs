using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI
{
    /// <summary>
    /// 暂停面板控制器（挂在 UIWindow/RootWindow/BattleHud 上）。
    ///
    /// 阶段 3-A：完全附加式（additive），不修改任何既有功能：
    ///   - poll Lua `Battle_State.paused` → 自动 显 / 隐 暂停遮罩
    ///   - 「继续」按钮 → 调既有 Lua 全局 Battle_OnPauseClicked（与 HUD 暂停按钮同款）
    ///   - 「返回主菜单」→ 卸载 GameScene（与 BattleResultController.UnloadGameScene 同款流程，
    ///     BSC.OnDisable 自动做 Lua 清理 + 显回主菜单）
    ///
    /// 0 [SerializeField]：遮罩与对话框全程序化构建。
    /// </summary>
    public class PauseController : MonoBehaviour
    {
        GameObject _overlay;     // 全屏半透明遮罩 + 居中对话框
        bool _builtOk;

        float _pollAccum;
        const float POLL_INTERVAL = 0.2f;
        bool _lastPaused;

        // ⚠ 已迁移到热更 UI：暂停面板现由 Game/ui/wnd_pause.xml + Game/lua/ui/wnd_pause.lua 实现
        //   （wnd_pause.lua 监听 "BattlePaused" 事件懒加载并显隐）。本 C# 控制器置惰性、不再构建遮罩，
        //   以免与 XML 面板重复。验证通过后将彻底移除本组件 + 删除本脚本（迁移收尾步）。
        static readonly bool MIGRATED_TO_XML = true;

        void OnEnable()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：不再程序化构建暂停遮罩（见上方迁移说明）
            EnsureOverlay();
            _lastPaused = false;
            if (_overlay != null) _overlay.SetActive(false);
        }

        void Update()
        {
            if (MIGRATED_TO_XML) return;   // 惰性：显隐改由 wnd_pause.lua 事件驱动
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < POLL_INTERVAL) return;
            _pollAccum = 0;
            SyncFromLua();
        }

        void SyncFromLua()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                bool paused = env.Global.GetInPath<bool>("Battle_State.paused");
                if (paused == _lastPaused) return;
                _lastPaused = paused;
                if (_overlay != null)
                {
                    if (paused) _overlay.transform.SetAsLastSibling();  // 置顶于其它面板之上
                    _overlay.SetActive(paused);
                }
            }
            catch { /* 启动早期 Lua 未就位，silent */ }
#endif
        }

        // ============ 程序化构建遮罩 ============

        void EnsureOverlay()
        {
            if (_builtOk && _overlay != null) return;
            try
            {
                var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

                // 全屏遮罩（半透明黑，raycastTarget=true 拦截点击）
                _overlay = new GameObject("PausePanel",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                _overlay.transform.SetParent(transform, false);
                var ort = _overlay.GetComponent<RectTransform>();
                ort.anchorMin = Vector2.zero; ort.anchorMax = Vector2.one;
                ort.offsetMin = Vector2.zero; ort.offsetMax = Vector2.zero;
                var oImg = _overlay.GetComponent<Image>();
                oImg.color = new Color(0f, 0f, 0f, 0.72f);
                oImg.raycastTarget = true;

                // 居中对话框
                var dlg = new GameObject("Dialog",
                    typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                dlg.transform.SetParent(_overlay.transform, false);
                var drt = dlg.GetComponent<RectTransform>();
                drt.anchorMin = new Vector2(0.5f, 0.5f);
                drt.anchorMax = new Vector2(0.5f, 0.5f);
                drt.pivot = new Vector2(0.5f, 0.5f);
                drt.sizeDelta = new Vector2(440f, 320f);
                drt.anchoredPosition = Vector2.zero;
                dlg.GetComponent<Image>().color = new Color(0.16f, 0.13f, 0.09f, 0.98f);

                // 标题「暂停」
                var titleGo = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer));
                titleGo.transform.SetParent(dlg.transform, false);
                var trt = titleGo.GetComponent<RectTransform>();
                trt.anchorMin = new Vector2(0f, 1f);
                trt.anchorMax = new Vector2(1f, 1f);
                trt.pivot = new Vector2(0.5f, 1f);
                trt.sizeDelta = new Vector2(0f, 80f);
                trt.anchoredPosition = new Vector2(0f, -24f);
                var titleTxt = titleGo.AddComponent<Text>();
                titleTxt.text = "暂停";
                titleTxt.font = font;
                titleTxt.fontSize = 44;
                titleTxt.fontStyle = FontStyle.Bold;
                titleTxt.color = new Color(1f, 0.93f, 0.6f, 1f);
                titleTxt.alignment = TextAnchor.MiddleCenter;
                titleTxt.raycastTarget = false;

                // 「继续」按钮
                BuildButton(dlg.transform, "Btn_Resume", "继续",
                    new Vector2(0f, -20f), new Color(0.32f, 0.5f, 0.28f, 1f), OnResumeClicked);
                // 「返回主菜单」按钮
                BuildButton(dlg.transform, "Btn_ToMenu", "返回主菜单",
                    new Vector2(0f, -110f), new Color(0.5f, 0.3f, 0.22f, 1f), OnReturnToMenuClicked);

                _overlay.SetActive(false);
                _builtOk = true;
                Debug.Log("[PauseController] PausePanel 程序化构建完成");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PauseController] EnsureOverlay 失败: {e.Message}");
            }
        }

        // 在对话框内构建一个按钮（锚点：上中，anchoredPosition 为相对顶部偏移）
        void BuildButton(Transform parent, string name, string label,
                         Vector2 anchoredPos, Color bg, UnityEngine.Events.UnityAction onClick)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var go = new GameObject(name,
                typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(280f, 72f);
            rt.anchoredPosition = anchoredPos;

            var img = go.GetComponent<Image>();
            img.color = bg;
            img.raycastTarget = true;

            var btn = go.GetComponent<Button>();
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(onClick);

            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
            txtGo.transform.SetParent(go.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero; txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = Vector2.zero; txtRt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.text = label;
            txt.font = font;
            txt.fontSize = 26;
            txt.color = new Color(1f, 0.96f, 0.85f, 1f);
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }

        // ============ 按钮处理 ============

        void OnResumeClicked()
        {
            CallLua("Battle_OnPauseClicked");  // 既有全局：toggle 暂停 → 恢复对局
            // poll 下一帧会同步隐藏；这里立即隐藏避免视觉延迟
            if (_overlay != null) _overlay.SetActive(false);
            _lastPaused = false;
        }

        void OnReturnToMenuClicked()
        {
            // 暂停态下 Time.timeScale 可能为 0 → 卸载前强制恢复，避免主菜单冻结
            Time.timeScale = 1f;
            if (_overlay != null) _overlay.SetActive(false);
            // 2026-05-20 修：走 SceneLoader.ExitContentScene 而不是直接 SceneManager.UnloadSceneAsync，
            //   否则 SceneLoader 的 _currentContentScene / _isLoading 会留陈旧值 → 下次「开始」时加载锁卡死。
            // 2026-05-21 修：改用 LoadContentScene("MainScene") 真正切回 MainScene，否则 MainSceneController
            //   不会再触发 Start → 主菜单背景 SpriteRenderer 不再创建 → BG 消失。
            // BSC.OnDisable 仍会随 GameScene 卸载自动跑 Lua 清理（Battle_OnSceneExit）+ 显回主菜单。
            var loader = HeroDefense.Core.SceneLoader.Instance;
            if (loader != null)
            {
                loader.LoadContentScene("MainScene");
            }
            else
            {
                // 兜底（理论不会到这里）
                try
                {
                    var gameScene = SceneManager.GetSceneByName("GameScene");
                    if (gameScene.IsValid() && gameScene.isLoaded)
                        SceneManager.UnloadSceneAsync(gameScene);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"[PauseController] 卸载 GameScene 失败: {e.Message}");
                }
            }
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
                Debug.LogError($"[PauseController] {fnName} 失败: {e.Message}");
            }
#endif
        }
    }
}
