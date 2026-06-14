using System;
using UnityEngine;
using HeroDefense.Engine.Host;
using HeroDefense.Utils;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Lua
{
    /// <summary>
    /// Lua 环境管理器 - 薄包装，内部 delegate 到 LuaHost。
    /// 保持向后兼容的 public API（Instance / LuaEnv / Initialize / DoString / CallGlobal / GetGlobal / CheckHotUpdate）。
    /// 实际 Lua 虚拟机生命周期由 LuaHost 管理。
    /// </summary>
    public class LuaManager : SingletonMono<LuaManager>
    {
#if XLUA
        public LuaEnv LuaEnv => LuaHost.Env;
#endif

        public bool IsInitialized => LuaHost.IsInitialized;

        protected override void OnSingletonInit()
        {
            // Lua 路径由 ResourceHost 统一管理，此处不再预设
        }

        /// <summary>初始化 Lua 环境并执行入口脚本。由 BootScene 的初始化流程调用。</summary>
        public void Initialize()
        {
            if (LuaHost.IsInitialized) return;
            LuaHost.Boot();
            Debug.Log("[LuaManager] Lua 环境初始化完成（通过 LuaHost）");
        }

        public object[] DoString(string luaCode)
        {
            return LuaHost.DoString(luaCode);
        }

        public void CallGlobal(string funcName, params object[] args)
        {
            LuaHost.CallGlobal(funcName, args);
        }

        public T GetGlobal<T>(string name)
        {
            return LuaHost.GetGlobal<T>(name);
        }

        private void Update()
        {
            LuaHost.Tick();
            // 业务层 Timer_Update（framework/timer.lua）
            // 业务暂停时传 0 dt，让 wave 倒计时 / 3 选 1 倒计时等所有 Lua 定时器一并冻结
            float dt = HeroDefense.Battle.BattleBridge.BattlePaused ? 0f : Time.deltaTime;
            LuaHost.CallGlobal("Timer_Update", dt);
        }

        private new void OnDestroy()
        {
            LuaHost.Shutdown();
            base.OnDestroy();
            Debug.Log("[LuaManager] Lua 环境已销毁");
        }

        #region 热更新
        /// <summary>
        /// 检查并下载 Lua 热更新（桩实现）。
        /// TODO: 后期接入 HotUpdateHost.CheckAndDownload(cdnBaseUrl, ...) 实现真正的增量下载。
        /// </summary>
        public void CheckHotUpdate(Action<bool> onComplete)
        {
            Debug.Log("[LuaManager] 热更新检查（当前为桩实现）");
            onComplete?.Invoke(false); // false = 无需更新
        }
        #endregion
    }
}
