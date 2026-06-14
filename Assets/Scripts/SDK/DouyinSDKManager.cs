using System;
using UnityEngine;
using HeroDefense.Utils;

namespace HeroDefense.SDK
{
    /// <summary>
    /// 抖音小游戏 SDK 管理器 - 封装 tt.xxx 平台 API。
    /// 条件编译：仅在 DOUYIN_MINIGAME + UNITY_WEBGL 下启用真实 SDK 调用。
    /// </summary>
    public class DouyinSDKManager : SingletonMono<DouyinSDKManager>
    {
        public bool IsDouyinPlatform
        {
            get
            {
#if UNITY_WEBGL && DOUYIN_MINIGAME
                return true;
#else
                return false;
#endif
            }
        }

        private bool _isLoggedIn = false;
        public bool IsLoggedIn => _isLoggedIn;

        private string _userId = "";
        private string _userName = "";
        public string UserId => _userId;
        public string UserName => _userName;

        protected override void OnSingletonInit()
        {
            Debug.Log("[DouyinSDK] 初始化");
        }

        public void Login(Action<bool, string> callback)
        {
#if UNITY_WEBGL && DOUYIN_MINIGAME
            // TODO: 调用 tt.login() 获取 code，再到服务端换 openId/session_key
            Debug.Log("[DouyinSDK] 调用 tt.login()");
#else
            _isLoggedIn = true;
            _userId = "editor_test_user";
            _userName = "测试用户";
            Debug.Log("[DouyinSDK] 编辑器模拟登录成功");
            callback?.Invoke(true, "模拟登录成功");
#endif
        }

        public void Share(string title, string imageUrl, Action<bool> callback)
        {
#if UNITY_WEBGL && DOUYIN_MINIGAME
            // TODO: tt.shareAppMessage()
            Debug.Log($"[DouyinSDK] 分享: {title}");
#else
            Debug.Log($"[DouyinSDK] 编辑器模拟分享: {title}");
            callback?.Invoke(true);
#endif
        }

        public void ReportEvent(string eventName, string jsonData)
        {
#if UNITY_WEBGL && DOUYIN_MINIGAME
            // TODO: tt.reportAnalytics()
#endif
            Debug.Log($"[DouyinSDK] 上报事件: {eventName} data={jsonData}");
        }

        public void VibrateShort()
        {
#if UNITY_WEBGL && DOUYIN_MINIGAME
            // TODO: tt.vibrateShort()
#endif
        }
    }
}
