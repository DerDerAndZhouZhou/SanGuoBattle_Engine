using HeroDefense.Engine.Host;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HeroDefense.UI
{
    /// <summary>
    /// 通用 UI 按下/松开/取消桥。组件只转发原始输入事件，
    /// 持续时长与业务阈值由 Lua 使用 unscaled 固定帧累计。
    /// </summary>
    public sealed class UIPressBridge : MonoBehaviour,
        IPointerDownHandler,
        IPointerUpHandler,
        IPointerExitHandler,
        ICancelHandler
    {
        private string _downFunction = "";
        private string _upFunction = "";
        private string _cancelFunction = "";
        private bool _pressed;
        private int _pointerId = int.MinValue;

        public void SetCallbacks(
            string downFunction,
            string upFunction,
            string cancelFunction)
        {
            CancelPress();
            _downFunction = downFunction ?? "";
            _upFunction = upFunction ?? "";
            _cancelFunction = cancelFunction ?? "";
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData == null || _pressed) return;
            _pressed = true;
            _pointerId = eventData.pointerId;
            Call(_downFunction);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_pressed
                    || eventData == null
                    || eventData.pointerId != _pointerId)
                return;
            _pressed = false;
            _pointerId = int.MinValue;
            Call(_upFunction);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_pressed
                    || eventData == null
                    || eventData.pointerId != _pointerId)
                return;
            CancelPress();
        }

        public void OnCancel(BaseEventData eventData)
        {
            CancelPress();
        }

        private void OnDisable()
        {
            CancelPress();
        }

        private void CancelPress()
        {
            if (!_pressed) return;
            _pressed = false;
            _pointerId = int.MinValue;
            Call(_cancelFunction);
        }

        private void Call(string functionName)
        {
            if (string.IsNullOrWhiteSpace(functionName)) return;
            LuaHost.CallGlobal(functionName, gameObject);
        }
    }
}
