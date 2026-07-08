#if XLUA
using XLua;
#endif

namespace HeroDefense.Core
{
    /// <summary>
    /// 存档 Lua 桥接（核心循环 P0-2 / P0-3）。
    ///
    /// 与 ConfigBridge 对等的桥层：把 SaveManager 的通用 KV 存取 + 货币入库
    /// 转发给 Lua 全局函数（LuaHost.Boot 注册 Save_GetInt / Save_SetInt / ...）。
    ///
    /// 业务（Lua）只约定「存什么 key」，C# 只提供存取通道。
    /// 一律走字符串存储，int 在桥内自行 Parse / ToString，Lua 端无需自己转。
    ///
    /// AGENTS.md §6：Lua → C# 只走 LuaHost 暴露的全局函数，不裸调 CS.HeroDefense.Core.*。
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class SaveBridge
    {
        // ======== 通用 KV ========

        public static string GetString(string key, string def)
        {
            var sm = SaveManager.Instance;
            return sm == null ? def : sm.GetKV(key, def);
        }

        public static void SetString(string key, string value)
        {
            SaveManager.Instance?.SetKV(key, value);
        }

        public static int GetInt(string key, int def)
        {
            var sm = SaveManager.Instance;
            if (sm == null) return def;
            string raw = sm.GetKV(key, null);
            if (string.IsNullOrEmpty(raw)) return def;
            return int.TryParse(raw, out int v) ? v : def;
        }

        public static void SetInt(string key, int value)
        {
            SaveManager.Instance?.SetKV(key, value.ToString());
        }

        // ======== 货币入库（结算页领取奖励用）========

        public static void AddCoins(int amount)
        {
            SaveManager.Instance?.AddCoins(amount);
        }

        public static void AddGems(int amount)
        {
            SaveManager.Instance?.AddGems(amount);
        }
    }
}

