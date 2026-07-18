using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI.Xml
{
    /// <summary>
    /// 热更 UI 系统的「游戏端适配器」（阶段0）。
    ///
    /// 职责：
    ///   1. 实现 IUIXmlHost：贴图走 ResourceHost、字体走内置、OnLoad/onClick 钩子转 LuaHost.CallGlobal。
    ///   2. 持有唯一 UIXmlBuilder + 面板注册表（name → 面板 GameObject / xml 路径 / 父节点），支持热重载。
    ///   3. 暴露一组静态方法给 Lua（在 LuaHost.Boot 里 _env.Global.Set 注册为全局函数）。
    ///
    /// 注意：本类是「游戏专属胶水」，不进共享包；UIXmlBuilder 才是要抽出的共享核心（见 §4 时序）。
    /// 全静态门面（与 LuaHost 同风格），对局只有一套 UI 系统。
    /// </summary>
    public static class HDUIXmlHost
    {
        // ---- IUIXmlHost 游戏实现 ----
        class GameHost : IUIXmlHost
        {
            public Sprite LoadSprite(string spriteKey)
            {
                // logMissing=false：缺图静默 → builder 用白兜底；.png/.jpg 双扩展名回落
                return LoadSpriteFlexible(spriteKey);
            }

            public Sprite LoadSprite(string spriteKey, Vector4 border)
            {
                return LoadSpriteFlexible(spriteKey, border);
            }

            public Font GetFont()
            {
                return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            }

            public void OnPanelLoaded(string fn, GameObject panel)
            {
                LuaHost.CallGlobal(fn, panel);
            }

            public void OnControlClicked(string fn, GameObject sender)
            {
                LuaHost.CallGlobal(fn, sender);
            }

            public void OnControlValueChanged(string fn, GameObject sender, string value)
            {
                LuaHost.CallGlobal(fn, sender, value);
            }
        }

        // sprite key 规整：补 resources/art/ 前缀（不强加扩展名 → 交给 LoadSpriteFlexible 做 .png/.jpg 回落）。
        static string NormalizeSpriteBase(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            key = key.Replace('\\', '/').TrimStart('/');
            if (!key.StartsWith("resources/art/", System.StringComparison.OrdinalIgnoreCase)) key = "resources/art/" + key;
            return key;
        }

        // 加载 sprite，支持 .png/.jpg 双扩展名回落（关卡缩略图/背景常是 .jpg；同 BattleSceneController.ApplyOneBgWithExtFallback）。
        // key 已带扩展名 → 直接加载；无扩展名 → .png 先试，缺则 .jpg。缺图静默(logMissing=false) → 调用方白兜底。
        static Sprite LoadSpriteFlexible(string spriteKey)
        {
            string key = NormalizeSpriteBase(spriteKey);
            if (string.IsNullOrEmpty(key)) return null;
            if (key.Contains(".")) return ResourceHost.LoadSprite(key, false);
            var sp = ResourceHost.LoadSprite(key + ".png", false);
            if (sp == null) sp = ResourceHost.LoadSprite(key + ".jpg", false);
            return sp;
        }

        static Sprite LoadSpriteFlexible(string spriteKey, Vector4 border)
        {
            string key = NormalizeSpriteBase(spriteKey);
            if (string.IsNullOrEmpty(key)) return null;
            if (key.Contains(".")) return ResourceHost.LoadSprite(key, false, border);
            var sp = ResourceHost.LoadSprite(key + ".png", false, border);
            if (sp == null) sp = ResourceHost.LoadSprite(key + ".jpg", false, border);
            return sp;
        }

        class PanelRec
        {
            public GameObject go;
            public string xmlRelPath;
            public RectTransform parent;
        }

        static UIXmlBuilder _builder;
        static readonly Dictionary<string, PanelRec> _panels = new Dictionary<string, PanelRec>();

        static UIXmlBuilder Builder
        {
            get
            {
                if (_builder == null) _builder = new UIXmlBuilder(new GameHost());
                return _builder;
            }
        }

        // 启动期资源：模板文件 Game/ui/templates/*.xml + 主题色 Game/ui/theme.xml → 注册进 builder。幂等、只扫一次。
        static bool _templatesLoaded;
        static void EnsureTemplatesLoaded()
        {
            if (_templatesLoaded) return;
            _templatesLoaded = true;

            // ① 主题色表（共享资源现归入 ui/base/·按模块文件夹重组 2026-06-17）
            string theme = ResourceHost.ReadText("ui/base/theme.xml");
            if (!string.IsNullOrEmpty(theme)) Builder.RegisterColorsFromXml(theme);

            // ② 控件模板（ui/base/ 下所有 *.xml·含 battle_rows/common；theme.xml 无 <Templates> 根→RegisterTemplatesFromXml 安全空跑）
            var files = ResourceHost.EnumerateFiles("ui/base", "*.xml", System.IO.SearchOption.AllDirectories);
            if (files != null && files.Count > 0)
            {
                files.Sort(System.StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string xml = ResourceHost.ReadText(f);
                    if (!string.IsNullOrEmpty(xml)) Builder.RegisterTemplatesFromXml(xml);
                }
            }
            Debug.Log($"[HDUIXmlHost] 启动资源加载完毕：{Builder.TemplateCount} 模板 / {Builder.ColorCount} 主题色");
        }

        /// <summary>开发期：强制重扫模板 + 主题（热更联调用）。</summary>
        public static void ReloadTemplates()
        {
            _templatesLoaded = false;
            EnsureTemplatesLoaded();
        }

        // ====================================================================
        // 暴露给 Lua 的静态方法（LuaHost.Boot 注册为全局 UI_*）
        // ====================================================================

        /// <summary>读 Game/ui 下的 XML（相对路径，如 "ui/wnd_demo.xml"），构建到 UI 根节点下，返回面板 GameObject。</summary>
        public static GameObject LoadPanel(string xmlRelPath)
        {
            if (string.IsNullOrEmpty(xmlRelPath)) return null;
            EnsureTemplatesLoaded();
            string xml = ResourceHost.ReadText(xmlRelPath);
            if (string.IsNullOrEmpty(xml))
            {
                Debug.LogError($"[HDUIXmlHost] LoadPanel 读不到 XML: {xmlRelPath}");
                return null;
            }

            RectTransform parent = ResolveUIParent();
            if (parent == null)
            {
                Debug.LogError("[HDUIXmlHost] LoadPanel 找不到 UI 父节点（Canvas）");
                return null;
            }

            GameObject panel = Builder.Build(xml, parent);
            if (panel == null) return null;

            string name = panel.name;
            // 同名旧面板先销毁（防重复叠加）
            if (_panels.TryGetValue(name, out var old) && old.go != null && old.go != panel)
                Destroy(old.go);

            _panels[name] = new PanelRec { go = panel, xmlRelPath = xmlRelPath, parent = parent };
            Debug.Log($"[HDUIXmlHost] LoadPanel 完成: {name}（来自 {xmlRelPath}）");
            return panel;
        }

        /// <summary>销毁并按原 XML 重建同名面板（XML/热更联调用）。返回新面板 GameObject。</summary>
        public static GameObject ReloadPanel(string name)
        {
            if (string.IsNullOrEmpty(name) || !_panels.TryGetValue(name, out var rec))
            {
                Debug.LogWarning($"[HDUIXmlHost] ReloadPanel 未找到已加载面板: {name}");
                return null;
            }
            string xmlRelPath = rec.xmlRelPath;
            DestroyPanel(name);
            return LoadPanel(xmlRelPath);
        }

        /// <summary>销毁指定面板。</summary>
        public static void DestroyPanel(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (_panels.TryGetValue(name, out var rec))
            {
                if (rec.go != null) Destroy(rec.go);
                _panels.Remove(name);
                Debug.Log($"[HDUIXmlHost] DestroyPanel: {name}");
            }
        }

        /// <summary>在面板（或任意子树根）下按 name 递归查子控件 GameObject。</summary>
        public static GameObject Find(GameObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name)) return null;
            var t = UIFinder.FindChildByName(root.transform, name);
            return t != null ? t.gameObject : null;
        }

        /// <summary>设文本（Text 或 InputField 都支持）。</summary>
        public static void SetText(GameObject go, string text)
        {
            if (go == null) return;
            string v = (text ?? "").Replace("\\n", "\n");
            var t = go.GetComponent<Text>();
            if (t != null) { t.text = v; return; }
            var inp = go.GetComponent<InputField>();
            if (inp != null) { inp.text = v; return; }
            Debug.LogWarning($"[HDUIXmlHost] SetText: {go.name} 上无 Text/InputField");
        }

        /// <summary>读文本（Text 或 InputField）。</summary>
        public static string GetText(GameObject go)
        {
            if (go == null) return "";
            var t = go.GetComponent<Text>();
            if (t != null) return t.text;
            var inp = go.GetComponent<InputField>();
            if (inp != null) return inp.text;
            return "";
        }

        /// <summary>设 Image 贴图（spriteKey 同 XML sprite 规则：补 resources/art/ 前缀 .png 后缀）。</summary>
        public static void SetImage(GameObject go, string spriteKey)
        {
            if (go == null) return;
            var img = go.GetComponent<Image>();
            if (img == null) { Debug.LogWarning($"[HDUIXmlHost] SetImage: {go.name} 上无 Image"); return; }
            var s = LoadSpriteFlexible(spriteKey);
            img.sprite = s != null ? s : UIXmlBuilder.WhiteSprite();
        }

        /// <summary>设颜色（命名色 / #hex / CSS 名；作用于 Image 或 Text）。</summary>
        public static void SetColor(GameObject go, string color)
        {
            if (go == null) return;
            Color c;
            if (!Builder.TryResolveColor(color, out c)) { Debug.LogWarning($"[HDUIXmlHost] SetColor 解析失败: {color}"); return; }
            var img = go.GetComponent<Image>();
            if (img != null) { img.color = c; return; }
            var t = go.GetComponent<Text>();
            if (t != null) t.color = c;
        }

        /// <summary>设 Image 填充量 0~1（进度/血条）。</summary>
        public static void SetFill(GameObject go, float amount)
        {
            if (go == null) return;
            var img = go.GetComponent<Image>();
            if (img == null) return;
            if (img.type != Image.Type.Filled) img.type = Image.Type.Filled;
            img.fillAmount = Mathf.Clamp01(amount);
        }

        /// <summary>设/读 CheckBox(Toggle) 勾选态。</summary>
        public static void SetChecked(GameObject go, bool on)
        {
            if (go == null) return;
            var tg = go.GetComponent<Toggle>();
            if (tg != null) tg.isOn = on;
        }

        public static bool GetChecked(GameObject go)
        {
            if (go == null) return false;
            var tg = go.GetComponent<Toggle>();
            return tg != null && tg.isOn;
        }

        /// <summary>运行时绑定 Button 点击到 Lua 全局函数（替换式，BHQSL SetWindowEvent 等价）。</summary>
        public static void BindClick(GameObject go, string fn)
        {
            if (go == null || string.IsNullOrEmpty(fn)) return;
            var btn = go.GetComponent<Button>();
            if (btn == null) { Debug.LogWarning($"[HDUIXmlHost] BindClick: {go.name} 上无 Button"); return; }
            btn.onClick.RemoveAllListeners();
            var self = go;
            btn.onClick.AddListener(() => LuaHost.CallGlobal(fn, self));
        }

        /// <summary>运行时把模板实例化到 parent 下（动态列表填充）。返回新控件 GameObject。</summary>
        public static GameObject CreateFromTemplate(GameObject parent, string templateName, string instanceName)
        {
            if (parent == null) { Debug.LogWarning("[HDUIXmlHost] CreateFromTemplate: parent 为空"); return null; }
            EnsureTemplatesLoaded();
            // parent 是 List/ScrolledWindow → 建到其内容容器（否则动态行绕过 ScrollRect）
            RectTransform prt;
            var sr = parent.GetComponent<ScrollRect>();
            if (sr != null && sr.content != null) prt = sr.content;
            else prt = parent.GetComponent<RectTransform>();
            if (prt == null) return null;
            return Builder.BuildTemplateInstance(templateName, instanceName, prt);
        }

        /// <summary>清空容器（或 List 内容容器）的所有子控件（列表刷新用）。传 List 根会自动定位到其 Content。
        /// 先脱离层级再 Destroy → childCount 即时归零、不与同帧新建行重叠（play 模式 Destroy 延迟帧末）。</summary>
        public static void DestroyChildren(GameObject go)
        {
            if (go == null) return;
            Transform container = go.transform;
            var sr = go.GetComponent<ScrollRect>();
            if (sr != null && sr.content != null) container = sr.content;   // List/ScrolledWindow → 清内容容器
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                var child = container.GetChild(i).gameObject;
                child.transform.SetParent(null, false);   // 立即脱离 → 不再计入 childCount / 不参与本帧布局
                Destroy(child);
            }
        }

        /// <summary>显隐 GameObject。</summary>
        public static void SetActive(GameObject go, bool active)
        {
            if (go != null) go.SetActive(active);
        }

        /// <summary>把面板/控件置为同级最后一个子 → 渲染在最上层（模态弹窗置顶用）。</summary>
        public static void BringToFront(GameObject go)
        {
            if (go != null) go.transform.SetAsLastSibling();
        }

        /// <summary>给控件挂拖拽源组件（DragInputBridge）+ 设来源（npcId + source 类型如 "inventory"）。
        /// 热更 UI 的库存卡 = 可拖拽部署源；XML builder 无法附加该组件 → 经此桥运行时 AddComponent + SetSource。
        /// 业务（Battle_OnDragBegin/Move/End）不变，仅补回组件挂载这一步。</summary>
        public static void AttachDragSource(GameObject card, long npcId, string source)
        {
            if (card == null) return;
            var dib = card.GetComponent<HeroDefense.Battle.DragInputBridge>();
            if (dib == null) dib = card.AddComponent<HeroDefense.Battle.DragInputBridge>();
            dib.SetSource(npcId, source ?? "");
        }

        // ====================================================================
        // 内部
        // ====================================================================

        // UI 父节点解析：优先 Panel_RootWindow Tag → 任意 Canvas → 新建一个 Canvas（兜底，保证有渲染目标）
        static RectTransform ResolveUIParent()
        {
            var tagged = UIFinder.FindPanelByTag("Panel_RootWindow");
            if (tagged != null) return tagged.GetComponent<RectTransform>() ?? tagged.transform as RectTransform;

            var canvas = Object.FindObjectOfType<Canvas>();
            if (canvas != null) return canvas.transform as RectTransform;

            var go = new GameObject("UIXmlCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var cv = go.GetComponent<Canvas>();
            cv.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            Debug.LogWarning("[HDUIXmlHost] 未找到现成 Canvas，已兜底新建 UIXmlCanvas（1920×1080 Expand）");
            return go.transform as RectTransform;
        }

        static void Destroy(GameObject go)
        {
            if (go == null) return;
            if (Application.isPlaying) Object.Destroy(go);
            else Object.DestroyImmediate(go);
        }
    }
}
