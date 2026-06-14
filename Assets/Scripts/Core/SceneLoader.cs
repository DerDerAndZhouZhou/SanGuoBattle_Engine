using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using HeroDefense.Utils;

namespace HeroDefense.Core
{
    /// <summary>
    /// 场景加载管理器 — BootScene 常驻 + UIWindow 常驻 + 内容场景按需加载卸载。
    ///
    /// ⚠️ 重要：切内容场景时 **必须** 用 `yield return loadOp`，不要 `while(!isDone) yield null`。
    /// Tuanjie/Unity 2022 下 additive 加载偶现 isDone 卡住导致协程挂起，对标项目 findings.md 已记录。
    /// </summary>
    public class SceneLoader : SingletonMono<SceneLoader>
    {
        private string _currentContentScene = "";
        private bool _isUIWindowLoaded = false;
        private bool _isLoading = false;

        public bool IsLoading => _isLoading;
        public string CurrentContentScene => _currentContentScene;

        /// <summary>加载持久 UI 场景。由 BootInitializer 调用。</summary>
        public void InitializeUIWindow(Action onComplete = null)
        {
            if (!_isUIWindowLoaded)
            {
                StartCoroutine(LoadSceneAdditiveCoroutine("UIWindow", () =>
                {
                    _isUIWindowLoaded = true;
                    Debug.Log("[SceneLoader] UIWindow 加载完成（持久UI）");
                    onComplete?.Invoke();
                }));
            }
            else
            {
                onComplete?.Invoke();
            }
        }

        /// <summary>加载内容场景（自动卸载当前内容场景）。</summary>
        public void LoadContentScene(string sceneName, Action onComplete = null)
        {
            if (_isLoading)
            {
                Debug.LogWarning("[SceneLoader] 正在加载中，忽略重复请求");
                return;
            }

            StartCoroutine(SwitchContentSceneCoroutine(sceneName, onComplete));
        }

        public void LoadSceneAdditive(string sceneName, Action onComplete = null)
        {
            StartCoroutine(LoadSceneAdditiveCoroutine(sceneName, onComplete));
        }

        public void UnloadScene(string sceneName, Action onComplete = null)
        {
            StartCoroutine(UnloadSceneCoroutine(sceneName, onComplete));
        }

        /// <summary>
        /// 从内容场景退回 UIWindow（主菜单）—— 卸载当前内容场景并清理 SceneLoader 内部状态。
        /// PauseController「返回主菜单」/ BattleResultController「OnBack」走这里，不要再直接 SceneManager.UnloadSceneAsync，
        /// 否则会让 _currentContentScene / _isLoading 留下陈旧值，下一次 LoadContentScene 会试图卸已不存在的场景 → 协程抛异常死掉 → 加载锁永远卡住。
        /// </summary>
        public void ExitContentScene(Action onComplete = null)
        {
            if (_isLoading)
            {
                Debug.LogWarning("[SceneLoader] ExitContentScene 跳过：正在加载中");
                return;
            }
            StartCoroutine(ExitContentSceneCoroutine(onComplete));
        }

        private IEnumerator ExitContentSceneCoroutine(Action onComplete)
        {
            _isLoading = true;
            string target = _currentContentScene;
            if (!string.IsNullOrEmpty(target))
            {
                var scene = SceneManager.GetSceneByName(target);
                if (scene.IsValid() && scene.isLoaded)
                {
                    var op = SceneManager.UnloadSceneAsync(scene);
                    if (op != null) yield return op;
                    Debug.Log($"[SceneLoader] 已退出内容场景: {target}");
                }
                else
                {
                    Debug.Log($"[SceneLoader] {target} 已不在加载状态，跳过卸载");
                }
            }
            _currentContentScene = "";
            _isLoading = false;
            onComplete?.Invoke();
        }

        private IEnumerator SwitchContentSceneCoroutine(string newSceneName, Action onComplete)
        {
            _isLoading = true;

            EventManager.Instance?.TriggerEvent(GameEvents.SCENE_LOAD_STARTED, newSceneName);

            if (!string.IsNullOrEmpty(_currentContentScene))
            {
                // 防御陈旧 _currentContentScene：若该场景已被外部卸载（e.g. PauseController 早期版本直接走 SceneManager），
                // SceneManager.UnloadSceneAsync 会抛 ArgumentException → 整个协程死掉 → _isLoading 卡死。
                // 这里先 IsValid + isLoaded 双检，安全跳过。
                var staleScene = SceneManager.GetSceneByName(_currentContentScene);
                if (staleScene.IsValid() && staleScene.isLoaded)
                {
                    var unloadOp = SceneManager.UnloadSceneAsync(staleScene);
                    if (unloadOp != null) yield return unloadOp;
                    Debug.Log($"[SceneLoader] 已卸载场景: {_currentContentScene}");
                }
                else
                {
                    Debug.Log($"[SceneLoader] _currentContentScene={_currentContentScene} 已不在加载状态，跳过卸载");
                }
            }

            // ⚠️ 必须用 `yield return loadOp` 以规避 Tuanjie/Unity 2022 additive 加载偶发挂起（见类注释）
            var loadOp = SceneManager.LoadSceneAsync(newSceneName, LoadSceneMode.Additive);
            if (loadOp != null)
            {
                yield return loadOp;
            }

            _currentContentScene = newSceneName;
            _isLoading = false;

            EventManager.Instance?.TriggerEvent(GameEvents.SCENE_LOAD_COMPLETED, newSceneName);
            Debug.Log($"[SceneLoader] 已加载场景: {newSceneName}");
            onComplete?.Invoke();
        }

        private IEnumerator LoadSceneAdditiveCoroutine(string sceneName, Action onComplete)
        {
            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op != null)
            {
                yield return op;
            }
            onComplete?.Invoke();
        }

        private IEnumerator UnloadSceneCoroutine(string sceneName, Action onComplete)
        {
            var op = SceneManager.UnloadSceneAsync(sceneName);
            if (op != null)
            {
                yield return op;
            }
            onComplete?.Invoke();
        }
    }
}
