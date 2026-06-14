using System;
using UnityEngine;

namespace HeroDefense.Core
{
    /// <summary>
    /// 游戏总管理器 - 精简为状态存储 + 对外 API。
    /// 业务逻辑（状态进入/退出处理）委托给 Lua 层 game_manager.lua。
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        #region 单例
        private static GameManager _instance;
        public static GameManager Instance => _instance;

        /// <summary>任意场景直接运行时自动创建 GameManager。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void AutoCreateInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("[GameManager]");
            go.AddComponent<GameManager>();
        }
        #endregion

        #region 游戏状态枚举
        public enum GameState
        {
            Boot,          // 启动初始化
            MainMenu,      // 主菜单
            Loading,       // 加载中
        }
        #endregion

        #region 运行时状态
        private GameState _currentState = GameState.Boot;
        private bool _isInitialized = false;
        #endregion

        #region 属性
        public GameState CurrentState => _currentState;
        #endregion

        #region Lua 回调委托
        public Action<GameState, GameState> OnStateChangedCallback;
        #endregion

        #region 生命周期
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
            InitializeGame();
        }

        private void Start()
        {
            Debug.Log("[GameManager] 游戏启动完成");
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
        #endregion

        #region 初始化
        private void InitializeGame()
        {
            if (_isInitialized) return;

            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            _isInitialized = true;
            Debug.Log("[GameManager] 核心系统初始化完成");
        }
        #endregion

        #region 状态切换
        public void ChangeState(GameState newState)
        {
            if (_currentState == newState) return;

            GameState oldState = _currentState;
            _currentState = newState;

            EventManager.Instance?.TriggerEvent(GameEvents.GAME_STATE_CHANGED, oldState, newState);
            OnStateChangedCallback?.Invoke(oldState, newState);

            Debug.Log($"[GameManager] 状态切换: {oldState} -> {newState}");
        }
        #endregion

        public void SetTimeScale(float scale)
        {
            Time.timeScale = scale;
        }
    }

    /// <summary>
    /// 游戏事件名称常量。所有命名事件经 EventManager 中转；Lua 侧用相同字符串监听。
    /// 对局事件已清空（2026-05-06 重新设计中）；新对局架构落地后在此追加。
    /// </summary>
    public static class GameEvents
    {
        // 全局状态
        public const string GAME_STATE_CHANGED = "GameStateChanged";

        // 经济（持久化货币 — SaveManager 用）
        public const string COINS_EARNED = "CoinsEarned";
        public const string COINS_SPENT = "CoinsSpent";
        public const string GEMS_EARNED = "GemsEarned";

        // UI
        public const string UI_PANEL_OPENED = "UIPanelOpened";
        public const string UI_PANEL_CLOSED = "UIPanelClosed";

        // 场景
        public const string SCENE_LOAD_STARTED = "SceneLoadStarted";
        public const string SCENE_LOAD_COMPLETED = "SceneLoadCompleted";
    }
}
