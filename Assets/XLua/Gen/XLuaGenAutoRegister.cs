#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using System;
using System.Collections.Generic;
using System.Reflection;


namespace XLua.CSObjectWrap
{
    public class XLua_Gen_Initer_Register__
	{
        
        
        static void wrapInit0(LuaEnv luaenv, ObjectTranslator translator)
        {
        
            translator.DelayWrapLoader(typeof(HeroDefense.SDK.AdBridge), HeroDefenseSDKAdBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Lua.LuaBridge), HeroDefenseLuaLuaBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Lua.LuaEventBridge), HeroDefenseLuaLuaEventBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Lua.LuaUIBridge), HeroDefenseLuaLuaUIBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Core.SaveBridge), HeroDefenseCoreSaveBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Config.ConfigBridge), HeroDefenseConfigConfigBridgeWrap.__Register);
        
        
            translator.DelayWrapLoader(typeof(HeroDefense.Battle.BattleBridge), HeroDefenseBattleBattleBridgeWrap.__Register);
        
        
        
        }
        
        static void Init(LuaEnv luaenv, ObjectTranslator translator)
        {
            
            wrapInit0(luaenv, translator);
            
            
        }
        
	    static XLua_Gen_Initer_Register__()
        {
		    XLua.LuaEnv.AddIniter(Init);
		}
		
		
	}
	
}
namespace XLua
{
	public partial class ObjectTranslator
	{
		static XLua.CSObjectWrap.XLua_Gen_Initer_Register__ s_gen_reg_dumb_obj = new XLua.CSObjectWrap.XLua_Gen_Initer_Register__();
		static XLua.CSObjectWrap.XLua_Gen_Initer_Register__ gen_reg_dumb_obj {get{return s_gen_reg_dumb_obj;}}
	}
	
	internal partial class InternalGlobals
    {
	    
	    static InternalGlobals()
		{
		    extensionMethodMap = new Dictionary<Type, IEnumerable<MethodInfo>>()
			{
			    
			};
			
			genTryArrayGetPtr = StaticLuaCallbacks.__tryArrayGet;
            genTryArraySetPtr = StaticLuaCallbacks.__tryArraySet;
		}
	}
}
