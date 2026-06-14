// HapticBridge.jslib — WebGL/小游戏 触觉震动原生实现
// 对应 C# [DllImport("__Internal")] static extern void HapticBridge_Vibrate(int ms)（HapticBridge.cs）。
//
// 背景（2026-06-07）：此文件原为 Phase 1 TODO 占位、一直没建 → 微信 WebGL 包里 extern
// 链接不到实现 → 调用时 Emscripten abort("missing function: HapticBridge_Vibrate")
// → WXUncaughtException → 整个小游戏崩溃（点/拖卡片触发震动时必崩）。本文件补上实现即解。
//
// 多平台兜底：微信 wx.vibrateShort / 抖音 tt.vibrateShort / 浏览器 navigator.vibrate；
// 全部 try-catch 包裹 —— 任何平台不支持都安全 no-op，绝不再 abort。
mergeInto(LibraryManager.library, {
  HapticBridge_Vibrate: function (ms) {
    try {
      // ms→强度档（与 Lua haptic_*_ms 约定：5/10/15/25）
      var type = ms >= 25 ? 'heavy' : (ms >= 15 ? 'medium' : 'light');
      if (typeof wx !== 'undefined' && wx.vibrateShort) {
        wx.vibrateShort({ type: type });
      } else if (typeof tt !== 'undefined' && tt.vibrateShort) {
        tt.vibrateShort({ type: type });
      } else if (typeof navigator !== 'undefined' && navigator.vibrate) {
        navigator.vibrate(ms);
      }
    } catch (e) {
      // 静默吞掉：震动失败绝不能影响游戏运行
    }
  }
});
