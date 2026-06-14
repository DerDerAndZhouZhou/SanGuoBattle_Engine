#if USE_UNI_LUA
using LuaAPI = UniLua.Lua;
using RealStatePtr = UniLua.ILuaState;
using LuaCSFunction = UniLua.CSharpFunctionDelegate;
#else
using LuaAPI = XLua.LuaDLL.Lua;
using RealStatePtr = System.IntPtr;
using LuaCSFunction = XLua.LuaDLL.lua_CSFunction;
#endif

using XLua;
using System.Collections.Generic;


namespace XLua.CSObjectWrap
{
    using Utils = XLua.Utils;
    public class HeroDefenseLuaLuaEventBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(HeroDefense.Lua.LuaEventBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 4, 0, 0);
			
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "RegisterLuaCallback", _m_RegisterLuaCallback);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "UnregisterLuaCallback", _m_UnregisterLuaCallback);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "FireEvent", _m_FireEvent);
			Utils.RegisterFunc(L, Utils.METHOD_IDX, "ClearAll", _m_ClearAll);
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 1, 1, 0);
			
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "Instance", _g_get_Instance);
            
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            
			try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
				if(LuaAPI.lua_gettop(L) == 1)
				{
					
					var gen_ret = new HeroDefense.Lua.LuaEventBridge();
					translator.Push(L, gen_ret);
                    
					return 1;
				}
				
			}
			catch(System.Exception gen_e) {
				return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
			}
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Lua.LuaEventBridge constructor!");
            
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RegisterLuaCallback(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                HeroDefense.Lua.LuaEventBridge gen_to_be_invoked = (HeroDefense.Lua.LuaEventBridge)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _eventName = LuaAPI.lua_tostring(L, 2);
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 3, typeof(XLua.LuaFunction));
                    
                    gen_to_be_invoked.RegisterLuaCallback( _eventName, _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_UnregisterLuaCallback(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                HeroDefense.Lua.LuaEventBridge gen_to_be_invoked = (HeroDefense.Lua.LuaEventBridge)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _eventName = LuaAPI.lua_tostring(L, 2);
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 3, typeof(XLua.LuaFunction));
                    
                    gen_to_be_invoked.UnregisterLuaCallback( _eventName, _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FireEvent(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                HeroDefense.Lua.LuaEventBridge gen_to_be_invoked = (HeroDefense.Lua.LuaEventBridge)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    string _eventName = LuaAPI.lua_tostring(L, 2);
                    object[] _args = translator.GetParams<object>(L, 3);
                    
                    gen_to_be_invoked.FireEvent( _eventName, _args );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_ClearAll(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
                HeroDefense.Lua.LuaEventBridge gen_to_be_invoked = (HeroDefense.Lua.LuaEventBridge)translator.FastGetCSObj(L, 1);
            
            
                
                {
                    
                    gen_to_be_invoked.ClearAll(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_Instance(RealStatePtr L)
        {
		    try {
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			    translator.Push(L, HeroDefense.Lua.LuaEventBridge.Instance);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
		
		
		
		
    }
}
