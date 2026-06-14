using UnityEngine;
using HeroDefense.Lua;
using HeroDefense.SDK;

namespace HeroDefense.Core
{
    /// <summary>
    /// 启动初始化器 - 挂在 BootScene 的 [Boot] 物体上。
    /// 按顺序初始化子系统，然后加载 UIWindow 与 MainScene 内容场景。
    /// </summary>
    public class BootInitializer : MonoBehaviour
    {
        private void Start()
        {
            // Editor 失焦 / 真机切后台时仍跑 Update（Tuanjie 1.8.4 + MCP 未 focus 时 Timer 不推进）
            Application.runInBackground = true;
            Debug.Log("[BootInitializer] ======== 游戏启动 ========");
            StartCoroutine(BootAll());
        }

        // CDN 根 URL。默认 = cloudflared 隧道（真机/任何网络可达 + HTTPS，DevTools 也走它，互不冲突）。
        // ⚠ 2026-06-07：这是 cloudflared **quick tunnel** 临时地址，cloudflared 一重启地址就变 →
        //    变了就改这一行重新出包。要长期稳定地址用 named tunnel（需 CF 账号+域名）或静态托管。
        // 编辑器/PC 本地有 Game/ → Exists("config/Enum.tab")=true 跳过下载，不受此默认值影响。
        // PlayerPrefs "cdn_base_url" 可覆盖（但小游戏沙箱 IndexedDB 不可用 → PlayerPrefs 不持久，真机改地址只能改这里）。
        private static string GetCdnBaseUrl()
        {
            return PlayerPrefs.GetString("cdn_base_url", "https://das-adoption-findarticles-string.trycloudflare.com");
        }

        /// <summary>
        /// 启动总流程（协程）。关键顺序铁律：
        ///   Step 0 资源底层 + CDN 下载 **必须最先**。WebGL/小游戏首启时沙箱里没有 Game/，
        ///   若先创建各 manager（AudioManager 等初始化会读配置）→ 会以"空 manifest"触发
        ///   ConfigManager.Load() → 把配置表加载成 0 张并锁定 _loaded=true → 之后下载完也回不来
        ///   （表现：Lua 后于下载初始化能跑，但所有配置表全空 / 商店刷不出 / 无背景）。
        ///   所以：先把资源拉到位、manifest 加载好，再建 manager / 读配置 / 起 Lua。
        ///   编辑器/PC 本地已有 Game/ → Exists=true → 不进下载分支、不 yield → 与改动前完全同步执行。
        /// </summary>
        private System.Collections.IEnumerator BootAll()
        {
            // ===== Step 0: 资源底层 + CDN 下载（最先，后续一切都依赖资源就位）=====
            HeroDefense.Engine.Host.ResourceHost.Boot();
            if (!HeroDefense.Engine.Host.ResourceHost.Exists("config/Enum.tab"))
            {
                string cdn = GetCdnBaseUrl();
                Debug.Log("[BootInitializer] Step 0: 本地无资源 → CDN 下载: " + cdn);
                bool ok = false; string msg = "";
                yield return HeroDefense.Engine.Host.HotUpdateHost.CheckAndDownload(cdn, (s, m) => { ok = s; msg = m; });
                if (ok)
                {
                    HeroDefense.Engine.Host.ResourceHost.TryLoadManifest();   // 下载后重载 manifest（EnumerateFiles 依赖）
                    HeroDefense.Engine.Host.ResourceHost.LoadAtlasIndex();    // 重扫 atlas（首次 Boot 时沙箱还没资源）
                    Debug.Log("[BootInitializer] Step 0: CDN 下载完成 v" + msg);
                }
                else
                {
                    Debug.LogError("[BootInitializer] Step 0: CDN 下载失败: " + msg + "（查本地 HTTP 服务器 + DevTools 勾'不校验合法域名'）");
                }
            }

            // ===== Step 1-6: 子系统（资源已就位，manager 初始化读配置不会再读到空）=====
            // 1. GameManager 已通过 [RuntimeInitializeOnLoadMethod] 自动创建
            Debug.Log("[BootInitializer] Step 1: GameManager 已就绪");
            var em = EventManager.Instance;
            Debug.Log("[BootInitializer] Step 2: EventManager 已就绪");
            var sm = SaveManager.Instance;
            Debug.Log("[BootInitializer] Step 3: SaveManager 已就绪");
            var am = AudioManager.Instance;
            Debug.Log("[BootInitializer] Step 4: AudioManager 已就绪");
            var opm = ObjectPoolManager.Instance;
            Debug.Log("[BootInitializer] Step 5: ObjectPoolManager 已就绪");
            var sdk = DouyinSDKManager.Instance;
            var adMgr = AdManager.Instance;
            Debug.Log("[BootInitializer] Step 6: SDK 已就绪");

            // ===== Step 6.5: 配置（manifest 已就位 → EnumerateFiles 能枚举到全部 .tab）=====
            HeroDefense.Config.ConfigManager.Instance.LoadIfNeeded();
            Debug.Log("[BootInitializer] Step 6.5: ResourceHost + ConfigManager 就绪");

            // ===== Step 7: Lua =====
            var luaMgr = LuaManager.Instance;
            if (luaMgr != null)
            {
                luaMgr.CheckHotUpdate((needUpdate) =>
                {
                    luaMgr.Initialize();
                    Debug.Log("[BootInitializer] Step 7: LuaManager 已就绪");
                    LoadUIWindowThenMainScene();
                });
            }
            else
            {
                Debug.LogWarning("[BootInitializer] LuaManager 未找到，跳过Lua初始化");
                LoadUIWindowThenMainScene();
            }
        }

        private void LoadUIWindowThenMainScene()
        {
            var sceneLoader = SceneLoader.Instance;
            if (sceneLoader == null)
            {
                Debug.LogError("[BootInitializer] SceneLoader 未找到！");
                return;
            }

            sceneLoader.InitializeUIWindow(() =>
            {
                Debug.Log("[BootInitializer] Step 8: UIWindow 加载完成");

                // UIWindow 加载后立即显示 Loading 面板（减少黑屏）
                var uiMgr = UI.UIManager.Instance;
                if (uiMgr != null && uiMgr.HasPanel("Panel_Loading"))
                    uiMgr.ShowPanel("Panel_Loading");

                sceneLoader.LoadContentScene("MainScene", () =>
                {
                    Debug.Log("[BootInitializer] Step 9: MainScene 加载完成");
                    GameManager.Instance?.ChangeState(GameManager.GameState.MainMenu);
                    Debug.Log("[BootInitializer] ======== 启动完成 ========");
                });
            });
        }
    }
}
