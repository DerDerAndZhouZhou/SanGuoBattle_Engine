using System.Runtime.InteropServices;
using UnityEngine;
using HeroDefense.Config;

namespace HeroDefense.SDK
{
    /// <summary>
    /// 触觉震动桥（抖音 / 微信 / 默认）。
    ///
    /// 表现层底层服务（CLAUDE.md §1.1）：
    ///   - 不写"何时震"业务，仅按 ms 触发一次振动
    ///   - 业务 Lua 通过 LuaHost 暴露的 Device_Vibrate(ms) 调用
    ///
    /// 平台选择：
    ///   current_platform (GameConfig.txt) = douyin | wechat
    ///   - douyin:   tt.vibrateShort({ type: "light"|"medium"|"heavy" }) — JS 互操作
    ///   - wechat:   wx.vibrateShort({ type: "light"|"medium"|"heavy" }) — JS 互操作
    ///   - iOS Safari WebGL: 多数无 navigator.vibrate → no-op
    ///   - Editor / Standalone: log + no-op
    ///
    /// JS 互操作约定：HapticBridge_Vibrate(int ms) 在 Plugins/WebGL/HapticBridge.jslib 实现（Phase 1 占位，Phase 2 真接 SDK）。
    /// 编译期通过 #if UNITY_WEBGL && !UNITY_EDITOR 隔离。
    /// </summary>
    public static class HapticBridge
    {
        private static bool _cfgLoaded;
        private static string _platform = "douyin";

        private static void EnsureConfig()
        {
            if (_cfgLoaded) return;
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", "current_platform");
                if (row != null) _platform = cm.GetValue<string>(row, "value", "douyin");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HapticBridge] 读 GameConfig 失败，沿用默认 douyin: {e.Message}");
            }
            _cfgLoaded = true;
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        // jslib 提供：function HapticBridge_Vibrate(ms) { ... }
        // 实际实现需 Plugins/WebGL/HapticBridge.jslib（Phase 1 占位文件由 Agent A 准备目录）
        [DllImport("__Internal")]
        private static extern void HapticBridge_Vibrate(int ms);
#endif

        /// <summary>
        /// 触发一次短振动（ms 由业务决定，通常 5/10/15/25 四档）。
        /// 编辑器 / iOS / 不支持平台 = no-op + log。
        /// </summary>
        public static void Vibrate(int ms)
        {
            if (ms <= 0) return;
            EnsureConfig();

#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                HapticBridge_Vibrate(ms);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[HapticBridge] jslib 调用失败 ms={ms}: {e.Message}");
            }
#else
            // Editor / Standalone：no-op + 静默 log（避免刷屏，10% 概率抽样）
            if (Random.value < 0.1f)
            {
                Debug.Log($"[HapticBridge] Vibrate({ms}ms) — Editor no-op (platform={_platform})");
            }
#endif
        }
    }
}
