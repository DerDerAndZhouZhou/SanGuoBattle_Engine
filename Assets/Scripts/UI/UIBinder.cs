using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace HeroDefense.UI
{
    /// <summary>
    /// UI 绑定器 - 自动遍历面板层级，将命名的 UI 元素收集到字典。
    /// Lua 层通过字典名获取 UI 组件引用，避免硬编码路径。
    /// </summary>
    public class UIBinder : MonoBehaviour
    {
        private Dictionary<string, GameObject> _elements = new Dictionary<string, GameObject>();
        private Dictionary<string, Button> _buttons = new Dictionary<string, Button>();
        private Dictionary<string, Text> _texts = new Dictionary<string, Text>();
        private Dictionary<string, Image> _images = new Dictionary<string, Image>();
        private Dictionary<string, Slider> _sliders = new Dictionary<string, Slider>();
        private Dictionary<string, Toggle> _toggles = new Dictionary<string, Toggle>();

        private bool _isBound = false;

        public void Bind()
        {
            if (_isBound) return;

            _elements.Clear();
            _buttons.Clear();
            _texts.Clear();
            _images.Clear();
            _sliders.Clear();
            _toggles.Clear();

            CollectElements(transform);
            _isBound = true;
        }

        private void CollectElements(Transform parent)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                string name = child.name;

                if (!_elements.ContainsKey(name))
                {
                    _elements[name] = child.gameObject;
                }

                var btn = child.GetComponent<Button>();
                if (btn != null && !_buttons.ContainsKey(name))
                    _buttons[name] = btn;

                var txt = child.GetComponent<Text>();
                if (txt != null && !_texts.ContainsKey(name))
                    _texts[name] = txt;

                var img = child.GetComponent<Image>();
                if (img != null && !_images.ContainsKey(name))
                    _images[name] = img;

                var slider = child.GetComponent<Slider>();
                if (slider != null && !_sliders.ContainsKey(name))
                    _sliders[name] = slider;

                var toggle = child.GetComponent<Toggle>();
                if (toggle != null && !_toggles.ContainsKey(name))
                    _toggles[name] = toggle;

                if (child.childCount > 0)
                {
                    CollectElements(child);
                }
            }
        }

        // === 公开查询接口（供 Lua 通过 Bridge 调用） ===

        public GameObject GetElement(string name)
        {
            _elements.TryGetValue(name, out var go);
            return go;
        }

        public Button GetButton(string name)
        {
            _buttons.TryGetValue(name, out var btn);
            return btn;
        }

        public Text GetText(string name)
        {
            _texts.TryGetValue(name, out var txt);
            return txt;
        }

        public Image GetImage(string name)
        {
            _images.TryGetValue(name, out var img);
            return img;
        }

        public Slider GetSlider(string name)
        {
            _sliders.TryGetValue(name, out var slider);
            return slider;
        }

        public Toggle GetToggle(string name)
        {
            _toggles.TryGetValue(name, out var toggle);
            return toggle;
        }

        public void SetText(string name, string content)
        {
            if (_texts.TryGetValue(name, out var txt))
            {
                txt.text = content;
            }
        }

        public void SetActive(string name, bool active)
        {
            if (_elements.TryGetValue(name, out var go))
            {
                go.SetActive(active);
            }
        }

        // ============ 面板层级控制（v3 §5.22 / Step 10：9 个 ui_sort_* 常量配套） ============

        /// <summary>
        /// 按 Tag 找到面板 → 设其 Canvas.overrideSorting + sortingOrder → SetActive(true)。
        /// 面板根上若无 Canvas 组件 → LogWarning（不抛异常）。GameConfig 9 项 `ui_sort_*` 配合使用。
        /// </summary>
        public static void ShowPanel(string tag, int sortingOrder)
        {
            var panel = UIFinder.FindPanelByTag(tag);
            if (panel == null)
            {
                Debug.LogWarning($"[UIBinder] ShowPanel: tag={tag} 未找到");
                return;
            }
            ApplySortingOrder(panel, sortingOrder);
            if (!panel.activeSelf) panel.SetActive(true);
        }

        /// <summary>仅设面板层级（不动 active 状态）。</summary>
        public static void SetPanelOrder(string tag, int sortingOrder)
        {
            var panel = UIFinder.FindPanelByTag(tag);
            if (panel == null)
            {
                Debug.LogWarning($"[UIBinder] SetPanelOrder: tag={tag} 未找到");
                return;
            }
            ApplySortingOrder(panel, sortingOrder);
        }

        private static void ApplySortingOrder(GameObject panel, int sortingOrder)
        {
            var canvas = panel.GetComponent<Canvas>();
            if (canvas == null)
            {
                // 子树上找一个 Canvas（如果是嵌套布局）
                canvas = panel.GetComponentInChildren<Canvas>(true);
            }
            if (canvas == null)
            {
                Debug.LogWarning($"[UIBinder] ApplySortingOrder: panel={panel.name} 上无 Canvas 组件（无法设 sortingOrder）");
                return;
            }
            canvas.overrideSorting = true;
            canvas.sortingOrder = sortingOrder;
        }
    }
}
