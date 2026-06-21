using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Core;
using HeroDefense.Utils;

namespace HeroDefense.UI
{
    /// <summary>
    /// UI 管理器 - 持久化 Canvas 管理。
    /// 通过字典管理所有面板，Lua 层通过面板名称字符串控制显隐。
    /// 跟随 UIScene 加载，整个游戏生命周期不销毁。
    /// </summary>
    public class UIManager : SingletonMono<UIManager>
    {
        const string TAG_ROOT_WINDOW = "Panel_RootWindow";
        // 旧场景 MainWindow 节点已于迁移收尾删除（主菜单为热更 XML wnd_main_menu）→ 不再按 Tag 查找

        Transform _panelRoot;
        GameObject rootWindow;

        private Dictionary<string, GameObject> _panels = new Dictionary<string, GameObject>();
        private Dictionary<string, UIBinder> _binders = new Dictionary<string, UIBinder>();
        private Stack<string> _panelStack = new Stack<string>();

        private string _currentPanelName = "";
        public string CurrentPanelName => _currentPanelName;

        protected override void OnSingletonInit()
        {
            ResolveWindows();

            if (_panelRoot == null)
                _panelRoot = transform;

            ScanPanels();
        }

        void ResolveWindows()
        {
            rootWindow = UIFinder.FindPanelByTag(TAG_ROOT_WINDOW);
        }

        private void ScanPanels()
        {
            _panels.Clear();
            for (int i = 0; i < _panelRoot.childCount; i++)
            {
                Transform child = _panelRoot.GetChild(i);
                string panelName = child.name;
                _panels[panelName] = child.gameObject;

                var binder = child.GetComponent<UIBinder>();
                if (binder == null)
                {
                    binder = child.gameObject.AddComponent<UIBinder>();
                }
                _binders[panelName] = binder;

                child.gameObject.SetActive(false);
            }

            Debug.Log($"[UIManager] 扫描到 {_panels.Count} 个 UI 面板");
        }

        public void ShowPanel(string panelName)
        {
            if (!_panels.TryGetValue(panelName, out var panel))
            {
                Debug.LogWarning($"[UIManager] 找不到面板: {panelName}");
                return;
            }

            if (!string.IsNullOrEmpty(_currentPanelName) && _currentPanelName != panelName)
            {
                _panelStack.Push(_currentPanelName);
            }

            panel.SetActive(true);

            if (_binders.TryGetValue(panelName, out var binder))
            {
                binder.Bind();
            }

            _currentPanelName = panelName;

            EventManager.Instance?.TriggerEvent(GameEvents.UI_PANEL_OPENED, panelName);
        }

        public void HidePanel(string panelName)
        {
            if (_panels.TryGetValue(panelName, out var panel))
            {
                panel.SetActive(false);
                EventManager.Instance?.TriggerEvent(GameEvents.UI_PANEL_CLOSED, panelName);
            }

            if (_currentPanelName == panelName)
            {
                _currentPanelName = "";
            }
        }

        public void HideAllPanels()
        {
            foreach (var kvp in _panels)
            {
                kvp.Value.SetActive(false);
            }
            _panelStack.Clear();
            _currentPanelName = "";
        }

        public void GoBack()
        {
            if (!string.IsNullOrEmpty(_currentPanelName))
            {
                HidePanel(_currentPanelName);
            }

            if (_panelStack.Count > 0)
            {
                string prevPanel = _panelStack.Pop();
                ShowPanel(prevPanel);
            }
        }

        public UIBinder GetBinder(string panelName)
        {
            _binders.TryGetValue(panelName, out var binder);
            return binder;
        }

        public GameObject GetPanel(string panelName)
        {
            _panels.TryGetValue(panelName, out var panel);
            return panel;
        }

        public bool HasPanel(string panelName)
        {
            return _panels.ContainsKey(panelName);
        }

        public bool IsPanelVisible(string panelName)
        {
            if (_panels.TryGetValue(panelName, out var panel))
            {
                return panel.activeSelf;
            }
            return false;
        }

        public void ShowMainWindow()
        {
            EnsureWindowsResolved();
            if (rootWindow != null && !rootWindow.activeSelf) rootWindow.SetActive(true);
            // 主菜单为热更 XML（Game/ui/main_menu/wnd_main_menu）；旧场景 MainWindow 节点已删除
            HeroDefense.Engine.Host.LuaHost.CallGlobal("MainMenu_Open");
        }

        public void HideAll()
        {
            EnsureWindowsResolved();
            HeroDefense.Engine.Host.LuaHost.CallGlobal("MainMenu_Close");   // 同步隐新 XML 主菜单
        }

        void EnsureWindowsResolved()
        {
            if (rootWindow == null) rootWindow = UIFinder.FindPanelByTag(TAG_ROOT_WINDOW);
        }
    }
}
