using UnityEngine;
using UnityEngine.SceneManagement;
using HeroDefense.UI;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.Core
{
    /// <summary>
    /// MainScene 场景控制器（M1.7）。
    /// 职责：场景加载完成时 ① 调用 UIManager.ShowMainWindow 显示主菜单 UI；
    ///       ② 按 GameConfig.main_menu_bg_key 运行时构造背景 SpriteRenderer 并 cover 铺满相机视口。
    /// MainScene 场景本身只承载相机，所有 UI 控件挂 UIWindow.MainWindow，背景由本控制器动态加。
    /// </summary>
    public class MainSceneController : MonoBehaviour
    {
        void Start()
        {
            // T217 (2026-05-21):无条件恢复主菜单 UI + 清孤儿 camera + 确保 MainScene 有 camera + 背景。
            // 从 GameScene 切回 MainScene 时三个问题串联:
            //   ①BSC.OnDisable 的 ToggleMainMenuUI(true) 可能没成功 → MainWindow 仍 inactive
            //   ②GameScene 卸载但 BattleCamera 残留(scene 已 unload 但 GameObject 没销毁)→ Camera.main 拿到残留 → bg 配错相机
            //   ③MainScene 第二次加载可能没有自己的 camera → 屏幕由 BootCamera 渲染但位置错 → bg 看不见
            EnsureMainMenuVisible();
            CleanupOrphanCameras();
            EnsureMainSceneCamera();
            ApplyMainMenuBackground();

            if (UIManager.Instance == null)
            {
                Debug.LogWarning("[MainSceneController] UIManager.Instance is null on Start; MainScene loaded out of normal flow?");
                return;
            }
            UIManager.Instance.ShowMainWindow();
        }

        static void EnsureMainMenuVisible()
        {
            try
            {
                var root = GameObject.Find("RootWindow");
                if (root == null) return;
                // 已迁热更 UI：抑制旧 MainWindow（保持 inactive）；新 XML 主菜单由 UIManager.ShowMainWindow→MainMenu_Open 显示
                var mw = root.transform.Find("MainWindow");
                if (mw != null && mw.gameObject.activeSelf) mw.gameObject.SetActive(false);
                var hud = root.transform.Find("BattleHud");
                if (hud != null && hud.gameObject.activeSelf) hud.gameObject.SetActive(false);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MainSceneController] EnsureMainMenuVisible 异常: {e.Message}");
            }
        }

        // T217:GameScene 卸载后残留的 camera(scene 已 unload 但 GameObject 没被销毁)主动清掉
        static void CleanupOrphanCameras()
        {
            try
            {
                foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                {
                    var sceneName = cam.gameObject.scene.name;
                    bool isOrphan = !cam.gameObject.scene.isLoaded
                                 || sceneName == "GameScene"
                                 || string.IsNullOrEmpty(sceneName);
                    if (isOrphan)
                    {
                        Debug.Log($"[MainSceneController] 清孤儿 camera: {cam.name} (scene={sceneName})");
                        Destroy(cam.gameObject);
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MainSceneController] CleanupOrphanCameras 异常: {e.Message}");
            }
        }

        // T217:确保 MainScene 内有一个 active+enabled+tag=MainCamera 的相机,bg 才能正确渲染
        void EnsureMainSceneCamera()
        {
            try
            {
                var mainScene = gameObject.scene;
                if (!mainScene.isLoaded) return;
                // 找 MainScene 内的 camera
                Camera found = null;
                foreach (var root in mainScene.GetRootGameObjects())
                {
                    var cam = root.GetComponent<Camera>() ?? root.GetComponentInChildren<Camera>(true);
                    if (cam != null) { found = cam; break; }
                }
                if (found != null)
                {
                    if (!found.gameObject.activeSelf) found.gameObject.SetActive(true);
                    found.enabled = true;
                    if (!found.CompareTag("MainCamera")) found.tag = "MainCamera";
                    return;
                }
                // 兜底:MainScene 没 camera → 程序化造一个
                var go = new GameObject("MainSceneCamera_Auto");
                SceneManager.MoveGameObjectToScene(go, mainScene);
                go.transform.position = new Vector3(0f, 0f, -10f);
                go.tag = "MainCamera";
                var c = go.AddComponent<Camera>();
                c.orthographic = true;
                c.orthographicSize = 6.4f;
                c.clearFlags = CameraClearFlags.SolidColor;
                c.backgroundColor = new Color(0.07f, 0.07f, 0.1f, 1f);
                c.depth = 0;
                Debug.Log("[MainSceneController] MainScene 无 camera → 程序化创建 MainSceneCamera_Auto");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MainSceneController] EnsureMainSceneCamera 异常: {e.Message}");
            }
        }

        // 按 GameConfig.main_menu_bg_key 运行时构造背景（支持 .png / .jpg 双扩展名回落）
        // 已存在同名 GameObject 时仅刷新 sprite，避免重复创建
        void ApplyMainMenuBackground()
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", "main_menu_bg_key");
                if (row == null) return;
                string key = cm.GetValue<string>(row, "value", "");
                if (string.IsNullOrEmpty(key)) return;

                // .png 优先，缺失回落 .jpg
                var sprite = ResourceHost.LoadSprite($"resources/art/bg/{key}.png", logMissing: false)
                          ?? ResourceHost.LoadSprite($"resources/art/bg/{key}.jpg", logMissing: false);
                if (sprite == null)
                {
                    Debug.LogWarning($"[MainSceneController] 主菜单背景缺失 (.png/.jpg 都没有): resources/art/bg/{key}");
                    return;
                }

                // 用户 2026-05-21 报：进对局后主菜单 BG 没隐藏 + 尺寸错位 + 返回仍错位
                // 根因：之前 GO 落在 active scene（可能是 UIWindow 持久场景），MainScene 卸载时 GO 不死 → 带入对局
                // 修：每次 Start 先清掉残留 + 创建新 GO + MoveGameObjectToScene 显式归到 MainScene（=本 controller 所在场景）
                //     这样 MainScene 卸载时 BG 一起死，对局/重进时不会残留旧 GO 错位
                const string BG_NAME = "MainMenuBackground";
                var existing = GameObject.Find(BG_NAME);
                if (existing != null)
                {
                    Destroy(existing);   // 清掉跨场景残留（理论上修了 MoveGameObjectToScene 后不会再发生，留作保险）
                }
                var bg = new GameObject(BG_NAME);
                var sr = bg.AddComponent<SpriteRenderer>();
                sr.sortingOrder = -100;   // 背景层最底
                sr.sprite = sprite;
                sr.color = Color.white;

                // 关键：归到 MainSceneController 所在的 scene（=MainScene），随 MainScene 卸载一并销毁
                SceneManager.MoveGameObjectToScene(bg, gameObject.scene);

                FitBackgroundToCamera(bg, sprite);
                Debug.Log($"[MainSceneController] 主菜单背景 ← resources/art/bg/{key} ({sprite.texture.width}x{sprite.texture.height}) scene={gameObject.scene.name}");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[MainSceneController] ApplyMainMenuBackground 异常: {e.Message}");
            }
        }

        // Cover 模式：取较大缩放 → sprite 永远铺满相机视口（任意比例都不留间隙）
        static void FitBackgroundToCamera(GameObject go, Sprite sprite)
        {
            var cam = Camera.main;
            if (cam == null || !cam.orthographic) return;
            var sprSize = sprite.bounds.size;
            if (sprSize.x <= 0f || sprSize.y <= 0f) return;
            float camH = cam.orthographicSize * 2f;
            float camW = camH * cam.aspect;
            float scale = Mathf.Max(camW / sprSize.x, camH / sprSize.y);
            go.transform.localScale = new Vector3(scale, scale, 1f);
            var camPos = cam.transform.position;
            // Z 放相机前面少量距离，确保不被相机 near plane 裁掉，但仍在 UI 之后（UI 是 Screen Space Camera）
            go.transform.position = new Vector3(camPos.x, camPos.y, camPos.z + 10f);
        }
    }
}
