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
    public class HeroDefenseBattleBattleBridgeWrap 
    {
        public static void __Register(RealStatePtr L)
        {
			ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
			System.Type type = typeof(HeroDefense.Battle.BattleBridge);
			Utils.BeginObjectRegister(type, L, translator, 0, 0, 0, 0);
			
			
			
			
			
			
			Utils.EndObjectRegister(type, L, translator, null, null,
			    null, null, null);

		    Utils.BeginClassRegister(type, L, __CreateInstance, 59, 1, 1);
			Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetProjectilePoolHits", _m_Battle_GetProjectilePoolHits_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetProjectilePoolMisses", _m_Battle_GetProjectilePoolMisses_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetProjectilePoolFree", _m_Battle_GetProjectilePoolFree_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "RecycleProjectile", _m_RecycleProjectile_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "OnBattleSceneExit", _m_OnBattleSceneExit_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnUnit", _m_Battle_SpawnUnit_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_DestroyUnit", _m_Battle_DestroyUnit_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetSprite", _m_Battle_SetSprite_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_PlayAnim", _m_Battle_PlayAnim_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetWorldPosition", _m_Battle_SetWorldPosition_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetAlpha", _m_Battle_SetAlpha_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetScale", _m_Battle_SetScale_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetUnitFacing", _m_Battle_SetUnitFacing_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetEnemyHsv", _m_Battle_SetEnemyHsv_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnEnemy", _m_Battle_SpawnEnemy_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnEnemyAtRow", _m_Battle_SpawnEnemyAtRow_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetEnemyHpBar", _m_Battle_SetEnemyHpBar_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetUnitHpBarVisible", _m_Battle_SetUnitHpBarVisible_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetEnemySpeed", _m_Battle_SetEnemySpeed_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetEnemyHalted", _m_Battle_SetEnemyHalted_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetEnemyRow", _m_Battle_GetEnemyRow_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetEnemyCol", _m_Battle_GetEnemyCol_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetZones", _m_Battle_SetZones_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsCellInOwnZone", _m_Battle_IsCellInOwnZone_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsCellInPublicZone", _m_Battle_IsCellInPublicZone_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsCellInEnemyZone", _m_Battle_IsCellInEnemyZone_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnProjectile", _m_Battle_SpawnProjectile_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "EnumerateEnemies", _m_EnumerateEnemies_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnProjectileTracking", _m_Battle_SpawnProjectileTracking_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnProjectileToCell", _m_Battle_SpawnProjectileToCell_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SpawnProjectileLine", _m_Battle_SpawnProjectileLine_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetEnemyCell", _m_Battle_SetEnemyCell_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetCellHighlight", _m_Battle_SetCellHighlight_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_CellToWorldX", _m_Battle_CellToWorldX_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_CellToWorldY", _m_Battle_CellToWorldY_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_ScreenToWorldX", _m_Battle_ScreenToWorldX_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_ScreenToWorldY", _m_Battle_ScreenToWorldY_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_ScreenToCellRow", _m_Battle_ScreenToCellRow_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_ScreenToCellCol", _m_Battle_ScreenToCellCol_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsCellInBounds", _m_Battle_IsCellInBounds_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsCellInCamp", _m_Battle_IsCellInCamp_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetGridVisualStyle", _m_Battle_SetGridVisualStyle_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_CalcSortingOrder", _m_Battle_CalcSortingOrder_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_ShowUIGhost", _m_Battle_ShowUIGhost_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_MoveUIGhost", _m_Battle_MoveUIGhost_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_HideUIGhost", _m_Battle_HideUIGhost_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsPointerOverInventory", _m_Battle_IsPointerOverInventory_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_IsPointerOverShop", _m_Battle_IsPointerOverShop_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetInventorySlotAtScreen", _m_Battle_GetInventorySlotAtScreen_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SetTimeScale", _m_Battle_SetTimeScale_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetUnitCount", _m_Battle_GetUnitCount_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetEnemyCount", _m_Battle_GetEnemyCount_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetProjectileCount", _m_Battle_GetProjectileCount_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetCampHp", _m_Battle_GetCampHp_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_GetCampMaxHp", _m_Battle_GetCampMaxHp_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FormatDamageRowName", _m_FormatDamageRowName_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "Battle_SkillCardCast", _m_Battle_SkillCardCast_xlua_st_);
            Utils.RegisterFunc(L, Utils.CLS_IDX, "FormatDamageRowDamage", _m_FormatDamageRowDamage_xlua_st_);
            
			
            
			Utils.RegisterFunc(L, Utils.CLS_GETTER_IDX, "BattlePaused", _g_get_BattlePaused);
            
			Utils.RegisterFunc(L, Utils.CLS_SETTER_IDX, "BattlePaused", _s_set_BattlePaused);
            
			
			Utils.EndClassRegister(type, L, translator);
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int __CreateInstance(RealStatePtr L)
        {
            return LuaAPI.luaL_error(L, "HeroDefense.Battle.BattleBridge does not have a constructor!");
        }
        
		
        
		
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetProjectilePoolHits_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetProjectilePoolHits(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetProjectilePoolMisses_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetProjectilePoolMisses(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetProjectilePoolFree_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetProjectilePoolFree(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_RecycleProjectile_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    
                    HeroDefense.Battle.BattleBridge.RecycleProjectile( _handle );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_OnBattleSceneExit_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    HeroDefense.Battle.BattleBridge.OnBattleSceneExit(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnUnit_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _npcId = LuaAPI.xlua_tointeger(L, 1);
                    int _row = LuaAPI.xlua_tointeger(L, 2);
                    int _col = LuaAPI.xlua_tointeger(L, 3);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnUnit( _npcId, _row, _col );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_DestroyUnit_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    
                    HeroDefense.Battle.BattleBridge.Battle_DestroyUnit( _handle );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetSprite_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    string _spriteKey = LuaAPI.lua_tostring(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetSprite( _handle, _spriteKey );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_PlayAnim_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
			    int gen_param_count = LuaAPI.lua_gettop(L);
            
                if(gen_param_count == 2&& (LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1) || LuaAPI.lua_isint64(L, 1))&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)) 
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    string _stateName = LuaAPI.lua_tostring(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_PlayAnim( _handle, _stateName );
                    
                    
                    
                    return 0;
                }
                if(gen_param_count == 3&& (LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 1) || LuaAPI.lua_isint64(L, 1))&& (LuaAPI.lua_isnil(L, 2) || LuaAPI.lua_type(L, 2) == LuaTypes.LUA_TSTRING)&& LuaTypes.LUA_TNUMBER == LuaAPI.lua_type(L, 3)) 
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    string _stateName = LuaAPI.lua_tostring(L, 2);
                    float _speedMult = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    HeroDefense.Battle.BattleBridge.Battle_PlayAnim( _handle, _stateName, _speedMult );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
            return LuaAPI.luaL_error(L, "invalid arguments to HeroDefense.Battle.BattleBridge.Battle_PlayAnim!");
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetWorldPosition_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _wx = (float)LuaAPI.lua_tonumber(L, 2);
                    float _wy = (float)LuaAPI.lua_tonumber(L, 3);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetWorldPosition( _handle, _wx, _wy );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetAlpha_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _alpha = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetAlpha( _handle, _alpha );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetScale_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _scale = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetScale( _handle, _scale );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetUnitFacing_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    bool _faceRight = LuaAPI.lua_toboolean(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetUnitFacing( _handle, _faceRight );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetEnemyHsv_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _hueShift = (float)LuaAPI.lua_tonumber(L, 2);
                    float _saturation = (float)LuaAPI.lua_tonumber(L, 3);
                    float _brightness = (float)LuaAPI.lua_tonumber(L, 4);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetEnemyHsv( _handle, _hueShift, _saturation, _brightness );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnEnemy_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _monsterId = LuaAPI.lua_tostring(L, 1);
                    int _laneId = LuaAPI.xlua_tointeger(L, 2);
                    float _spawnX = (float)LuaAPI.lua_tonumber(L, 3);
                    float _spawnY = (float)LuaAPI.lua_tonumber(L, 4);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnEnemy( _monsterId, _laneId, _spawnX, _spawnY );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnEnemyAtRow_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _monsterId = LuaAPI.lua_tostring(L, 1);
                    int _laneId = LuaAPI.xlua_tointeger(L, 2);
                    float _spawnX = (float)LuaAPI.lua_tonumber(L, 3);
                    float _spawnY = (float)LuaAPI.lua_tonumber(L, 4);
                    int _spawnRow = LuaAPI.xlua_tointeger(L, 5);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnEnemyAtRow( _monsterId, _laneId, _spawnX, _spawnY, _spawnRow );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetEnemyHpBar_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _pct = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetEnemyHpBar( _handle, _pct );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetUnitHpBarVisible_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    bool _visible = LuaAPI.lua_toboolean(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetUnitHpBarVisible( _handle, _visible );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetEnemySpeed_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    float _speed = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetEnemySpeed( _handle, _speed );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetEnemyHalted_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    bool _halted = LuaAPI.lua_toboolean(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetEnemyHalted( _handle, _halted );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetEnemyRow_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetEnemyRow( _handle );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetEnemyCol_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetEnemyCol( _handle );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetZones_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _ownCols = LuaAPI.xlua_tointeger(L, 1);
                    int _enemyCols = LuaAPI.xlua_tointeger(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetZones( _ownCols, _enemyCols );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsCellInOwnZone_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsCellInOwnZone( _row, _col );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsCellInPublicZone_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsCellInPublicZone( _row, _col );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsCellInEnemyZone_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsCellInEnemyZone( _row, _col );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnProjectile_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _srcHandle = LuaAPI.lua_toint64(L, 1);
                    long _tgtHandle = LuaAPI.lua_toint64(L, 2);
                    float _damage = (float)LuaAPI.lua_tonumber(L, 3);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnProjectile( _srcHandle, _tgtHandle, _damage );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_EnumerateEnemies_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.EnumerateEnemies(  );
                        translator.PushAny(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnProjectileTracking_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _srcHandle = LuaAPI.lua_toint64(L, 1);
                    long _tgtHandle = LuaAPI.lua_toint64(L, 2);
                    string _projectileKey = LuaAPI.lua_tostring(L, 3);
                    float _speed = (float)LuaAPI.lua_tonumber(L, 4);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnProjectileTracking( _srcHandle, _tgtHandle, _projectileKey, _speed );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnProjectileToCell_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _srcHandle = LuaAPI.lua_toint64(L, 1);
                    int _landingRow = LuaAPI.xlua_tointeger(L, 2);
                    int _landingCol = LuaAPI.xlua_tointeger(L, 3);
                    string _projectileKey = LuaAPI.lua_tostring(L, 4);
                    float _speed = (float)LuaAPI.lua_tonumber(L, 5);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnProjectileToCell( _srcHandle, _landingRow, _landingCol, _projectileKey, _speed );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SpawnProjectileLine_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _srcHandle = LuaAPI.lua_toint64(L, 1);
                    float _dirX = (float)LuaAPI.lua_tonumber(L, 2);
                    float _dirY = (float)LuaAPI.lua_tonumber(L, 3);
                    float _distance = (float)LuaAPI.lua_tonumber(L, 4);
                    float _width = (float)LuaAPI.lua_tonumber(L, 5);
                    string _projectileKey = LuaAPI.lua_tostring(L, 6);
                    float _speed = (float)LuaAPI.lua_tonumber(L, 7);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SpawnProjectileLine( _srcHandle, _dirX, _dirY, _distance, _width, _projectileKey, _speed );
                        LuaAPI.lua_pushint64(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetEnemyCell_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    long _handle = LuaAPI.lua_toint64(L, 1);
                    int _row = LuaAPI.xlua_tointeger(L, 2);
                    int _col = LuaAPI.xlua_tointeger(L, 3);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetEnemyCell( _handle, _row, _col );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetCellHighlight_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    int _stateEnum = LuaAPI.xlua_tointeger(L, 3);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetCellHighlight( _row, _col, _stateEnum );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_CellToWorldX_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_CellToWorldX( _row, _col );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_CellToWorldY_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_CellToWorldY( _row, _col );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_ScreenToWorldX_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_ScreenToWorldX( _sx, _sy );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_ScreenToWorldY_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_ScreenToWorldY( _sx, _sy );
                        LuaAPI.lua_pushnumber(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_ScreenToCellRow_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_ScreenToCellRow( _sx, _sy );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_ScreenToCellCol_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_ScreenToCellCol( _sx, _sy );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsCellInBounds_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsCellInBounds( _row, _col );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsCellInCamp_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _row = LuaAPI.xlua_tointeger(L, 1);
                    int _col = LuaAPI.xlua_tointeger(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsCellInCamp( _row, _col );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetGridVisualStyle_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _style = LuaAPI.lua_tostring(L, 1);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetGridVisualStyle( _style );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_CalcSortingOrder_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _worldY = (float)LuaAPI.lua_tonumber(L, 1);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_CalcSortingOrder( _worldY );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_ShowUIGhost_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    string _spriteKey = LuaAPI.lua_tostring(L, 1);
                    float _sx = (float)LuaAPI.lua_tonumber(L, 2);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 3);
                    float _pivotX = (float)LuaAPI.lua_tonumber(L, 4);
                    float _pivotY = (float)LuaAPI.lua_tonumber(L, 5);
                    
                    HeroDefense.Battle.BattleBridge.Battle_ShowUIGhost( _spriteKey, _sx, _sy, _pivotX, _pivotY );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_MoveUIGhost_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                    HeroDefense.Battle.BattleBridge.Battle_MoveUIGhost( _sx, _sy );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_HideUIGhost_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                    HeroDefense.Battle.BattleBridge.Battle_HideUIGhost(  );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsPointerOverInventory_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsPointerOverInventory( _sx, _sy );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_IsPointerOverShop_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_IsPointerOverShop( _sx, _sy );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetInventorySlotAtScreen_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _sx = (float)LuaAPI.lua_tonumber(L, 1);
                    float _sy = (float)LuaAPI.lua_tonumber(L, 2);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetInventorySlotAtScreen( _sx, _sy );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SetTimeScale_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    float _scale = (float)LuaAPI.lua_tonumber(L, 1);
                    
                    HeroDefense.Battle.BattleBridge.Battle_SetTimeScale( _scale );
                    
                    
                    
                    return 0;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetUnitCount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetUnitCount(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetEnemyCount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetEnemyCount(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetProjectileCount_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetProjectileCount(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetCampHp_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetCampHp(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_GetCampMaxHp_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_GetCampMaxHp(  );
                        LuaAPI.xlua_pushinteger(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FormatDamageRowName_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    object _luaItem = translator.GetObject(L, 1, typeof(object));
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.FormatDamageRowName( _luaItem );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_Battle_SkillCardCast_xlua_st_(RealStatePtr L)
        {
		    try {
            
            
            
                
                {
                    int _skillId = LuaAPI.xlua_tointeger(L, 1);
                    int _targetRow = LuaAPI.xlua_tointeger(L, 2);
                    int _targetCol = LuaAPI.xlua_tointeger(L, 3);
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.Battle_SkillCardCast( _skillId, _targetRow, _targetCol );
                        LuaAPI.lua_pushboolean(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _m_FormatDamageRowDamage_xlua_st_(RealStatePtr L)
        {
		    try {
            
                ObjectTranslator translator = ObjectTranslatorPool.Instance.Find(L);
            
            
            
                
                {
                    object _luaItem = translator.GetObject(L, 1, typeof(object));
                    
                        var gen_ret = HeroDefense.Battle.BattleBridge.FormatDamageRowDamage( _luaItem );
                        LuaAPI.lua_pushstring(L, gen_ret);
                    
                    
                    
                    return 1;
                }
                
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            
        }
        
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _g_get_BattlePaused(RealStatePtr L)
        {
		    try {
            
			    LuaAPI.lua_pushboolean(L, HeroDefense.Battle.BattleBridge.BattlePaused);
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 1;
        }
        
        
        
        [MonoPInvokeCallbackAttribute(typeof(LuaCSFunction))]
        static int _s_set_BattlePaused(RealStatePtr L)
        {
		    try {
                
			    HeroDefense.Battle.BattleBridge.BattlePaused = LuaAPI.lua_toboolean(L, 1);
            
            } catch(System.Exception gen_e) {
                return LuaAPI.luaL_error(L, "c# exception:" + gen_e);
            }
            return 0;
        }
        
		
		
		
		
    }
}
