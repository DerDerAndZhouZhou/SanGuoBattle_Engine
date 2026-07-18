using System.Collections.Generic;
using System.Xml.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace HeroDefense.UI.Xml
{
    /// <summary>
    /// 宿主抽象（热更 UI 系统的「接口隔离」核心，见 Docs/bhqsl-ui-research-2026-06.md §4）。
    ///
    /// UIXmlBuilder 只依赖本接口，不依赖 ResourceHost / LuaHost / 任何游戏单例 →
    /// 同一份构建逻辑可被「游戏运行时」与「编辑器预览」两处复用，零漂移：
    ///   - 游戏端（HDUIXmlHost）：LoadSprite 走 ResourceHost；OnXxx 钩子转 LuaHost.CallGlobal。
    ///   - 编辑器端（后续 uieditorengine）：LoadSprite 本地直读；钩子可空实现（预览不跑 Lua）。
    /// 这是「两工程能否分开」的答案——靠共享包 + 接口隔离，而非复制 UIXmlBuilder。
    /// </summary>
    public interface IUIXmlHost
    {
        /// <summary>按 sprite key 取图；缺失返回 null（builder 会用纯白兜底图 + color 渲染）。</summary>
        Sprite LoadSprite(string spriteKey);

        /// <summary>按 sprite key 与 Unity border(L,B,R,T) 取九宫 Sprite；缺失返回 null。</summary>
        Sprite LoadSprite(string spriteKey, Vector4 border);

        /// <summary>文本控件用的字体。</summary>
        Font GetFont();

        /// <summary>面板构建完成后触发 &lt;Script event="OnLoad" fn="X"/&gt;（fn = Lua 全局函数名，arg = 面板 GameObject）。</summary>
        void OnPanelLoaded(string fn, GameObject panel);

        /// <summary>控件 onClick 触发（fn = Lua 全局函数名，sender = 被点控件 GameObject）。</summary>
        void OnControlClicked(string fn, GameObject sender);

        /// <summary>控件值变更触发（CheckBox/Edit 等；value = 字符串化的值："true"/"false" 或输入文本）。</summary>
        void OnControlValueChanged(string fn, GameObject sender, string value);
    }

    /// <summary>
    /// XML → UGUI 运行时构建器（热更 UI 系统 阶段0 核心）。
    ///
    /// 设计目标：自包含、零游戏依赖、易抽成共享 UPM 包（被游戏引擎 + 编辑器引擎双方引用）。
    /// 仅依赖 UnityEngine + UnityEngine.UI + IUIXmlHost。
    ///
    /// 支持控件（阶段0）：Panel/Window（容器）/ Image / Text / Button（含嵌套子控件）。
    /// 支持属性：name / anchor（9 锚点 + stretch 系列）/ pos / size / margin / sprite / color /
    ///           text / fontSize / align / onClick / visible。
    /// 面板级钩子：&lt;Scripts&gt;&lt;Script event="OnLoad" fn="..."/&gt;&lt;/Scripts&gt;。
    ///
    /// 坐标语义 = UGUI 原生（不复刻 BHQSL 的 Ogre 29 坐标类型）：
    ///   anchor 关键字 → anchorMin/Max/pivot；pos → anchoredPosition（Y 向上）；
    ///   size → sizeDelta；margin（L,T,R,B）→ stretch 轴的 offsetMin/Max。
    ///   分辨率无关由场景里 CanvasScaler(ScaleWithScreenSize + Expand) 提供，本类不管缩放。
    /// </summary>
    public class UIXmlBuilder
    {
        readonly IUIXmlHost _host;

        /// <summary>
        /// 可选回链回调（编辑器用·向后兼容）：每构建一个控件 GameObject 后触发 (elem, go)。
        /// 游戏端不设（保持 null）→ 现有渲染逻辑零影响；编辑器端用它建 XElement↔GameObject 双向映射做画布直接操作。
        /// 注意：InheritFrom 节点的 elem 是 MergeTemplate 后的克隆，非原树节点（编辑器据此禁用其画布写回，见架构 §8）。
        /// </summary>
        public System.Action<XElement, GameObject> OnControlBuilt;

        // 保留标签：不当作控件构建（OnLoad 等在 <Scripts> 内，BHQSL 风格的 <Anchors>/<Size> 子块为未来预留）
        static readonly HashSet<string> _reservedTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "Scripts", "Anchors", "Anchor", "Size", "Templates"
        };

        // 模板注册表（InheritFrom 机制 ≈ Prefab + Variant override）。
        // 来源：① 模板文件 Game/ui/templates/*.xml（HDUIXmlHost 扫入）② 面板内 Virtual="true" 节点。
        // 跨面板复用 → 存在 builder 实例（HDUIXmlHost 持一份）。
        readonly Dictionary<string, XElement> _templates =
            new Dictionary<string, XElement>(System.StringComparer.OrdinalIgnoreCase);

        public UIXmlBuilder(IUIXmlHost host)
        {
            _host = host;
        }

        /// <summary>注册模板文件：root = &lt;Templates&gt;，每个直接子元素 = 一个模板（按 name 注册）。HDUIXmlHost 启动期扫 Game/ui/templates/。</summary>
        public void RegisterTemplatesFromXml(string xmlText)
        {
            if (string.IsNullOrEmpty(xmlText)) return;
            XDocument doc;
            try { doc = XDocument.Parse(xmlText); }
            catch (System.Exception e) { Debug.LogError($"[UIXmlBuilder] 模板 XML 解析失败: {e.Message}"); return; }
            if (doc.Root == null) return;
            foreach (var el in doc.Root.Elements())
            {
                string n = Attr(el, "name");
                if (!string.IsNullOrEmpty(n)) _templates[n] = el;
            }
        }

        public int TemplateCount => _templates.Count;

        // 命名色表（主题）：color="primary" 等名字 → 颜色。来源 Game/ui/theme.xml（HDUIXmlHost 扫入）。
        // 价值：统一调色板 + 编辑器色板预设 + 改主题集中一处。
        readonly Dictionary<string, Color> _namedColors =
            new Dictionary<string, Color>(System.StringComparer.OrdinalIgnoreCase);

        public int ColorCount => _namedColors.Count;

        /// <summary>注册主题色：root 下 &lt;Color name="primary" value="#RRGGBBAA"/&gt;。HDUIXmlHost 启动期扫 Game/ui/theme.xml。</summary>
        public void RegisterColorsFromXml(string xmlText)
        {
            if (string.IsNullOrEmpty(xmlText)) return;
            XDocument doc;
            try { doc = XDocument.Parse(xmlText); }
            catch (System.Exception e) { Debug.LogError($"[UIXmlBuilder] 主题 XML 解析失败: {e.Message}"); return; }
            if (doc.Root == null) return;
            foreach (var el in doc.Root.Elements("Color"))
            {
                string n = Attr(el, "name");
                string v = Attr(el, "value");
                Color c;
                if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(v) && ColorUtility.TryParseHtmlString(v.Trim(), out c))
                    _namedColors[n] = c;
            }
        }

        /// <summary>
        /// 解析 XML 文本并在 parent 下构建一棵 UGUI 控件树，返回面板根 GameObject。
        /// 解析失败返回 null（不抛异常，调用方判 null）。
        /// </summary>
        public GameObject Build(string xmlText, RectTransform parent)
        {
            if (string.IsNullOrEmpty(xmlText))
            {
                Debug.LogError("[UIXmlBuilder] Build: xml 为空");
                return null;
            }

            XDocument doc;
            try { doc = XDocument.Parse(xmlText); }
            catch (System.Exception e)
            {
                Debug.LogError($"[UIXmlBuilder] XML 解析失败: {e.Message}");
                return null;
            }
            if (doc.Root == null)
            {
                Debug.LogError("[UIXmlBuilder] XML 无根节点");
                return null;
            }
            return Build(doc.Root, parent);
        }

        /// <summary>
        /// 从已解析的 XElement 模型构建（编辑器持 XDocument、改属性后从内存重建预览用；非破坏性：
        /// InheritFrom 合并克隆新节点不改原树）。返回面板根 GameObject。
        /// </summary>
        public GameObject Build(XElement root, RectTransform parent)
        {
            if (root == null)
            {
                Debug.LogError("[UIXmlBuilder] Build: root 为空");
                return null;
            }
            // 先登记面板内 Virtual="true" 模板节点（供同面板 InheritFrom 引用；构建时跳过）
            RegisterVirtualNodes(root);

            GameObject panel = BuildControl(root, parent);
            if (panel == null) return null;

            // 面板级 OnLoad 钩子
            FirePanelScripts(root, panel);
            return panel;
        }

        /// <summary>运行时把已注册模板实例化到 parent 下（动态列表填充用）。返回新控件，模板不存在返回 null。</summary>
        public GameObject BuildTemplateInstance(string templateName, string instanceName, RectTransform parent)
        {
            if (string.IsNullOrEmpty(templateName) || !_templates.ContainsKey(templateName))
            {
                Debug.LogWarning($"[UIXmlBuilder] BuildTemplateInstance 模板未找到: {templateName}");
                return null;
            }
            // 合成一个仅含 InheritFrom 的占位元素 → 复用 BuildControl 里的模板合并逻辑（标签由模板决定）
            var synthetic = new XElement("Control");
            synthetic.SetAttributeValue("InheritFrom", templateName);
            if (!string.IsNullOrEmpty(instanceName)) synthetic.SetAttributeValue("name", instanceName);
            return BuildControl(synthetic, parent);
        }

        /// <summary>对外解析颜色（命名色表 + #hex + CSS 名）。供 HDUIXmlHost.UI_SetColor 复用同一套规则。</summary>
        public bool TryResolveColor(string s, out Color c) { return TryParseColor(s, out c); }

        // 递归登记带 Virtual="true" 的节点为模板（按 name）。
        void RegisterVirtualNodes(XElement el)
        {
            foreach (var child in el.Elements())
            {
                if (IsVirtual(child))
                {
                    string n = Attr(child, "name");
                    if (!string.IsNullOrEmpty(n)) _templates[n] = child;
                }
                RegisterVirtualNodes(child);
            }
        }

        static bool IsVirtual(XElement el)
        {
            string v = Attr(el, "Virtual") ?? Attr(el, "virtual");
            return v == "true" || v == "1";
        }

        // InheritFrom 合并：以模板为基，用实例的属性 + 同名子节点做差量覆盖（递归 → 支持覆盖模板深层子节点）。
        static XElement MergeTemplate(XElement baseTpl, XElement over)
        {
            var result = new XElement(baseTpl);   // 深克隆模板（标签 + 属性 + 子树）
            // 实例属性覆盖模板属性（InheritFrom/Virtual 不带过去）
            foreach (var a in over.Attributes())
            {
                string an = a.Name.LocalName;
                if (an.Equals("InheritFrom", System.StringComparison.OrdinalIgnoreCase) ||
                    an.Equals("inherit", System.StringComparison.OrdinalIgnoreCase) ||
                    an.Equals("Virtual", System.StringComparison.OrdinalIgnoreCase) ||
                    an.Equals("virtual", System.StringComparison.OrdinalIgnoreCase)) continue;
                result.SetAttributeValue(a.Name, a.Value);
            }
            // 模板自身不应再带 Virtual 标记
            result.Attribute("Virtual")?.Remove();
            result.Attribute("virtual")?.Remove();
            // 同名子节点合并；实例独有子节点追加
            foreach (var oc in over.Elements())
            {
                if (_reservedTags.Contains(oc.Name.LocalName)) { result.Add(new XElement(oc)); continue; }
                string cn = Attr(oc, "name");
                XElement bc = null;
                if (!string.IsNullOrEmpty(cn))
                {
                    foreach (var e in result.Elements())
                        if (string.Equals(Attr(e, "name"), cn, System.StringComparison.OrdinalIgnoreCase)) { bc = e; break; }
                }
                if (bc != null)
                {
                    var merged = MergeTemplate(bc, oc);
                    bc.ReplaceWith(merged);
                }
                else
                {
                    result.Add(new XElement(oc));
                }
            }
            return result;
        }

        // ============ 递归构建 ============

        GameObject BuildControl(XElement elem, RectTransform parent)
        {
            // InheritFrom：用模板实例化 + 差量覆盖（≈ Prefab Variant override）
            string inheritFrom = Attr(elem, "InheritFrom") ?? Attr(elem, "inherit");
            if (!string.IsNullOrEmpty(inheritFrom))
            {
                XElement tpl;
                if (_templates.TryGetValue(inheritFrom, out tpl) && tpl != null)
                    elem = MergeTemplate(tpl, elem);
                else
                    Debug.LogWarning($"[UIXmlBuilder] InheritFrom 模板未找到: {inheritFrom}（按原节点构建）");
            }

            string tag = elem.Name.LocalName;
            string name = Attr(elem, "name");

            GameObject go;
            RectTransform childParent = null;   // 子控件挂载点；List/ScrolledWindow 指向其内容容器

            switch (tag.ToLowerInvariant())
            {
                case "panel":
                case "window":
                    go = NewUI(name, tag, typeof(RectTransform));
                    break;
                case "image":
                    go = NewUI(name, tag, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    ApplyImage(go.GetComponent<Image>(), elem);
                    break;
                case "text":
                    go = NewUI(name, tag, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
                    ApplyText(go.GetComponent<Text>(), elem);
                    break;
                case "button":
                    go = NewUI(name, tag, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
                    ApplyImage(go.GetComponent<Image>(), elem);
                    ApplyButton(go, go.GetComponent<Button>(), elem);
                    break;
                case "checkbox":
                    go = BuildCheckBox(name, tag, elem);
                    break;
                case "edit":
                    go = BuildEdit(name, tag, elem);
                    break;
                case "list":
                    go = BuildScroll(name, tag, elem, true, out childParent);
                    break;
                case "scrolledwindow":
                    go = BuildScroll(name, tag, elem, false, out childParent);
                    break;
                default:
                    Debug.LogWarning($"[UIXmlBuilder] 未知控件 <{tag}> → 当作容器处理（name={name}）");
                    go = NewUI(name, tag, typeof(RectTransform));
                    break;
            }

            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            ApplyRect(rt, elem);
            ApplyWindowProps(go, rt, elem);

            // visible
            string vis = Attr(elem, "visible");
            if (!string.IsNullOrEmpty(vis) && (vis == "false" || vis == "0"))
                go.SetActive(false);

            // 若父容器是布局组（List 内容容器），按 size 自动补 LayoutElement → 子项定宽高正确（否则 VLG 下高度算 0 被压扁）
            var lg = parent != null ? parent.GetComponent<HorizontalOrVerticalLayoutGroup>() : null;
            if (lg != null)
            {
                Vector2 sz = ParseVec2(Attr(elem, "size"), Vector2.zero);
                var le = go.GetComponent<LayoutElement>();
                if (le == null) le = go.AddComponent<LayoutElement>();
                if (sz.x > 0f) le.preferredWidth = sz.x;
                if (sz.y > 0f) { le.preferredHeight = sz.y; le.minHeight = sz.y; }
            }

            // 递归子控件（跳过 <Scripts> 等保留块 + Virtual 模板节点）；List/ScrolledWindow 挂到内容容器
            if (childParent == null) childParent = rt;
            foreach (var child in elem.Elements())
            {
                if (_reservedTags.Contains(child.Name.LocalName)) continue;
                if (IsVirtual(child)) continue;   // 模板节点本身不构建，仅供 InheritFrom 引用
                BuildControl(child, childParent);
            }

            OnControlBuilt?.Invoke(elem, go);   // 编辑器回链（游戏端为 null·不影响）
            return go;
        }

        GameObject NewUI(string name, string tag, params System.Type[] comps)
        {
            string goName = string.IsNullOrEmpty(name) ? tag : name;
            return new GameObject(goName, comps);
        }

        // ============ 组件应用 ============

        void ApplyImage(Image img, XElement elem)
        {
            if (img == null) return;
            string spriteKey = Attr(elem, "sprite");
            string fillStr = Attr(elem, "fill");
            string imageType = (Attr(elem, "imageType") ?? "").Trim().ToLowerInvariant();
            bool useBorderVariant = string.IsNullOrEmpty(fillStr) && (imageType == "sliced" || imageType == "tiled");
            Vector4 xmlBorder = ParseVec4(Attr(elem, "border"), Vector4.zero); // XML: L,T,R,B
            Vector4 unityBorder = new Vector4(
                Mathf.Max(0f, xmlBorder.x),
                Mathf.Max(0f, xmlBorder.w),
                Mathf.Max(0f, xmlBorder.z),
                Mathf.Max(0f, xmlBorder.y)); // Unity: L,B,R,T
            Sprite s = null;
            if (!string.IsNullOrEmpty(spriteKey) && _host != null)
            {
                s = useBorderVariant ? _host.LoadSprite(spriteKey, unityBorder) : _host.LoadSprite(spriteKey);
            }
            img.sprite = s != null ? s : WhiteSprite();

            // 缺图时用纯白兜底，靠 color 显示为纯色块（PoC 默认走这条）
            Color c;
            if (TryParseColor(Attr(elem, "color"), out c)) img.color = c;
            else img.color = Color.white;

            // fill：把 Image 当进度/填充条（exp/loading/血条），值 0~1
            if (!string.IsNullOrEmpty(fillStr))
            {
                img.type = Image.Type.Filled;
                img.fillMethod = ParseFillMethod(Attr(elem, "fillMethod"));
                img.fillAmount = Mathf.Clamp01(ParseF(fillStr, 1f));
                img.fillOrigin = (int)ParseF(Attr(elem, "fillOrigin"), 0f);
            }
            else if (imageType == "sliced")
            {
                img.type = Image.Type.Sliced;
            }
            else if (imageType == "tiled")
            {
                img.type = Image.Type.Tiled;
            }
            else
            {
                img.type = Image.Type.Simple;
            }

            // preserveAspect：立绘/卡面按原图比例适配 rect（不拉伸变形）。迁移真实面板（详情/库存/商店头像）需要。
            string pa = Attr(elem, "preserveAspect");
            img.preserveAspect = (pa == "true" || pa == "1");

            img.raycastTarget = true;
        }

        static Image.FillMethod ParseFillMethod(string s)
        {
            switch ((s ?? "horizontal").Trim().ToLowerInvariant())
            {
                case "vertical":   return Image.FillMethod.Vertical;
                case "radial":
                case "radial360":  return Image.FillMethod.Radial360;
                case "horizontal":
                default:           return Image.FillMethod.Horizontal;
            }
        }

        void ApplyText(Text txt, XElement elem)
        {
            if (txt == null) return;
            txt.font = _host != null ? _host.GetFont() : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            string content = Attr(elem, "text") ?? "";
            txt.text = content.Replace("\\n", "\n");   // 允许 XML 属性里写 \n 换行

            int fs;
            if (int.TryParse(Attr(elem, "fontSize"), out fs) && fs > 0) txt.fontSize = fs;
            else txt.fontSize = 24;

            Color c;
            txt.color = TryParseColor(Attr(elem, "color"), out c) ? c : Color.white;

            txt.alignment = ParseAlign(Attr(elem, "align"));
            // wrap="true"：按宽度自动换行（描述/多行文本用）；默认 Overflow（单行标签，不裁切）。
            string wrapAttr = Attr(elem, "wrap");
            bool wrap = (wrapAttr == "true" || wrapAttr == "1");
            txt.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.supportRichText = true;
            txt.raycastTarget = false;
        }

        void ApplyButton(GameObject go, Button btn, XElement elem)
        {
            if (btn == null) return;
            string fn = Attr(elem, "onClick");
            if (!string.IsNullOrEmpty(fn))
            {
                var host = _host;
                btn.onClick.AddListener(() =>
                {
                    if (host != null) host.OnControlClicked(fn, go);
                });
            }

            // Button 多态贴图（BHQSL 5 态）：normal=Image 的 sprite；提供 highlighted/pressed/disabled 任一 → 切 SpriteSwap。
            // 无任何状态图时保持默认 ColorTint（UGUI 自带 hover/press 色调反馈）。
            Sprite spHi = LoadSpriteOrNull(Attr(elem, "spriteHighlighted"));
            Sprite spPr = LoadSpriteOrNull(Attr(elem, "spritePressed"));
            Sprite spDi = LoadSpriteOrNull(Attr(elem, "spriteDisabled"));
            if (spHi != null || spPr != null || spDi != null)
            {
                btn.transition = Selectable.Transition.SpriteSwap;
                var img = go.GetComponent<Image>();
                if (img != null) btn.targetGraphic = img;
                var ss = btn.spriteState;
                ss.highlightedSprite = spHi;
                ss.pressedSprite = spPr;
                ss.disabledSprite = spDi;
                btn.spriteState = ss;
            }

            // enabled="false" → 禁用交互（走 disabled 态）
            string en = Attr(elem, "enabled");
            if (en == "false" || en == "0") btn.interactable = false;
        }

        Sprite LoadSpriteOrNull(string key)
        {
            if (string.IsNullOrEmpty(key) || _host == null) return null;
            return _host.LoadSprite(key);
        }

        // ============ 1a 扩展控件 ============

        // CheckBox → UGUI Toggle（左侧方框 + 勾；可嵌套 <Text> 当标签，作者自行定位）。
        // 属性：checked(true/1) / boxColor / checkColor / box(方框边长,默认36) / onChange(Lua 全局名,传 "true"/"false")
        GameObject BuildCheckBox(string name, string tag, XElement elem)
        {
            var go = NewUI(name, tag, typeof(RectTransform));
            var toggle = go.AddComponent<Toggle>();

            float box = ParseF(Attr(elem, "box"), 36f);

            var bg = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            bg.transform.SetParent(go.transform, false);
            var bgrt = bg.GetComponent<RectTransform>();
            bgrt.anchorMin = new Vector2(0, 0.5f); bgrt.anchorMax = new Vector2(0, 0.5f); bgrt.pivot = new Vector2(0, 0.5f);
            bgrt.sizeDelta = new Vector2(box, box); bgrt.anchoredPosition = Vector2.zero;
            var bgImg = bg.GetComponent<Image>();
            bgImg.sprite = WhiteSprite();
            Color bc; bgImg.color = TryParseColor(Attr(elem, "boxColor"), out bc) ? bc : new Color(1, 1, 1, 0.22f);

            var ck = new GameObject("Checkmark", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            ck.transform.SetParent(bg.transform, false);
            var ckrt = ck.GetComponent<RectTransform>();
            ckrt.anchorMin = new Vector2(0.18f, 0.18f); ckrt.anchorMax = new Vector2(0.82f, 0.82f);
            ckrt.offsetMin = Vector2.zero; ckrt.offsetMax = Vector2.zero;
            var ckImg = ck.GetComponent<Image>();
            ckImg.sprite = WhiteSprite();
            Color cc; ckImg.color = TryParseColor(Attr(elem, "checkColor"), out cc) ? cc : new Color(0.3f, 0.85f, 0.35f, 1f);

            toggle.targetGraphic = bgImg;
            toggle.graphic = ckImg;
            string chk = Attr(elem, "checked");
            toggle.isOn = (chk == "true" || chk == "1");

            string fn = Attr(elem, "onChange");
            if (!string.IsNullOrEmpty(fn))
            {
                var host = _host; var self = go;
                toggle.onValueChanged.AddListener((bool v) => { if (host != null) host.OnControlValueChanged(fn, self, v ? "true" : "false"); });
            }
            return go;
        }

        // Edit → UGUI InputField（背景 + Text + Placeholder）。
        // 属性：text / placeholder / fontSize / color(背景) / textColor / onChange(Lua,传输入文本,onEndEdit)
        GameObject BuildEdit(string name, string tag, XElement elem)
        {
            var go = NewUI(name, tag, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var bgImg = go.GetComponent<Image>();
            bgImg.sprite = WhiteSprite();
            Color bc; bgImg.color = TryParseColor(Attr(elem, "color"), out bc) ? bc : new Color(1, 1, 1, 0.12f);

            var input = go.AddComponent<InputField>();
            var font = _host != null ? _host.GetFont() : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            int fs; if (!int.TryParse(Attr(elem, "fontSize"), out fs) || fs <= 0) fs = 24;
            Color tc; if (!TryParseColor(Attr(elem, "textColor"), out tc)) tc = Color.white;

            var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textGo.transform.SetParent(go.transform, false);
            StretchWithPadding(textGo.GetComponent<RectTransform>(), 10f, 6f);
            var t = textGo.GetComponent<Text>();
            t.font = font; t.fontSize = fs; t.color = tc; t.alignment = TextAnchor.MiddleLeft;
            t.supportRichText = false;

            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            phGo.transform.SetParent(go.transform, false);
            StretchWithPadding(phGo.GetComponent<RectTransform>(), 10f, 6f);
            var ph = phGo.GetComponent<Text>();
            ph.font = font; ph.fontSize = fs; ph.color = new Color(tc.r, tc.g, tc.b, 0.4f);
            ph.alignment = TextAnchor.MiddleLeft; ph.fontStyle = FontStyle.Italic;
            ph.text = Attr(elem, "placeholder") ?? "";

            input.textComponent = t;
            input.placeholder = ph;
            input.text = Attr(elem, "text") ?? "";

            string fn = Attr(elem, "onChange");
            if (!string.IsNullOrEmpty(fn))
            {
                var host = _host; var self = go;
                input.onEndEdit.AddListener((string v) => { if (host != null) host.OnControlValueChanged(fn, self, v); });
            }
            return go;
        }

        // List / ScrolledWindow → UGUI ScrollRect + Viewport(Mask) + Content。
        //   autoLayout=true(List)：Content 加 Vertical/HorizontalLayoutGroup + ContentSizeFitter，子控件自动排布。
        //   autoLayout=false(ScrolledWindow)：Content 充满 viewport，自由放绝对定位子控件。
        // 属性：color(背景) / direction(vertical|horizontal) / spacing / padding(L,T,R,B)；子控件挂到 content(out)。
        GameObject BuildScroll(string name, string tag, XElement elem, bool autoLayout, out RectTransform content)
        {
            var go = NewUI(name, tag, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(ScrollRect));
            var bgImg = go.GetComponent<Image>();
            bgImg.sprite = WhiteSprite();
            Color bc; bgImg.color = TryParseColor(Attr(elem, "color"), out bc) ? bc : new Color(0, 0, 0, 0.15f);

            var vp = new GameObject("Viewport", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Mask));
            vp.transform.SetParent(go.transform, false);
            var vprt = vp.GetComponent<RectTransform>();
            vprt.anchorMin = Vector2.zero; vprt.anchorMax = Vector2.one; vprt.offsetMin = Vector2.zero; vprt.offsetMax = Vector2.zero;
            vp.GetComponent<Image>().color = new Color(1, 1, 1, 0.01f);
            vp.GetComponent<Mask>().showMaskGraphic = false;

            var contentGo = new GameObject("Content", typeof(RectTransform), typeof(CanvasRenderer));
            contentGo.transform.SetParent(vp.transform, false);
            var crt = contentGo.GetComponent<RectTransform>();

            bool horizontal = (Attr(elem, "direction") ?? "vertical").Trim().ToLowerInvariant().StartsWith("h");

            var scroll = go.GetComponent<ScrollRect>();
            scroll.viewport = vprt;
            scroll.content = crt;
            scroll.horizontal = horizontal;
            scroll.vertical = !horizontal;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 20f;

            if (autoLayout)
            {
                if (horizontal) { crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(0, 1); crt.pivot = new Vector2(0, 0.5f); }
                else            { crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1); crt.pivot = new Vector2(0.5f, 1); }
                crt.anchoredPosition = Vector2.zero; crt.sizeDelta = Vector2.zero;

                float spacing = ParseF(Attr(elem, "spacing"), 4f);
                Vector4 pad = ParseVec4(Attr(elem, "padding"), new Vector4(8, 8, 8, 8));
                var ro = new RectOffset((int)pad.x, (int)pad.z, (int)pad.y, (int)pad.w);  // L,R,T,B
                if (horizontal)
                {
                    var hlg = contentGo.AddComponent<HorizontalLayoutGroup>();
                    hlg.spacing = spacing; hlg.padding = ro;
                    hlg.childControlWidth = true; hlg.childControlHeight = true;
                    hlg.childForceExpandWidth = false; hlg.childForceExpandHeight = true;
                    hlg.childAlignment = TextAnchor.MiddleLeft;
                    var fit = contentGo.AddComponent<ContentSizeFitter>();
                    fit.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                    fit.verticalFit = ContentSizeFitter.FitMode.Unconstrained;
                }
                else
                {
                    var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
                    vlg.spacing = spacing; vlg.padding = ro;
                    vlg.childControlWidth = true; vlg.childControlHeight = true;
                    vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
                    vlg.childAlignment = TextAnchor.UpperLeft;
                    var fit = contentGo.AddComponent<ContentSizeFitter>();
                    fit.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                    fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }
            }
            else
            {
                crt.anchorMin = Vector2.zero; crt.anchorMax = Vector2.one;
                crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
            }

            content = crt;
            return go;
        }

        static void StretchWithPadding(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY); rt.offsetMax = new Vector2(-padX, -padY);
        }

        // ============ RectTransform 布局 ============

        void ApplyRect(RectTransform rt, XElement elem)
        {
            rt.localScale = Vector3.one;
            if (UsesNewRectModel(elem)) ApplyRectModel(rt, elem);
            else ApplyRectLegacy(rt, elem);
        }

        // 是否用新（BHQSL 式·显式）定位模型——只要出现任一新属性就走新路径。
        static bool UsesNewRectModel(XElement e)
        {
            return Attr(e, "point") != null || Attr(e, "relativePoint") != null
                || Attr(e, "widthMode") != null || Attr(e, "heightMode") != null
                || Attr(e, "width") != null || Attr(e, "height") != null
                || Attr(e, "offsetX") != null || Attr(e, "offsetY") != null;
        }

        // 新模型（BHQSL 式）：自身锚点 point 对齐 相对窗口(=父)的 相对锚点 relativePoint + 偏移(offsetX/Y 像素) ；
        //   宽/高各有坐标系：abs=绝对像素 / percent=占父窗口百分比(原生 anchor 拉伸→分辨率无关)。
        //   全部映射到 UGUI 原生 anchorMin/Max + pivot + offsetMin/Max（相对窗口固定=父，UGUI RT 只能锚父）。
        void ApplyRectModel(RectTransform rt, XElement elem)
        {
            Vector2 pivot = PointFrac(Attr(elem, "point") ?? "center");
            Vector2 rel = PointFrac(Attr(elem, "relativePoint") ?? Attr(elem, "point") ?? "center");
            float offX = ParseF(Attr(elem, "offsetX"), 0f);
            float offY = ParseF(Attr(elem, "offsetY"), 0f);
            rt.pivot = pivot;

            float aMinX, aMaxX, oMinX, oMaxX, aMinY, aMaxY, oMinY, oMaxY;
            AxisModel(Attr(elem, "widthMode"), ParseF(Attr(elem, "width"), 100f), rel.x, pivot.x, offX, out aMinX, out aMaxX, out oMinX, out oMaxX);
            AxisModel(Attr(elem, "heightMode"), ParseF(Attr(elem, "height"), 100f), rel.y, pivot.y, offY, out aMinY, out aMaxY, out oMinY, out oMaxY);
            rt.anchorMin = new Vector2(aMinX, aMinY);
            rt.anchorMax = new Vector2(aMaxX, aMaxY);
            rt.offsetMin = new Vector2(oMinX, oMinY);
            rt.offsetMax = new Vector2(oMaxX, oMaxY);
        }

        // 单轴解算：percent → anchor 拉伸占父 val%（offset 为像素平移，宽随父变=分辨率无关）；
        //           abs → 单点锚定(anchorMin=anchorMax=相对点) + val 像素定宽，自身点对齐相对点+偏移。
        static void AxisModel(string mode, float val, float rel, float piv, float off,
            out float aMin, out float aMax, out float oMin, out float oMax)
        {
            bool percent = (mode ?? "abs").Trim().ToLowerInvariant().StartsWith("p");
            if (percent)
            {
                float f = val / 100f;
                aMin = rel - piv * f;
                aMax = rel + (1f - piv) * f;
                oMin = off; oMax = off;
            }
            else
            {
                aMin = rel; aMax = rel;
                oMin = off - piv * val;
                oMax = off + (1f - piv) * val;
            }
        }

        // 9 宫格关键字 → frac(0..1, Y 向上)。自身锚点与相对锚点共用。
        static Vector2 PointFrac(string key)
        {
            switch ((key ?? "center").Trim().ToLowerInvariant())
            {
                case "top-left":      return new Vector2(0f, 1f);
                case "top": case "top-center":       return new Vector2(0.5f, 1f);
                case "top-right":     return new Vector2(1f, 1f);
                case "left": case "middle-left": case "center-left":   return new Vector2(0f, 0.5f);
                case "right": case "middle-right": case "center-right": return new Vector2(1f, 0.5f);
                case "bottom-left":   return new Vector2(0f, 0f);
                case "bottom": case "bottom-center": return new Vector2(0.5f, 0f);
                case "bottom-right":  return new Vector2(1f, 0f);
                case "center": case "middle": default: return new Vector2(0.5f, 0.5f);
            }
        }

        // 旧模型（anchor 关键字 + pos + size + margin）保留兼容，未迁移面板照常渲染。
        void ApplyRectLegacy(RectTransform rt, XElement elem)
        {
            Vector2 min, max, pivot;
            ResolveAnchor(Attr(elem, "anchor"), out min, out max, out pivot);
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.pivot = pivot;

            Vector2 pos = ParseVec2(Attr(elem, "pos"), Vector2.zero);
            Vector2 size = ParseVec2(Attr(elem, "size"), new Vector2(100f, 100f));
            Vector4 m = ParseVec4(Attr(elem, "margin"), Vector4.zero);
            float L = m.x, T = m.y, R = m.z, B = m.w;

            bool hStretch = !Mathf.Approximately(min.x, max.x);
            bool vStretch = !Mathf.Approximately(min.y, max.y);

            float offMinX = hStretch ? L  : pos.x - pivot.x * size.x;
            float offMaxX = hStretch ? -R : pos.x + (1f - pivot.x) * size.x;
            float offMinY = vStretch ? B  : pos.y - pivot.y * size.y;
            float offMaxY = vStretch ? -T : pos.y + (1f - pivot.y) * size.y;

            rt.offsetMin = new Vector2(offMinX, offMinY);
            rt.offsetMax = new Vector2(offMaxX, offMaxY);
        }

        // 基础界面属性（BHQSL 式·UGUI 扩展）：缩放 scale / 透明度 alpha(CanvasGroup,含子树) /
        //   鼠标穿透 mousePassthrough、响应由子窗口决定 hitByChildren → 关本控件 Graphic.raycastTarget(子控件不受影响)。
        //   与父共享响应 shareWithParent：UGUI 事件默认上冒泡=已共享，存属性供编辑器/未来用。
        void ApplyWindowProps(GameObject go, RectTransform rt, XElement elem)
        {
            float scale = ParseF(Attr(elem, "scale"), 1f);
            if (!Mathf.Approximately(scale, 1f)) rt.localScale = new Vector3(scale, scale, 1f);

            float alpha = ParseF(Attr(elem, "alpha"), 1f);
            if (alpha < 0.999f)
            {
                var cg = go.GetComponent<CanvasGroup>();
                if (cg == null) cg = go.AddComponent<CanvasGroup>();
                cg.alpha = Mathf.Clamp01(alpha);
            }

            string pass = Attr(elem, "mousePassthrough");
            string hitC = Attr(elem, "hitByChildren");
            bool passthrough = pass == "true" || pass == "1";
            bool hitByChildren = hitC == "true" || hitC == "1";
            if (passthrough || hitByChildren)
            {
                var g = go.GetComponent<Graphic>();
                if (g != null) g.raycastTarget = false;
            }
        }

        // 锚点关键字 → (anchorMin, anchorMax, pivot)。stretch 由 min≠max 隐式表达。
        static void ResolveAnchor(string key, out Vector2 min, out Vector2 max, out Vector2 pivot)
        {
            switch ((key ?? "center").Trim().ToLowerInvariant())
            {
                case "top-left":      min = new Vector2(0, 1); max = new Vector2(0, 1); pivot = new Vector2(0, 1); return;
                case "top":
                case "top-center":    min = new Vector2(0.5f, 1); max = new Vector2(0.5f, 1); pivot = new Vector2(0.5f, 1); return;
                case "top-right":     min = new Vector2(1, 1); max = new Vector2(1, 1); pivot = new Vector2(1, 1); return;
                case "left":
                case "middle-left":
                case "center-left":   min = new Vector2(0, 0.5f); max = new Vector2(0, 0.5f); pivot = new Vector2(0, 0.5f); return;
                case "right":
                case "middle-right":
                case "center-right":  min = new Vector2(1, 0.5f); max = new Vector2(1, 0.5f); pivot = new Vector2(1, 0.5f); return;
                case "bottom-left":   min = new Vector2(0, 0); max = new Vector2(0, 0); pivot = new Vector2(0, 0); return;
                case "bottom":
                case "bottom-center": min = new Vector2(0.5f, 0); max = new Vector2(0.5f, 0); pivot = new Vector2(0.5f, 0); return;
                case "bottom-right":  min = new Vector2(1, 0); max = new Vector2(1, 0); pivot = new Vector2(1, 0); return;

                case "fill":
                case "stretch":
                case "stretch-full":  min = new Vector2(0, 0); max = new Vector2(1, 1); pivot = new Vector2(0.5f, 0.5f); return;
                case "stretch-top":   min = new Vector2(0, 1); max = new Vector2(1, 1); pivot = new Vector2(0.5f, 1); return;
                case "stretch-bottom":min = new Vector2(0, 0); max = new Vector2(1, 0); pivot = new Vector2(0.5f, 0); return;
                case "stretch-left":  min = new Vector2(0, 0); max = new Vector2(0, 1); pivot = new Vector2(0, 0.5f); return;
                case "stretch-right": min = new Vector2(1, 0); max = new Vector2(1, 1); pivot = new Vector2(1, 0.5f); return;
                case "stretch-h":
                case "stretch-horizontal": min = new Vector2(0, 0.5f); max = new Vector2(1, 0.5f); pivot = new Vector2(0.5f, 0.5f); return;
                case "stretch-v":
                case "stretch-vertical":   min = new Vector2(0.5f, 0); max = new Vector2(0.5f, 1); pivot = new Vector2(0.5f, 0.5f); return;

                case "center":
                case "middle":
                default:              min = new Vector2(0.5f, 0.5f); max = new Vector2(0.5f, 0.5f); pivot = new Vector2(0.5f, 0.5f); return;
            }
        }

        static TextAnchor ParseAlign(string key)
        {
            switch ((key ?? "center").Trim().ToLowerInvariant())
            {
                case "left":         return TextAnchor.MiddleLeft;
                case "right":        return TextAnchor.MiddleRight;
                case "top":
                case "top-center":   return TextAnchor.UpperCenter;
                case "bottom":
                case "bottom-center":return TextAnchor.LowerCenter;
                case "top-left":     return TextAnchor.UpperLeft;
                case "top-right":    return TextAnchor.UpperRight;
                case "bottom-left":  return TextAnchor.LowerLeft;
                case "bottom-right": return TextAnchor.LowerRight;
                case "center":
                case "middle":
                default:             return TextAnchor.MiddleCenter;
            }
        }

        // ============ 面板脚本钩子 ============

        void FirePanelScripts(XElement root, GameObject panel)
        {
            var scripts = root.Element("Scripts");
            if (scripts == null) return;
            foreach (var s in scripts.Elements("Script"))
            {
                string ev = Attr(s, "event");
                string fn = Attr(s, "fn");
                if (string.IsNullOrEmpty(fn)) continue;
                if (string.Equals(ev, "OnLoad", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (_host != null) _host.OnPanelLoaded(fn, panel);
                }
            }
        }

        // ============ 解析辅助 ============

        static string Attr(XElement e, string name)
        {
            var a = e.Attribute(name);
            return a != null ? a.Value : null;
        }

        // 实例方法（需访问命名色表）。非 # 开头先查主题色表，未命中再交 ColorUtility。
        bool TryParseColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length > 0 && s[0] != '#' && _namedColors.TryGetValue(s, out c)) return true;
            // 支持 #RGB / #RRGGBB / #RRGGBBAA / CSS 颜色名（ColorUtility 内置）
            return ColorUtility.TryParseHtmlString(s, out c);
        }

        static Vector2 ParseVec2(string s, Vector2 def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            var parts = s.Split(',');
            if (parts.Length < 2) return def;
            float x, y;
            if (TryF(parts[0], out x) && TryF(parts[1], out y)) return new Vector2(x, y);
            return def;
        }

        static Vector4 ParseVec4(string s, Vector4 def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            var parts = s.Split(',');
            if (parts.Length < 4) return def;
            float a, b, cc, d;
            if (TryF(parts[0], out a) && TryF(parts[1], out b) && TryF(parts[2], out cc) && TryF(parts[3], out d))
                return new Vector4(a, b, cc, d);
            return def;
        }

        static bool TryF(string s, out float v)
        {
            if (string.IsNullOrEmpty(s)) { v = 0f; return false; }
            return float.TryParse(s.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out v);
        }

        static float ParseF(string s, float def)
        {
            float v; return TryF(s, out v) ? v : def;
        }

        // 纯白兜底图（缺 sprite 时用，靠 color 显示为纯色块）。全局共享一份。
        static Sprite _whiteSprite;
        public static Sprite WhiteSprite()
        {
            if (_whiteSprite != null) return _whiteSprite;
            var tex = Texture2D.whiteTexture;
            _whiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f);
            return _whiteSprite;
        }
    }
}
