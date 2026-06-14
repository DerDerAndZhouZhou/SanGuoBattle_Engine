using UnityEngine;
#if XLUA
using XLua;
#endif

namespace HeroDefense.SDK
{
    /// <summary>
    /// 广告 Lua 桥接（核心循环 P0-3：结算页广告翻倍 / 失败复活）。
    ///
    /// Lua 调 Ad_Show(placement, onReward)（LuaHost.Boot 注册的全局函数）：
    ///   - placement：广告位字符串（ad_revive / ad_double_reward），当前 AdManager 不分位，仅记日志。
    ///   - onReward：Lua function，看完广告获得奖励时回调。
    ///
    /// 回调形态用 LuaFunction（非 Action）规避 xLua delegate 生成配置坑（CLAUDE.md §6 / §10 / 技术方案 R2）。
    ///
    /// 编辑器 / 无真广告 SDK 环境兜底：AdManager.ShowRewardedAd 在非 DOUYIN_MINIGAME 分支
    /// 已直接 callback(true)，故编辑器下 Ad_Show 天然走成功回调，结算页广告功能可测。
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class AdBridge
    {
#if XLUA
        /// <summary>展示激励视频广告；看完后回调 Lua onReward（中途关闭不回调）。</summary>
        public static void Show(string placement, LuaFunction onReward)
        {
            Debug.Log($"[AdBridge] Ad_Show placement={placement ?? "?"}");

            var mgr = AdManager.Instance;
            if (mgr == null)
            {
                // SDK 未就位兜底：直接发奖（编辑器 / 无真广告环境）
                Debug.LogWarning("[AdBridge] AdManager.Instance 为 null，直接走成功回调（兜底）");
                Invoke(onReward);
                return;
            }

            mgr.ShowRewardedAd(ok =>
            {
                if (ok) Invoke(onReward);
                onReward?.Dispose();
            });
        }

        private static void Invoke(LuaFunction fn)
        {
            if (fn == null) return;
            try { fn.Call(); }
            catch (System.Exception e) { Debug.LogError($"[AdBridge] onReward 回调异常: {e.Message}"); }
        }
#else
        public static void Show(string placement, object onReward)
        {
            Debug.LogWarning("[AdBridge] xLua 未启用，Ad_Show 不可用");
        }
#endif
    }
}
