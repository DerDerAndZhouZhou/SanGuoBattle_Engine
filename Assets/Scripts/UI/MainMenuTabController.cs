using UnityEngine;
using HeroDefense.Config;

namespace HeroDefense.UI
{
    /// <summary>
    /// MainScene 5 Tab 切换主控。挂在 MainWindow 上。
    /// 切 Tab 仅 SetActive，不重建子节点。
    ///
    /// 2026-05-06 重构：
    ///   - 5 个 tabPanels 通过 Tag (Tab_Shop / Tab_Hero / Tab_Battle / Tab_Castle / Tab_Dungeon) 运行时查找
    ///   - tabNames / defaultTab 从 GameConfig.txt 读取（key=tab_names / tab_default）
    /// </summary>
    public class MainMenuTabController : MonoBehaviour
    {
        // tabNames 顺序与 tag 顺序一一对应（从 GameConfig 读，默认 shop/hero/battle/castle/dungeon）
        string[] _tabNames;
        string _defaultTab;
        // 5 个 Tab 容器（Tag 查找）
        GameObject[] _tabPanels;

        string _currentTab;
        public string CurrentTab => _currentTab;

        void Start()
        {
            ConfigManager.Instance?.LoadIfNeeded();
            LoadConfig();
            ResolveTabPanels();
            ShowTab(_defaultTab);
            RepositionTabBar();
        }

        // 把 TabBar 从「主菜单底部」移到「关卡选择窗口上方」
        // 用户反馈 2026-05-21：5 个 tab 落到选择关卡上方，做出 5 个按钮
        // LSW 在 BattleTab 内 anchored y=560、size=380、pivot 0.5,0.5 → 顶边 y=750 距 MainWindow 底部
        // TabBar bottom-center anchor + pivot bottom，anchoredY 950 → 与 LSW 顶留 200px 缓冲（用户 2026-05-21 反馈太矮）
        void RepositionTabBar()
        {
            var tabBar = transform.Find("TabBar") as RectTransform;
            if (tabBar == null)
            {
                Debug.LogWarning("[MainMenuTabController] TabBar 子节点未找到，跳过 RepositionTabBar");
                return;
            }
            tabBar.anchorMin = new Vector2(0.5f, 0f);
            tabBar.anchorMax = new Vector2(0.5f, 0f);
            tabBar.pivot = new Vector2(0.5f, 0f);
            // 5 按钮总宽 5×144=720。容器 720×128
            tabBar.sizeDelta = new Vector2(720f, 128f);
            tabBar.anchoredPosition = new Vector2(0f, 950f);
            Debug.Log($"[MainMenuTabController] TabBar 已重定位到 LSW 上方 (anchoredY=950)");
        }

        void LoadConfig()
        {
            var cm = ConfigManager.Instance;
            string namesCsv = "shop,hero,battle,castle,dungeon";
            _defaultTab = "battle";
            if (cm != null)
            {
                var row = cm.GetTableInfo("GameConfig", "key", "tab_names");
                if (row != null) namesCsv = cm.GetValue<string>(row, "value", namesCsv);

                var rowDefault = cm.GetTableInfo("GameConfig", "key", "tab_default");
                if (rowDefault != null) _defaultTab = cm.GetValue<string>(rowDefault, "value", _defaultTab);
            }
            _tabNames = namesCsv.Split(',');
            for (int i = 0; i < _tabNames.Length; i++) _tabNames[i] = _tabNames[i].Trim();
        }

        void ResolveTabPanels()
        {
            _tabPanels = new GameObject[_tabNames.Length];
            for (int i = 0; i < _tabNames.Length; i++)
            {
                // tag 命名规约：Tab_<PascalCaseName>
                string tag = "Tab_" + Capitalize(_tabNames[i]);
                _tabPanels[i] = UIFinder.FindPanelByTag(tag);
            }
        }

        public void ShowTab(string tabName)
        {
            int idx = System.Array.IndexOf(_tabNames, tabName);
            if (idx < 0)
            {
                Debug.LogWarning($"[MainMenuTabController] 未知 tabName={tabName}，回落到 {_defaultTab}");
                idx = System.Array.IndexOf(_tabNames, _defaultTab);
                if (idx < 0) idx = 0;
            }
            for (int i = 0; i < _tabPanels.Length; i++)
            {
                if (_tabPanels[i] != null) _tabPanels[i].SetActive(i == idx);
            }
            _currentTab = _tabNames[idx];
            Debug.Log($"[MainMenuTabController] ShowTab → {_currentTab}");
        }

        static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        // 5 个 Tab 按钮各自连这 5 个方法（场景搭建时按此方法名连 Button.onClick）
        public void OnTabBattle()   => ShowTab("battle");
        public void OnTabHero()     => ShowTab("hero");
        public void OnTabShop()     => ShowTab("shop");
        public void OnTabCastle()   => ShowTab("castle");
        public void OnTabDungeon()  => ShowTab("dungeon");
    }
}
