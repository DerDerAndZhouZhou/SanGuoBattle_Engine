using UnityEngine;
using UnityEngine.UI;
using HeroDefense.UI;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Lua
{
    /// <summary>
    /// UI 桥接 - 让 Lua 直接操作 UI 元素。
    /// 对 UIBinder 的封装，提供 Lua 友好的静态 API。
    /// Lua 用法:
    ///   local uiBridge = CS.HeroDefense.Lua.LuaUIBridge
    ///   uiBridge.SetText("Panel_HUD", "CoinText", "1000")
    ///   uiBridge.AddButtonListener("Panel_HUD", "PauseBtn", luaFunction)
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class LuaUIBridge
    {
        public static void SetText(string panelName, string elementName, string content)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            binder?.SetText(elementName, content);
        }

        public static void SetElementActive(string panelName, string elementName, bool active)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            binder?.SetActive(elementName, active);
        }

        public static Button GetButton(string panelName, string buttonName)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            return binder?.GetButton(buttonName);
        }

        public static Text GetText(string panelName, string textName)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            return binder?.GetText(textName);
        }

        public static Image GetImage(string panelName, string imageName)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            return binder?.GetImage(imageName);
        }

        public static Slider GetSlider(string panelName, string sliderName)
        {
            var binder = UIManager.Instance?.GetBinder(panelName);
            return binder?.GetSlider(sliderName);
        }

#if XLUA
        public static void AddButtonListener(string panelName, string buttonName, LuaFunction callback)
        {
            var btn = GetButton(panelName, buttonName);
            if (btn != null && callback != null)
            {
                btn.onClick.AddListener(() =>
                {
                    try
                    {
                        callback.Call();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[LuaUIBridge] 按钮回调失败 {panelName}/{buttonName}: {e.Message}");
                    }
                });
            }
        }

        public static void AddSliderListener(string panelName, string sliderName, LuaFunction callback)
        {
            var slider = GetSlider(panelName, sliderName);
            if (slider != null && callback != null)
            {
                slider.onValueChanged.AddListener((value) =>
                {
                    try
                    {
                        callback.Call(value);
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[LuaUIBridge] Slider回调失败 {panelName}/{sliderName}: {e.Message}");
                    }
                });
            }
        }
#endif

        public static void RemoveAllButtonListeners(string panelName, string buttonName)
        {
            var btn = GetButton(panelName, buttonName);
            btn?.onClick.RemoveAllListeners();
        }

        public static void SetSliderValue(string panelName, string sliderName, float value)
        {
            var slider = GetSlider(panelName, sliderName);
            if (slider != null) slider.value = value;
        }

        public static void SetImageFillAmount(string panelName, string imageName, float amount)
        {
            var img = GetImage(panelName, imageName);
            if (img != null) img.fillAmount = amount;
        }
    }
}
