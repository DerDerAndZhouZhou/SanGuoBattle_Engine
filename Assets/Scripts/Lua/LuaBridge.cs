using UnityEngine;
using HeroDefense.Core;
using HeroDefense.UI;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Lua
{
    /// <summary>
    /// C# ↔ Lua 桥接层 - 将 C# 核心服务暴露给 Lua。
    /// Lua 端用法: local bridge = CS.HeroDefense.Lua.LuaBridge
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class LuaBridge
    {
        public static GameManager GetGameManager() => GameManager.Instance;
        public static EventManager GetEventManager() => EventManager.Instance;
        public static SceneLoader GetSceneLoader() => SceneLoader.Instance;
        public static AudioManager GetAudioManager() => AudioManager.Instance;
        public static SaveManager GetSaveManager() => SaveManager.Instance;
        public static ObjectPoolManager GetObjectPoolManager() => ObjectPoolManager.Instance;
        public static UIManager GetUIManager() => UIManager.Instance;
        public static LuaManager GetLuaManager() => LuaManager.Instance;

        #region 便捷方法（减少 Lua 端调用链）

        /// <summary>切换游戏状态。Lua 用法: LuaBridge.ChangeGameState(1) -- MainMenu</summary>
        public static void ChangeGameState(int stateValue)
        {
            GameManager.Instance?.ChangeState((GameManager.GameState)stateValue);
        }

        public static void LoadContentScene(string sceneName)
        {
            SceneLoader.Instance?.LoadContentScene(sceneName);
        }

        public static void ShowPanel(string panelName) => UIManager.Instance?.ShowPanel(panelName);
        public static void HidePanel(string panelName) => UIManager.Instance?.HidePanel(panelName);
        public static void HideAllPanels() => UIManager.Instance?.HideAllPanels();

        public static void FireEvent(string eventName, params object[] args)
        {
            EventManager.Instance?.TriggerEvent(eventName, args);
        }

        public static void PlaySFX(string sfxName) => AudioManager.Instance?.PlaySFX(sfxName);

        public static void Log(string message) => Debug.Log($"[Lua] {message}");
        public static void LogWarning(string message) => Debug.LogWarning($"[Lua] {message}");
        public static void LogError(string message) => Debug.LogError($"[Lua] {message}");

        #endregion
    }
}
