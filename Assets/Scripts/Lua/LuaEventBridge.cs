using System;
using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Core;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Lua
{
    /// <summary>
    /// 事件系统桥接 - 让 Lua 可以监听/触发 C# 事件系统。
    /// Lua 用法:
    ///   local bridge = CS.HeroDefense.Lua.LuaEventBridge.Instance
    ///   bridge:RegisterLuaCallback("WaveStarted", luaFunction)
    ///   bridge:FireEvent("TowerPlaced", towerId)
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public class LuaEventBridge : MonoBehaviour
    {
        private static LuaEventBridge _instance;
        public static LuaEventBridge Instance => _instance;

#if XLUA
        private Dictionary<string, List<LuaFunction>> _luaCallbacks =
            new Dictionary<string, List<LuaFunction>>();

        private Dictionary<string, EventManager.EventCallbackWithParams> _bridgeCallbacks =
            new Dictionary<string, EventManager.EventCallbackWithParams>();
#endif

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                ClearAll();
                _instance = null;
            }
        }

#if XLUA
        public void RegisterLuaCallback(string eventName, LuaFunction callback)
        {
            if (callback == null) return;

            if (!_luaCallbacks.ContainsKey(eventName))
            {
                _luaCallbacks[eventName] = new List<LuaFunction>();

                // 首次注册某事件时，在 C# 事件系统中也注册一个桥接回调
                EventManager.EventCallbackWithParams bridgeCallback = (args) =>
                {
                    DispatchToLua(eventName, args);
                };
                _bridgeCallbacks[eventName] = bridgeCallback;
                EventManager.Instance?.RegisterEvent(eventName, bridgeCallback);
            }

            _luaCallbacks[eventName].Add(callback);
        }

        public void UnregisterLuaCallback(string eventName, LuaFunction callback)
        {
            if (callback == null) return;

            if (_luaCallbacks.TryGetValue(eventName, out var list))
            {
                list.Remove(callback);
                callback.Dispose();

                if (list.Count == 0)
                {
                    _luaCallbacks.Remove(eventName);
                    if (_bridgeCallbacks.TryGetValue(eventName, out var bridgeCallback))
                    {
                        EventManager.Instance?.UnregisterEvent(eventName, bridgeCallback);
                        _bridgeCallbacks.Remove(eventName);
                    }
                }
            }
        }

        private void DispatchToLua(string eventName, object[] args)
        {
            if (!_luaCallbacks.TryGetValue(eventName, out var list)) return;

            var snapshot = new List<LuaFunction>(list);
            foreach (var luaFunc in snapshot)
            {
                try
                {
                    luaFunc.Call(args);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[LuaEventBridge] Lua回调执行失败 事件={eventName}: {e.Message}");
                }
            }
        }
#endif

        public void FireEvent(string eventName, params object[] args)
        {
            EventManager.Instance?.TriggerEvent(eventName, args);
        }

        public void ClearAll()
        {
#if XLUA
            foreach (var kvp in _luaCallbacks)
            {
                foreach (var func in kvp.Value)
                {
                    func.Dispose();
                }
            }
            _luaCallbacks.Clear();

            foreach (var kvp in _bridgeCallbacks)
            {
                EventManager.Instance?.UnregisterEvent(kvp.Key, kvp.Value);
            }
            _bridgeCallbacks.Clear();
#endif
        }
    }
}
