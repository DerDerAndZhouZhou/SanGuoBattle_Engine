using UnityEngine;
using UnityEngine.SceneManagement;
using HeroDefense.Config;
using HeroDefense.Core;
using HeroDefense.Engine.Host;
using HeroDefense.Utils;

namespace HeroDefense.Battle
{
    /// <summary>
    /// Battle 场景根 controller（三路兜底 + 防重入，CLAUDE.md §10 R-V12）。
    ///
    /// 场景 Start 在 Tuanjie 1.8.4 + MCP unfocused 状态偶发不触发 →
    /// 必须 Awake + OnEnable + Start + SceneManager.sceneLoaded 4 路兜底，
    /// 配 _isReady flag 防重入。
    ///
    /// 入口：调 Lua 全局 `Battle_OnSceneReady(level_id)`。level_id 从 GameManager 注入（或默认 1001 MVP）。
    ///
    /// 0 SerializeField — pendingLevelId 由 SceneLoader 切场景前通过静态属性注入。
    /// </summary>
    public class BattleSceneController : MonoBehaviour
    {
        /// <summary>外部进入对局前必须 set。如未 set 默认走 1001 MVP（黄巾起义-1）。</summary>
        public static int PendingLevelId = 1001;

        private bool _isReady;
        private bool _eventBound;

        private void Awake()
        {
            BindSceneLoaded();
            TryEnterReady("Awake");
        }

        private void OnEnable()
        {
            BindSceneLoaded();
            TryEnterReady("OnEnable");
        }

        private void Start()
        {
            TryEnterReady("Start");
        }

        private void OnDisable()
        {
            UnbindSceneLoaded();
            // 场景退出时清理（避免句柄泄漏）
            if (_isReady)
            {
                try { LuaHost.CallGlobal("Battle_OnSceneExit"); }
                catch (System.Exception e) { Debug.LogError($"[BSC] Lua Battle_OnSceneExit 异常: {e.Message}"); }
                try { BattleBridge.OnBattleSceneExit(); }
                catch (System.Exception e) { Debug.LogError($"[BSC] OnBattleSceneExit 异常: {e.Message}"); }
                try { ToggleMainMenuUI(true); }
                catch (System.Exception e) { Debug.LogError($"[BSC] ToggleMainMenuUI 异常: {e.Message}"); }
            }
            _isReady = false; // 允许下次 Additive 加载重新触发
        }

        /// <summary>切对局/退出对局时，隐 / 显 UIWindow 内的 MainWindow（即 5 Tab 主菜单），同时切 BattleHud。
        /// 用路径查找而非 Tag，因为 inactive 节点 FindWithTag 找不到。</summary>
        private static void ToggleMainMenuUI(bool show)
        {
            try
            {
                if (!Application.isPlaying) return;
#if UNITY_EDITOR
                if (!UnityEditor.EditorApplication.isPlaying) return;
#endif
                // 已迁热更 UI：旧 MainWindow / BattleHud 场景节点已于迁移收尾删除；主菜单/HUD/库存/商场全经 XML *_Open/Close 显隐
                HeroDefense.Engine.Host.LuaHost.CallGlobal(show ? "MainMenu_Open" : "MainMenu_Close");
                HeroDefense.Engine.Host.LuaHost.CallGlobal(show ? "BattleHud_Close" : "BattleHud_Open");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BSC] ToggleMainMenuUI({show}) 失败: {e.Message}");
            }
        }

        private void BindSceneLoaded()
        {
            if (_eventBound) return;
            SceneManager.sceneLoaded += OnSceneLoaded;
            _eventBound = true;
        }

        private void UnbindSceneLoaded()
        {
            if (!_eventBound) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _eventBound = false;
        }

        private void OnSceneLoaded(Scene s, LoadSceneMode m)
        {
            TryEnterReady("sceneLoaded:" + s.name);
        }

