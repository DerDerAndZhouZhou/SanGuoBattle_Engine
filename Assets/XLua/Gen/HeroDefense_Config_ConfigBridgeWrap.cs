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
    public class HeroDefenseConfigConfigBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(HeroDefense.Config.ConfigBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 18, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "LoadAll", _m_LoadAll_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Reload", _m_Reload_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetTableList", _m_GetTableList_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetTableInfo", _m_GetTableInfo_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetTableInfoList", _m_GetTableInfoList_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetTableInfoMulti", _m_GetTableInfoMulti_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetTableInfoListMulti", _m_GetTableInfoListMulti_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetEnumValue", _m_GetEnumValue_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetEnumName", _m_GetEnumName_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetRowCount", _m_GetRowCount_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldRaw", _m_GetFieldRaw_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldInt", _m_GetFieldInt_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldFloat", _m_GetFieldFloat_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldString", _m_GetFieldString_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldBool", _m_GetFieldBool_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldIntArray", _m_GetFieldIntArray_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetFieldTable", _m_GetFieldTable_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "HeroDefense.Config.ConfigBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_LoadAll_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    HeroDefense.Config.ConfigBridge.LoadAll(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Reload_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    
                    HeroDefense.Config.ConfigBridge.Reload( _tableName );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetTableList_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetTableList( _tableName );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetTableInfo_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    string _field = LuaAPI.lua_tostring(L, 2);
                    string _value = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetTableInfo( _tableName, _field, _value );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetTableInfoList_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    string _field = LuaAPI.lua_tostring(L, 2);
                    string _value = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetTableInfoList( _tableName, _field, _value );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetTableInfoMulti_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    System.Collections.Generic.Dictionary<string, object> _conditions = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 2, typeof(System.Collections.Generic.Dictionary<string, object>));
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetTableInfoMulti( _tableName, _conditions );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetTableInfoListMulti_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    System.Collections.Generic.Dictionary<string, object> _conditions = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 2, typeof(System.Collections.Generic.Dictionary<string, object>));
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetTableInfoListMulti( _tableName, _conditions );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetEnumValue_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _enumType = LuaAPI.lua_tostring(L, 1);
                    string _enumName = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetEnumValue( _enumType, _enumName );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetEnumName_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _enumType = LuaAPI.lua_tostring(L, 1);
                    int _enumValue = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetEnumName( _enumType, _enumValue );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetRowCount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _tableName = LuaAPI.lua_tostring(L, 1);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetRowCount( _tableName );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldRaw_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldRaw( _row, _field );
                        translator.PushAny(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldInt_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    int _defaultValue = LuaAPI.xlua_tointeger(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldInt( _row, _field, _defaultValue );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 2&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldInt( _row, _field );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Config.ConfigBridge.GetFieldInt!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldFloat_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    float _defaultValue = (float)LuaAPI.lua_tonumber(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldFloat( _row, _field, _defaultValue );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 2&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldFloat( _row, _field );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Config.ConfigBridge.GetFieldFloat!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldString_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& (LuaAPI.lua_isnil(L, 3) || LuaAPI.lua_type(L, 3) == LuaTypes.LUA_TSTRING)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    string _defaultValue = LuaAPI.lua_tostring(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldString( _row, _field, _defaultValue );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 2&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldString( _row, _field );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Config.ConfigBridge.GetFieldString!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldBool_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 3&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TBOOLEAN == LuaAPI.lua_type(L, 3)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    bool _defaultValue = LuaAPI.lua_toboolean(L, 3);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldBool( _row, _field, _defaultValue );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                if(gen_param_count == 2&& translator.Assignable<System.Collections.Generic.Dictionary<string, object>>(L, 1)&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldBool( _row, _field );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Config.ConfigBridge.GetFieldBool!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldIntArray_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldIntArray( _row, _field );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetFieldTable_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    System.Collections.Generic.Dictionary<string, object> _row = (System.Collections.Generic.Dictionary<string, object>)translator.GetObject(L, 1, typeof(System.Collections.Generic.Dictionary<string, object>));
                    string _field = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Config.ConfigBridge.GetFieldTable( _row, _field );
                        translator.PushAny(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
