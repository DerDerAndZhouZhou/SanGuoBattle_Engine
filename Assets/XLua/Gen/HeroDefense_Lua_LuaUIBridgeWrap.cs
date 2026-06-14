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
    public class HeroDefenseLuaLuaUIBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(HeroDefense.Lua.LuaUIBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 12, 0, 0);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "SetText", _m_SetText_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetElementActive", _m_SetElementActive_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetButton", _m_GetButton_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetText", _m_GetText_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetImage", _m_GetImage_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "GetSlider", _m_GetSlider_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AddButtonListener", _m_AddButtonListener_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "AddSliderListener", _m_AddSliderListener_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RemoveAllButtonListeners", _m_RemoveAllButtonListeners_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetSliderValue", _m_SetSliderValue_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "SetImageFillAmount", _m_SetImageFillAmount_xlua_st_);
            
			
            
			
			
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "HeroDefense.Lua.LuaUIBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetText_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _elementName = LuaAPI.lua_tostring(L, 2);
                    string _content = LuaAPI.lua_tostring(L, 3);
                    
                    HeroDefense.Lua.LuaUIBridge.SetText( _panelName, _elementName, _content );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetElementActive_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _elementName = LuaAPI.lua_tostring(L, 2);
                    bool _active = LuaAPI.lua_toboolean(L, 3);
                    
                    HeroDefense.Lua.LuaUIBridge.SetElementActive( _panelName, _elementName, _active );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetButton_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _buttonName = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Lua.LuaUIBridge.GetButton( _panelName, _buttonName );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetText_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _textName = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Lua.LuaUIBridge.GetText( _panelName, _textName );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetImage_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _imageName = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Lua.LuaUIBridge.GetImage( _panelName, _imageName );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_GetSlider_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _sliderName = LuaAPI.lua_tostring(L, 2);
                    
                        var gen_ret = HeroDefense.Lua.LuaUIBridge.GetSlider( _panelName, _sliderName );
                        translator.Push(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AddButtonListener_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _buttonName = LuaAPI.lua_tostring(L, 2);
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 3, typeof(XLua.LuaFunction));
                    
                    HeroDefense.Lua.LuaUIBridge.AddButtonListener( _panelName, _buttonName, _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_AddSliderListener_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _sliderName = LuaAPI.lua_tostring(L, 2);
                    XLua.LuaFunction _callback = (XLua.LuaFunction)translator.GetObject(L, 3, typeof(XLua.LuaFunction));
                    
                    HeroDefense.Lua.LuaUIBridge.AddSliderListener( _panelName, _sliderName, _callback );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RemoveAllButtonListeners_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _buttonName = LuaAPI.lua_tostring(L, 2);
                    
                    HeroDefense.Lua.LuaUIBridge.RemoveAllButtonListeners( _panelName, _buttonName );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetSliderValue_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _sliderName = LuaAPI.lua_tostring(L, 2);
                    float _value = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    HeroDefense.Lua.LuaUIBridge.SetSliderValue( _panelName, _sliderName, _value );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_SetImageFillAmount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _panelName = LuaAPI.lua_tostring(L, 1);
                    string _imageName = LuaAPI.lua_tostring(L, 2);
                    float _amount = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    HeroDefense.Lua.LuaUIBridge.SetImageFillAmount( _panelName, _imageName, _amount );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        
        
		
		
		
		
    }
}