        private void TryEnterReady(string trigger)
        {
            if (_isReady) return;
            _isReady = true;

            try
            {
                Debug.Log($"[BSC] EnterReady via {trigger}, level_id={PendingLevelId}");

                // 1. 初始化 GridMap（先读 grid.txt 参数 → 再读场景预摆的 cell 节点）
                int gridId = ResolveGridIdForLevel(PendingLevelId);
                GridMap.InitFromConfig(gridId);
                if (!GridMap.InitFromScene3DLayout())
                {
                    if (!GridMap.InitFromScene2DLayout())
                        GridMap.InitFromScene();
                }

                // 1.5 R3a (2026-06-11)：给对局相机挂 Physics2DRaycaster —— 场上单位输入改走
                //     EventSystem（UnitView IPointer/IDrag），无此 raycaster 场上单位收不到任何指针事件。
                EnsureUnitPointerRaycaster();

                // 2. 加载场景背景 sprite（Tag=Sky_Bg / Battle_Bg；level.scene_bg_key / sky_bg_key）
                ApplyBackgroundSprites(PendingLevelId);
                if (!Battlefield3DLayoutBridge.ApplyVisuals())
                    Battlefield2DLayoutBridge.ApplyVisuals();

                // 3. 隐藏 MainMenu UI（持久 UIWindow 切到对局 HUD 模式）
                ToggleMainMenuUI(false);

                // 4. 调 Lua 全局 Battle_OnSceneReady(level_id)
                CallLuaOnSceneReady(PendingLevelId);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BSC] TryEnterReady 异常 via {trigger}: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>R3a：找对局相机（优先 BattleCamera，名字/挂常用 tag 都试）挂 Physics2DRaycaster。
        /// 找不到 BattleCamera 时兜底给所有启用相机挂（多挂无害——EventSystem 取最高优先命中）。</summary>
        private static void EnsureUnitPointerRaycaster()
        {
            try
            {
                var named = GameObject.Find("BattleCamera");
                Camera target = named != null ? named.GetComponent<Camera>() : null;
                if (target != null)
                {
                    AddRaycasterIfMissing(target);
                    return;
                }
                foreach (var cam in Camera.allCameras)
                {
                    if (cam != null && cam.isActiveAndEnabled) AddRaycasterIfMissing(cam);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BSC] EnsureUnitPointerRaycaster 失败: {e.Message}");
            }
        }

        private static void AddRaycasterIfMissing(Camera cam)
        {
            if (cam.GetComponent<UnityEngine.EventSystems.Physics2DRaycaster>() == null)
            {
                cam.gameObject.AddComponent<UnityEngine.EventSystems.Physics2DRaycaster>();
                Debug.Log($"[BSC] Physics2DRaycaster 注入 → {cam.name}");
            }
        }

        /// <summary>读 level.sky_bg_key / scene_bg_key → 分别加载到 Tag=Sky_Bg / Battle_Bg。</summary>
        private static void ApplyBackgroundSprites(int levelId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("level", "id", levelId);
                if (row == null) return;

                string battleBgKey = cm.GetValue<string>(row, "scene_bg_key", "chapter_1_yellow_turban");
                string skyBgKey = cm.GetValue<string>(row, "sky_bg_key", "sky_dusk");

                ApplyOneBgWithExtFallback("Battle_Bg", "resources/art/bg/" + battleBgKey, fitCamera: true);
                ApplyOneBgWithExtFallback("Sky_Bg", "resources/art/bg/" + skyBgKey, fitCamera: false);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BSC] ApplyBackgroundSprites 异常: {e.Message}");
            }
        }

        private static void ApplyOneBg(string tag, string spritePath, bool fitCamera = false)
        {
            var go = GameObject.FindWithTag(tag);
            if (go == null)
            {
                Debug.LogWarning($"[BSC] 未找到 Tag={tag} 的 GameObject");
                return;
            }
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) return;
            var sprite = ResourceHost.LoadSprite(spritePath);
            if (sprite != null)
            {
                sr.sprite = sprite;
                sr.color = Color.white;
                Debug.Log($"[BSC] {tag} ← {spritePath}");
                // 背景需铺满相机视口（cover 模式：sprite 尺寸 / 屏幕比例 任意都不留间隙）
                if (fitCamera) FitBackgroundToCamera(go, sprite);
            }
        }

        /// <summary>
        /// 背景图加载,支持 .png / .jpg 双扩展名回落(用户可能直接放 jpg)。
        /// pathNoExt 是不带扩展名的相对路径,如 "resources/art/bg/chapter_1_yellow_turban"。
        /// </summary>
        private static void ApplyOneBgWithExtFallback(string tag, string pathNoExt, bool fitCamera = false)
        {
            var go = GameObject.FindWithTag(tag);
            if (go == null)
            {
                Debug.LogWarning($"[BSC] 未找到 Tag={tag} 的 GameObject");
                return;
            }
            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) return;

            // .png 先试静默,缺失就 .jpg(都缺再 log)
            string usedPath = pathNoExt + ".png";
            var sprite = ResourceHost.LoadSprite(usedPath, logMissing: false);
            if (sprite == null)
            {
                usedPath = pathNoExt + ".jpg";
                sprite = ResourceHost.LoadSprite(usedPath, logMissing: false);
            }
            if (sprite == null)
            {
                Debug.LogWarning($"[BSC] 背景缺失 (.png/.jpg 都没有): {pathNoExt}");
                return;
            }
            sr.sprite = sprite;
            sr.color = Color.white;
            Debug.Log($"[BSC] {tag} ← {usedPath}");
            if (fitCamera) FitBackgroundToCamera(go, sprite);
        }

        /// <summary>
        /// 把背景 sprite 缩放 + 居中到相机视口，cover 模式（取较大缩放，溢出裁掉，永不留间隙）。
        /// sprite 像素尺寸 / Game view 比例 / 相机 ortho size 任意组合都铺满。
        /// </summary>
        private static void FitBackgroundToCamera(GameObject go, Sprite sprite)
        {
            var cam = Camera.main;
            if (cam == null || !cam.orthographic) return;
            var sprSize = sprite.bounds.size; // 世界单位，已含 pixelsPerUnit
            if (sprSize.x <= 0f || sprSize.y <= 0f) return;
            float camH = cam.orthographicSize * 2f;
            float camW = camH * cam.aspect;
            float scale = Mathf.Max(camW / sprSize.x, camH / sprSize.y);
            go.transform.localScale = new Vector3(scale, scale, 1f);
            var camPos = cam.transform.position;
            go.transform.position = new Vector3(camPos.x, camPos.y, go.transform.position.z);
            Debug.Log($"[BSC] BG fit camera: camW={camW:F2} camH={camH:F2} sprite={sprSize.x:F2}x{sprSize.y:F2} scale={scale:F3}");
        }

        /// <summary>从 level.txt 读 level_id → grid_id。失败回退 grid_id=1。</summary>
        private static int ResolveGridIdForLevel(int levelId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return 1;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("level", "id", levelId);
                if (row == null) return 1;
                return cm.GetValue<int>(row, "grid_id", 1);
            }
            catch
            {
                return 1;
            }
        }

        private static void CallLuaOnSceneReady(int levelId)
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null)
                {
                    Debug.LogWarning("[BSC] LuaHost.Env 为 null，跳过 Battle_OnSceneReady");
                    return;
                }
                var fn = env.Global.Get<XLua.LuaFunction>("Battle_OnSceneReady");
                if (fn != null)
                {
                    fn.Call(levelId);
                    fn.Dispose();
                }
                else
                {
                    Debug.LogWarning("[BSC] Lua 端未定义 Battle_OnSceneReady（业务 Lua 尚未就位则正常）");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[BSC] CallLua Battle_OnSceneReady 失败: {e.Message}");
            }
#endif
        }
    }
}
