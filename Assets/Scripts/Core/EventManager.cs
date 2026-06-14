using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroDefense.Core
{
    /// <summary>
    /// 事件管理器 - 单例观察者模式，供 C#/Lua 系统间解耦通信。
    /// </summary>
    public class EventManager : MonoBehaviour
    {
        #region 单例
        private static EventManager _instance;
        private static bool _isQuitting = false;

        public static EventManager Instance
        {
            get
            {
                if (_isQuitting) return null;
                if (_instance == null)
                {
                    GameObject go = new GameObject("[EventManager]");
                    _instance = go.AddComponent<EventManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }
        #endregion

        #region 事件委托定义
        public delegate void EventCallback();
        public delegate void EventCallbackWithParams(params object[] args);
        #endregion

        #region 私有字段
        private Dictionary<string, List<EventCallbackWithParams>> _eventListeners =
            new Dictionary<string, List<EventCallbackWithParams>>();

        private Dictionary<string, List<EventCallback>> _simpleEventListeners =
            new Dictionary<string, List<EventCallback>>();
        #endregion

        #region 生命周期
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

        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _isQuitting = true;
                _instance = null;
                _eventListeners.Clear();
                _simpleEventListeners.Clear();
            }
        }
        #endregion

        #region 注册/注销事件（带参数版）
        public void RegisterEvent(string eventName, EventCallbackWithParams callback)
        {
            if (!_eventListeners.ContainsKey(eventName))
            {
                _eventListeners[eventName] = new List<EventCallbackWithParams>();
            }

            if (!_eventListeners[eventName].Contains(callback))
            {
                _eventListeners[eventName].Add(callback);
            }
        }

        public void UnregisterEvent(string eventName, EventCallbackWithParams callback)
        {
            if (_eventListeners.ContainsKey(eventName))
            {
                _eventListeners[eventName].Remove(callback);
                if (_eventListeners[eventName].Count == 0)
                {
                    _eventListeners.Remove(eventName);
                }
            }
        }

        public void TriggerEvent(string eventName, params object[] args)
        {
            if (_eventListeners.ContainsKey(eventName))
            {
                var listeners = new List<EventCallbackWithParams>(_eventListeners[eventName]);
                foreach (var callback in listeners)
                {
                    try
                    {
                        callback?.Invoke(args);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[EventManager] 事件 '{eventName}' 回调执行异常: {e.Message}\n{e.StackTrace}");
                    }
                }
            }

            if (_simpleEventListeners.ContainsKey(eventName))
            {
                var listeners = new List<EventCallback>(_simpleEventListeners[eventName]);
                foreach (var callback in listeners)
                {
                    try
                    {
                        callback?.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[EventManager] 事件 '{eventName}' 简单回调执行异常: {e.Message}");
                    }
                }
            }
        }
        #endregion

        #region 注册/注销事件（无参数版）
        public void RegisterEvent(string eventName, EventCallback callback)
        {
            if (!_simpleEventListeners.ContainsKey(eventName))
            {
                _simpleEventListeners[eventName] = new List<EventCallback>();
            }

            if (!_simpleEventListeners[eventName].Contains(callback))
            {
                _simpleEventListeners[eventName].Add(callback);
            }
        }

        public void UnregisterEvent(string eventName, EventCallback callback)
        {
            if (_simpleEventListeners.ContainsKey(eventName))
            {
                _simpleEventListeners[eventName].Remove(callback);
                if (_simpleEventListeners[eventName].Count == 0)
                {
                    _simpleEventListeners.Remove(eventName);
                }
            }
        }
        #endregion

        #region 工具方法
        public void ClearEvent(string eventName)
        {
            _eventListeners.Remove(eventName);
            _simpleEventListeners.Remove(eventName);
        }

        public void ClearAllEvents()
        {
            _eventListeners.Clear();
            _simpleEventListeners.Clear();
            Debug.Log("[EventManager] 所有事件监听已清除");
        }
        #endregion
    }
}
