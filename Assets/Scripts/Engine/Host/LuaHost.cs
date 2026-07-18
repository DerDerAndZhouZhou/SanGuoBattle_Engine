using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Battle;
using HeroDefense.Core;
using HeroDefense.SDK;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Engine.Host
{
    /// <summary>
    /// Lua 虚拟机门面，统一暴露 Include / LoadModuleListFromFile / LoadAllModule。
    /// 业务 Lua 走 Game/ 下 scripts(框架) / modules(业务) / ui(界面) 三层，通过 ResourceHost 读取（五层重组 v3）。
    /// </summary>
    public static class LuaHost
    {
        /// <summary>当前打包/运行作用域（≈BHQSL m_strRunPlace）。决定 config.xml 加载哪个作用域段 + manifest 打包过滤。
        /// 客户端唯一作用域；未来服务器版改此值即切换作用域（五层重组 v3, 2026-06-17）。</summary>
        public const string ActiveScope = "GameClient";

#if XLUA
        private static LuaEnv _env;
        public static LuaEnv Env => _env;

        private static readonly List<string> _pending = new List<string>();
        private static readonly HashSet<string> _loaded = new HashSet<string>();
        private static bool _initialized;

        public static bool IsInitialized => _initialized;

        public static void Boot()
        {
            if (_initialized) return;

            ResourceHost.Boot();
            _env = new LuaEnv();

            _env.Global.Set<string, System.Action<string>>("Include", IncludeLua);
            _env.Global.Set<string, System.Action<string>>("LoadModuleListFromFile", LoadModuleListFromFile);
            _env.Global.Set<string, System.Action>("LoadAllModule", LoadAllModule);

            // 资源加载桥接：Lua 业务层从配置表读 *_key 字段，调下面这些全局函数取 Unity 资产
            _env.Global.Set("Resource_LoadSprite",  (System.Func<string, Sprite>)     LoadSprite);
            _env.Global.Set("Resource_LoadAudio",   (System.Func<string, AudioClip>)  LoadAudio);
            _env.Global.Set("Resource_LoadPrefab",  (System.Func<string, GameObject>) LoadPrefab);
            _env.Global.Set("Resource_LoadText",    (System.Func<string, string>)     LoadText);
            _env.Global.Set("Resource_Exists",      (System.Func<string, bool>)       ResourceExists);

            // ============ 表现层桥接（Agent E / Step 6.5） ============
            // 5 个全局函数封装 HitFeedback / DamageNumberManager / VFXManager / HapticBridge
            // 业务 Lua 走 feedback_manager.lua 门面（Feedback_Hit / Feedback_DamageNumber / ...）
            // xLua delegate userdata 坑（CLAUDE.md §6）：Lua 端用 `fn ~= nil`，不要 `if fn then`
            DamageNumberManager.EnsureInstance();
            VFXManager.EnsureInstance();
            _env.Global.Set("HitFeedback_Play",  (System.Action<long, int>)               HitFeedback.Play);
            _env.Global.Set("DamageNum_Spawn",   (System.Action<float, float, int, int>)  DamageNumberManager.Instance.Spawn);
            // v2 批 1b：Play 改返回实例 id（int）→ 注册 Func；StopById(id) 定点停（落点预警圈）。
            _env.Global.Set("VFX_Play",          (System.Func<string, float, float, float, int>) VFXManager.Instance.Play);
            // v2 批 1b：PlayOnUnit 加可选 durationOverride（cast_vfx 用 cast_time 时长）→ 注册 3 参委托；旧 2 参调用 xLua 补 0 走配置 duration。
            _env.Global.Set("VFX_PlayOnUnit",    (System.Action<long, string, float>)     VFXManager.Instance.PlayOnUnit);
            _env.Global.Set("VFX_StopOnUnit",    (System.Action<long>)                    VFXManager.Instance.StopOnUnit);   // v2 批 0：停掉跟随某单位的全部 vfx（loop 状态圈清理用）
            _env.Global.Set("VFX_StopById",      (System.Action<int>)                     VFXManager.Instance.StopById);     // v2 批 1b：按 Play 返回的 id 停某个 vfx
            _env.Global.Set("VFX_ClearAll",      (System.Action)                          VFXManager.VFX_ClearAll);          // v2 批 1b：清全部活动 vfx
            _env.Global.Set("Device_Vibrate",    (System.Action<int>)                     HapticBridge.Vibrate);

            // ============ Battle 桥接（Agent C / Step 2-5） ============
            // 18+ 个 Battle_* 全局函数，避开 Agent E 上面 5 个表现层 hook。
            // tuple 返回 → 拆为 X/Y/Row/Col 多个标量函数（避 xLua delegate userdata 坑 CLAUDE.md R-V8）。
            // Lua 端调用：直接 `Battle_SpawnUnit(...)`（全局）或 `CS.HeroDefense.Battle.BattleBridge.XXX(...)`（class）。
            // 单位 5
            _env.Global.Set("Battle_SpawnUnit",       (System.Func<int, int, int, long>)         BattleBridge.Battle_SpawnUnit);
            _env.Global.Set("Battle_DestroyUnit",     (System.Action<long>)                      BattleBridge.Battle_DestroyUnit);
            _env.Global.Set("Battle_SetSprite",       (System.Action<long, string>)              BattleBridge.Battle_SetSprite);
            // v2 批 0（2026-06-13）：注册 3 参重载（攻速倍率 → 攻击动画 fps 缩放）。此前注册 2 参版 → Lua 传的第 3 参被 xLua 吞、ScaledFps 恒按基础 fps。
            // 兼容性：只传 2 参的调用者（walk/idle/die 多处）→ xLua 补 speedMult=0 → ScaledFps 内 `speedMult>0f` 判定不进缩放分支 → 仍按基础 fps，零回归。
            _env.Global.Set("Battle_PlayAnim",        (System.Action<long, string, float>)       BattleBridge.Battle_PlayAnim);
            // v2 批 1b：取动画时长（attack 动画时长 → 出手点定时 / 普攻减 CD）。Func<long,string,float,float>。
            _env.Global.Set("Battle_GetAnimLen",      (System.Func<long, string, float, float>)  BattleBridge.Battle_GetAnimLen);
            _env.Global.Set("Battle_SetUnitFacing",   (System.Action<long, bool>)                BattleBridge.Battle_SetUnitFacing);
            _env.Global.Set("Battle_SetWorldPosition",(System.Action<long, float, float>)        BattleBridge.Battle_SetWorldPosition);
            _env.Global.Set("Battle_GetUnitCellAndStop",(System.Func<long, int>)                BattleBridge.Battle_GetUnitCellAndStop);   // 2026-06-14 移动中再拖:读当前视觉格 cellId + 停 GridMover
            _env.Global.Set("Battle_GetUnitCell",     (System.Func<long, int>)                   BattleBridge.Battle_GetUnitCell);          // 2026-06-14 只读当前视觉格(拖拽中动态算路径,不停)
            // v2 批 1b 方案A：加第 3 参 speed（逐单位移速）。旧 2 参 Lua 调用 → xLua 补 speed=0 → BeginPath 走 ConfigSpeed 兜底，零回归。
            _env.Global.Set("Battle_UnitWalkPath",    (System.Action<long, string, float>)       BattleBridge.Battle_UnitWalkPath);   // R2 玩家移动：沿 cell 路径走("r,c;r,c", speed)
            // 敌人 5
            _env.Global.Set("Battle_SpawnEnemy",      (System.Func<string, int, float, float, long>) BattleBridge.Battle_SpawnEnemy);
            _env.Global.Set("Battle_SpawnEnemyAtRow", (System.Func<string, int, float, float, int, long>) BattleBridge.Battle_SpawnEnemyAtRow);
            _env.Global.Set("Battle_SetEnemyHpBar",   (System.Action<long, float>)               BattleBridge.Battle_SetEnemyHpBar);
            _env.Global.Set("Battle_SetEnemySpeed",   (System.Action<long, float>)               BattleBridge.Battle_SetEnemySpeed);
            _env.Global.Set("Battle_SetEnemyHalted",  (System.Action<long, bool>)                BattleBridge.Battle_SetEnemyHalted);
            _env.Global.Set("Battle_GetEnemyRow",     (System.Func<long, int>)                   BattleBridge.Battle_GetEnemyRow);
            _env.Global.Set("Battle_GetEnemyCol",     (System.Func<long, int>)                   BattleBridge.Battle_GetEnemyCol);
            _env.Global.Set("Battle_GetEnemyCellAndStop", (System.Func<long, int>)               BattleBridge.Battle_GetEnemyCellAndStop);
            // T237 怪物 HSV 暗化（怪 = 武将/兵种黑暗变体）
            _env.Global.Set("Battle_SetEnemyHsv",     (System.Action<long, float, float, float>) BattleBridge.Battle_SetEnemyHsv);
            // R6 怪网格步进（块3.2 greedy 选格在 Lua，C# 只做单格位移）+ 瞬移（knockback/测试）
            _env.Global.Set("Battle_EnemyGridMode",   (System.Action<long>)                      BattleBridge.Battle_EnemyGridMode);
            _env.Global.Set("Battle_EnemyStepToCell", (System.Func<long, int, int, bool>)        BattleBridge.Battle_EnemyStepToCell);
            _env.Global.Set("Battle_EnemyStepToXY",   (System.Func<long, float, float, bool>)    BattleBridge.Battle_EnemyStepToXY);  // P2b 围攻：怪连续移动到世界点（环位/沿行推进）
            _env.Global.Set("Battle_SetEnemyCell",    (System.Action<long, int, int>)            BattleBridge.Battle_SetEnemyCell);
            // v2 批 1b：击退怪 cells 格（etKnockback effect；远离基地 +col + bounds-clamp 瞬移）。Action<long,float>。
            _env.Global.Set("Battle_KnockbackEnemy",  (System.Action<long, float>)               BattleBridge.Battle_KnockbackEnemy);
            // 投射物（R5 落格 ToCell + 连弩 LineStop；v2 批 1b 补注册 Tracking + Line —— 技能线投射物需要，此前 C# 有方法但 LuaHost 漏挂）
            _env.Global.Set("Battle_SpawnProjectile", (System.Func<long, long, float, long>)     BattleBridge.Battle_SpawnProjectile);
            _env.Global.Set("Battle_SpawnProjectileTracking", (System.Func<long, long, string, float, long>)                       BattleBridge.Battle_SpawnProjectileTracking);
            _env.Global.Set("Battle_SpawnProjectileToCell",   (System.Func<long, int, int, string, float, long>)                   BattleBridge.Battle_SpawnProjectileToCell);
            _env.Global.Set("Battle_SpawnProjectileLine",     (System.Func<long, float, float, float, float, string, float, long>) BattleBridge.Battle_SpawnProjectileLine);
            _env.Global.Set("Battle_SpawnProjectileLineStop", (System.Func<long, float, float, float, float, string, float, long>) BattleBridge.Battle_SpawnProjectileLineStop);
            // 网格 1
            _env.Global.Set("Battle_SetCellHighlight",(System.Action<int, int, int>)             BattleBridge.Battle_SetCellHighlight);
            _env.Global.Set("Battle_SetTileTint",     (System.Action<int, int, string>)          Battlefield3DLayoutBridge.Battle_SetTileTint);
            // 坐标 4 拆分（tuple 拆分避 xLua delegate userdata 坑）
            _env.Global.Set("Battle_CellToWorldX",    (System.Func<int, int, float>)             BattleBridge.Battle_CellToWorldX);
            _env.Global.Set("Battle_CellToWorldY",    (System.Func<int, int, float>)             BattleBridge.Battle_CellToWorldY);
            _env.Global.Set("Battle_ScreenToCellRow", (System.Func<float, float, int>)           BattleBridge.Battle_ScreenToCellRow);
            _env.Global.Set("Battle_ScreenToCellCol", (System.Func<float, float, int>)           BattleBridge.Battle_ScreenToCellCol);
            _env.Global.Set("Battle_ScreenToWorldX",  (System.Func<float, float, float>)         BattleBridge.Battle_ScreenToWorldX);
            _env.Global.Set("Battle_ScreenToWorldY",  (System.Func<float, float, float>)         BattleBridge.Battle_ScreenToWorldY);
            // 单位变换（拖拽 ghost 用）
            _env.Global.Set("Battle_SetAlpha",        (System.Action<long, float>)               BattleBridge.Battle_SetAlpha);
            _env.Global.Set("Battle_SetScale",        (System.Action<long, float>)               BattleBridge.Battle_SetScale);
            // UI Ghost（Bug 4 fix — 拖拽 inventory 卡时显示在 InventoryPanel 之上）
            _env.Global.Set("Battle_ShowUIGhost",     (System.Action<string, float, float, float, float>) BattleBridge.Battle_ShowUIGhost);
            _env.Global.Set("Battle_MoveUIGhost",     (System.Action<float, float>)              BattleBridge.Battle_MoveUIGhost);
            _env.Global.Set("Battle_HideUIGhost",     (System.Action)                            BattleBridge.Battle_HideUIGhost);
            _env.Global.Set("Battle_IsPointerOverInventory", (System.Func<float, float, bool>)   BattleBridge.Battle_IsPointerOverInventory);
            _env.Global.Set("Battle_IsPointerOverShop", (System.Func<float, float, bool>)        BattleBridge.Battle_IsPointerOverShop);   // F3 商场回收拖放命中
            // Round 8 (2026-05-15) — Issue 1 真因：背包合成/升级/swap 全失效，Lua 看 nil 不调
            _env.Global.Set("Battle_GetInventorySlotAtScreen", (System.Func<float, float, int>) BattleBridge.Battle_GetInventorySlotAtScreen);
            // 边界 4
            _env.Global.Set("Battle_IsCellInBounds",  (System.Func<int, int, bool>)              BattleBridge.Battle_IsCellInBounds);
            _env.Global.Set("Battle_IsCellInCamp",    (System.Func<int, int, bool>)              BattleBridge.Battle_IsCellInCamp);
            // 三区桥（R1b 2026-06-10 加，R1c 落点门控依赖；LuaHost 是手工白名单注册非反射 → 必须显式补）
            _env.Global.Set("Battle_SetZones",          (System.Action<int, int>)                BattleBridge.Battle_SetZones);
            _env.Global.Set("Battle_IsCellInOwnZone",   (System.Func<int, int, bool>)            BattleBridge.Battle_IsCellInOwnZone);
            _env.Global.Set("Battle_IsCellInPublicZone",(System.Func<int, int, bool>)            BattleBridge.Battle_IsCellInPublicZone);
            _env.Global.Set("Battle_IsCellInEnemyZone", (System.Func<int, int, bool>)            BattleBridge.Battle_IsCellInEnemyZone);
            // T202 (2026-05-21) — 玩法模式 grid 视觉样式（R1d 保留；解锁动画桥已删）
            _env.Global.Set("Battle_SetGridVisualStyle",   (System.Action<string>)               BattleBridge.Battle_SetGridVisualStyle);
            _env.Global.Set("Battle_ReloadScene2DLayout",  (System.Func<bool>)                    BattleBridge.Battle_ReloadScene2DLayout);
            _env.Global.Set("Battle_ReloadScene3DLayout",  (System.Func<bool>)                    BattleBridge.Battle_ReloadScene3DLayout);
            // T203 (2026-05-21) — 单位头顶血量条可见性（拖拽时隐藏 / 落地恢复）
            _env.Global.Set("Battle_SetUnitHpBarVisible",  (System.Action<long, bool>)           BattleBridge.Battle_SetUnitHpBarVisible);
            // 排序 1
            _env.Global.Set("Battle_CalcSortingOrder",(System.Func<float, int>)                  BattleBridge.Battle_CalcSortingOrder);
            // 时间 1
            _env.Global.Set("Battle_SetTimeScale",    (System.Action<float>)                     BattleBridge.Battle_SetTimeScale);
            _env.Global.Set("Battle_GetGameTime",     (System.Func<float>)                       BattleBridge.Battle_GetGameTime);   // v2 批 0 P0：权威对局时钟（暂停冻结）→ 恢复主动技调度/buff 到期

            // ============ 存档桥（核心循环 P0-2 / P0-3） ============
            // 通用 KV 存取 + 货币入库。业务（Lua）约定 key，C# 只提供存取通道。
            // xLua delegate userdata 坑（CLAUDE.md §6）：Lua 端用 `fn ~= nil`，不要 `if fn then`
            _env.Global.Set("Save_GetInt",    (System.Func<string, int, int>)        SaveBridge.GetInt);
            _env.Global.Set("Save_SetInt",    (System.Action<string, int>)           SaveBridge.SetInt);
            _env.Global.Set("Save_GetString", (System.Func<string, string, string>)  SaveBridge.GetString);
            _env.Global.Set("Save_SetString", (System.Action<string, string>)        SaveBridge.SetString);
            _env.Global.Set("Save_AddCoins",  (System.Action<int>)                   SaveBridge.AddCoins);
            _env.Global.Set("Save_AddGems",   (System.Action<int>)                   SaveBridge.AddGems);

            // ============ 广告桥（核心循环 P0-3：结算页翻倍 / 失败复活） ============
            // Ad_Show(placement, luaCallback) — 回调用 LuaFunction 形态规避 delegate 生成配置坑（R2）
            _env.Global.Set("Ad_Show",        (System.Action<string, LuaFunction>)   AdBridge.Show);

            // ============ 热更 UI 桥（阶段0：XML→UGUI 运行时构建，仿 BHQSL；见 Docs/bhqsl-ui-research-2026-06.md） ============
            // UI 结构 = Game/ui/*.xml（CDN 热更），逻辑 = Game/lua/ui/*.lua；改面板不重打包。
            // 构建核心 = UIXmlBuilder（可抽共享包）；HDUIXmlHost = 游戏端胶水（接 ResourceHost/LuaHost）。
            _env.Global.Set("UI_LoadPanel",    (System.Func<string, GameObject>)              UI.Xml.HDUIXmlHost.LoadPanel);
            _env.Global.Set("UI_ReloadPanel",  (System.Func<string, GameObject>)              UI.Xml.HDUIXmlHost.ReloadPanel);
            _env.Global.Set("UI_DestroyPanel", (System.Action<string>)                        UI.Xml.HDUIXmlHost.DestroyPanel);
            _env.Global.Set("UI_Find",         (System.Func<GameObject, string, GameObject>)  UI.Xml.HDUIXmlHost.Find);
            _env.Global.Set("UI_SetText",      (System.Action<GameObject, string>)            UI.Xml.HDUIXmlHost.SetText);
            _env.Global.Set("UI_GetText",      (System.Func<GameObject, string>)              UI.Xml.HDUIXmlHost.GetText);
            _env.Global.Set("UI_SetImage",     (System.Action<GameObject, string>)            UI.Xml.HDUIXmlHost.SetImage);
            _env.Global.Set("UI_SetColor",     (System.Action<GameObject, string>)            UI.Xml.HDUIXmlHost.SetColor);
            _env.Global.Set("UI_SetFill",      (System.Action<GameObject, float>)             UI.Xml.HDUIXmlHost.SetFill);
            _env.Global.Set("UI_SetChecked",   (System.Action<GameObject, bool>)              UI.Xml.HDUIXmlHost.SetChecked);
            _env.Global.Set("UI_GetChecked",   (System.Func<GameObject, bool>)                UI.Xml.HDUIXmlHost.GetChecked);
            _env.Global.Set("UI_BindClick",    (System.Action<GameObject, string>)            UI.Xml.HDUIXmlHost.BindClick);
            _env.Global.Set("UI_CreateFromTemplate", (System.Func<GameObject, string, string, GameObject>) UI.Xml.HDUIXmlHost.CreateFromTemplate);
            _env.Global.Set("UI_DestroyChildren", (System.Action<GameObject>)                 UI.Xml.HDUIXmlHost.DestroyChildren);
            _env.Global.Set("UI_ReloadTemplates", (System.Action)                             UI.Xml.HDUIXmlHost.ReloadTemplates);
            _env.Global.Set("UI_SetActive",    (System.Action<GameObject, bool>)              UI.Xml.HDUIXmlHost.SetActive);
            _env.Global.Set("UI_BringToFront", (System.Action<GameObject>)                    UI.Xml.HDUIXmlHost.BringToFront);
            _env.Global.Set("UI_AttachDragSource", (System.Action<GameObject, long, string>)  UI.Xml.HDUIXmlHost.AttachDragSource);
            // 热更 UI 迁移（HUD 簇）：库存/商场面板加载后由 Lua 注入引用给 BattleBridge（拖拽落点遮挡守卫 + slot 反查）
            _env.Global.Set("Battle_SetInventoryRefs", (System.Action<GameObject, GameObject>) BattleBridge.Battle_SetInventoryRefs);
            _env.Global.Set("Battle_SetShopRef",       (System.Action<GameObject>)             BattleBridge.Battle_SetShopRef);

            EnumRegistry.LoadIfNeeded();
            EnumRegistry.InjectToLua(_env);

            // 框架层（scripts/modulelist.xml → scripts 模块；scripts/config.xml 按 ActiveScope 作用域段定序加载·五层重组 v3）
            LoadModuleListFromFile("scripts/modulelist.xml");
            LoadAllModule();

            // 业务入口（main.lua 在 ui/main/·内部加载 modules/ + ui/ 两层 modulelist）
            IncludeLua("ui/main/main.lua");

            _initialized = true;
            Debug.Log("[LuaHost] 启动完成");
        }

        public static void Shutdown()
        {
            // v2 批 1b：先释放热路径缓存的 LuaFunction（env Dispose 后旧引用失效，必须丢弃避免 next boot 用脏引用）。
            ProjectileTicker.ResetLuaCache();
            _env?.Dispose();
            _env = null;
            _pending.Clear();
            _loaded.Clear();
            _initialized = false;
        }

        public static void Tick()
        {
            _env?.Tick();
        }

        /// <summary>执行指定 .lua 文件（在真全局 _G 下，不沙箱）。幂等：同一路径仅执行一次。</summary>
        public static void IncludeLua(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return;
            if (!_loaded.Add(relPath))
                return;

            var bytes = ResourceHost.ReadBytes(relPath);
            if (bytes == null)
            {
                Debug.LogError($"[LuaHost] Include 找不到文件: {relPath}");
                return;
            }

            try
            {
                _env.DoString(bytes, relPath);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LuaHost] Include {relPath} 执行失败: {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>读模块清单，把模块下所有 .lua 加到待加载列表。
        /// .xml 格式（推荐）：&lt;Modules&gt;&lt;Module Name="framework"/&gt;...&lt;/Modules&gt;
        ///   每个 Module Name 是 lua/ 相对子路径，递归扫该路径下所有 .lua（按字母序）。
        /// .txt 格式（兼容）：每行一个 lua 文件相对路径，# 或 // 开头跳过。</summary>
        public static void LoadModuleListFromFile(string relPath)
        {
            var text = ResourceHost.ReadText(relPath);
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning($"[LuaHost] LoadModuleListFromFile: {relPath} 未找到或为空");
                return;
            }

            if (relPath.EndsWith(".xml", System.StringComparison.OrdinalIgnoreCase))
            {
                LoadModuleListFromXml(text);
                return;
            }

            // 旧 .txt 兼容路径：每行一个 .lua 文件
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().Replace("\r", "");
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                _pending.Add(line);
            }
        }

        /// <summary>解析 &lt;Modules&gt;&lt;Module Name="X"/&gt;&lt;/Modules&gt; → 逐模块加入 _pending（五层重组 v3）。
        /// 每个 Module Name = Game 根相对目录（如 "scripts" / "modules/battle" / "ui/inventory"）：
        ///   必须有 config.xml，按 ActiveScope 作用域段的 &lt;Script File&gt; 显式定序加载（≈BHQSL；
        ///   无文件夹扫描兜底——缺 config.xml = 跳过+告警，用户 2026-06-18 定）。
        /// 注释掉某 &lt;Module&gt; 行即不加载该模块（选择性裁剪）。</summary>
        private static void LoadModuleListFromXml(string xmlText)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xmlText); }
            catch (System.Exception e)
            {
                Debug.LogError($"[LuaHost] XML 解析失败: {e.Message}");
                return;
            }
            if (doc.Root == null) return;

            int totalAdded = 0;
            foreach (var moduleElem in doc.Root.Elements("Module"))
            {
                var name = moduleElem.Attribute("Name")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                name = name.Trim().TrimStart('/', '\\').TrimEnd('/', '\\');
                int added = AddModuleScripts(name);
                Debug.Log($"[LuaHost] module '{name}' 加载 {added} 个 .lua");
                totalAdded += added;
            }
            Debug.Log($"[LuaHost] XML modulelist 共 {totalAdded} 个 .lua 加入 pending");
        }

        /// <summary>加载单个模块目录的脚本：必须有 config.xml，按 ActiveScope 段显式定序。
        /// 无文件夹扫描兜底（用户 2026-06-18 定）——缺 config.xml / 解析失败 = 加载 0 个 + 告警。返回加入数。</summary>
        private static int AddModuleScripts(string relDir)
        {
            // ResourceHost.ReadText 缺文件静默返 null（不打日志），可安全用作 config.xml 探测
            var cfgText = ResourceHost.ReadText(relDir + "/config.xml");
            if (string.IsNullOrEmpty(cfgText))
            {
                Debug.LogWarning($"[LuaHost] 模块 '{relDir}' 缺 config.xml → 跳过（无文件夹扫描兜底）");
                return 0;
            }
            var ordered = ParseScopedScripts(cfgText, ActiveScope);
            if (ordered == null)
            {
                Debug.LogError($"[LuaHost] '{relDir}/config.xml' 解析失败 → 加载 0 个（无文件夹扫描兜底）");
                return 0;
            }
            foreach (var f in ordered) _pending.Add(f);
            return ordered.Count;
        }

        /// <summary>解析 config.xml 指定作用域段 → 有序 Script File 列表。
        /// 返回 null = 解析失败（调用方回退扫描）；空 list = 该作用域无脚本（合法，加载 0 个）。</summary>
        private static List<string> ParseScopedScripts(string xmlText, string scope)
        {
            XDocument doc;
            try { doc = XDocument.Parse(xmlText); }
            catch { return null; }
            if (doc.Root == null) return null;
            var result = new List<string>();
            var scopeElem = doc.Root.Element(scope);
            if (scopeElem == null) return result;   // 缺该作用域段 → 本作用域不加载此模块（合法）
            foreach (var s in scopeElem.Elements("Script"))
            {
                var file = s.Attribute("File")?.Value;
                if (string.IsNullOrEmpty(file)) continue;
                result.Add(file.Trim().Replace('\\', '/'));
            }
            return result;
        }

        /// <summary>加载 _pending 里全部模块（幂等 + 清空 pending）。</summary>
        public static void LoadAllModule()
        {
            var snapshot = new List<string>(_pending);
            _pending.Clear();
            foreach (var p in snapshot)
                IncludeLua(p);
        }

        /// <summary>调 Lua 全局函数（无返回值）。</summary>
        public static void CallGlobal(string funcName, params object[] args)
        {
            if (_env == null) return;
            try
            {
                var func = _env.Global.Get<LuaFunction>(funcName);
                if (func != null)
                {
                    func.Call(args);
                    func.Dispose();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LuaHost] CallGlobal '{funcName}' 失败: {e.Message}");
            }
        }

        public static T GetGlobal<T>(string name)
        {
            return _env != null ? _env.Global.Get<T>(name) : default;
        }

        public static object[] DoString(string luaCode)
        {
            if (_env == null) return null;
            try { return _env.DoString(luaCode); }
            catch (System.Exception e)
            {
                Debug.LogError($"[LuaHost] DoString 失败: {e.Message}");
                return null;
            }
        }

        // 缓存：避免同一资源被反复解码（弱引用，Sprite 销毁后自动清）
        private static readonly Dictionary<string, Sprite>     _spriteCache = new Dictionary<string, Sprite>();
        private static readonly Dictionary<string, AudioClip>  _audioCache  = new Dictionary<string, AudioClip>();
        private static readonly Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();

        /// <summary>
        /// 通过 ResourceHost 读取图片字节，解码成 Sprite 返回。失败返回 null。
        /// 图片来源统一走 Game/ 目录（CDN 热更同源）。
        /// </summary>
        public static Sprite LoadSprite(string relPath) => LoadSprite(relPath, true);

        /// <summary>logMissing=false：文件缺失时静默返回 null（动画帧数自动探测靠"加载失败"判终点，不算错误）。</summary>
        public static Sprite LoadSprite(string relPath, bool logMissing)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            if (_spriteCache.TryGetValue(relPath, out var cached) && cached != null) return cached;

            var bytes = ResourceHost.ReadBytes(relPath);
            if (bytes == null || bytes.Length == 0)
            {
                if (logMissing) Debug.LogWarning($"[LuaHost] LoadSprite 失败: {relPath}");
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes))
            {
                Object.Destroy(tex);
                return null;
            }
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _spriteCache[relPath] = sprite;
            return sprite;
        }

        /// <summary>
        /// 加载音频。Game/resources/art/sfx/ 或 Game/resources/art/bgm/ 下的 .wav / .mp3 / .ogg。
        /// </summary>
        public static AudioClip LoadAudio(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            if (_audioCache.TryGetValue(relPath, out var cached) && cached != null) return cached;

            // WebGL 下 AudioClip 必须经 UnityWebRequest，但本工程统一走 ResourceHost 字节流。
            // MVP 阶段：仅支持 WAV（短促音效）；BGM/MP3 后续走 UnityWebRequestMultimedia.GetAudioClip 异步路径。
            var bytes = ResourceHost.ReadBytes(relPath);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning($"[LuaHost] LoadAudio 失败: {relPath}");
                return null;
            }

            // 简易 WAV 解码（PCM 16-bit 单声道）。完整解码（多声道/位深/MP3）后续接入。
            var clip = WavDecoder.Decode(bytes, relPath);
            if (clip != null) _audioCache[relPath] = clip;
            return clip;
        }

        // v5 round 5 (2026-05-15) — Issue 1：vfx prefab 未到位时 console 刷屏（synth_uplight 每次合成都 warn）
        // 改为每个 key 只 warn 一次；VFXManager 已有占位 GO 兜底，业务不受影响
        private static readonly HashSet<string> _warnedMissingPrefabs = new HashSet<string>();

        /// <summary>
        /// 加载 Prefab。MVP 阶段先走 Resources.Load（Editor / Standalone）；
        /// 热更阶段切到 AssetBundle 或 Addressables。
        /// </summary>
        public static GameObject LoadPrefab(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            if (_prefabCache.TryGetValue(relPath, out var cached) && cached != null) return cached;

            // 先去 Resources/（Unity 内置主包）
            var prefab = Resources.Load<GameObject>(relPath);
            if (prefab != null)
            {
                _prefabCache[relPath] = prefab;
                return prefab;
            }

            // TODO: 接 AssetBundle / Addressables 路径（M3+ 引入热更资产时）
            if (_warnedMissingPrefabs.Add(relPath))
            {
                Debug.LogWarning($"[LuaHost] LoadPrefab 未找到: {relPath}（占位回落 default_prefab，本 key 不再重复 warn）");
            }
            return null;
        }

        /// <summary>读纯文本（i18n 文案 / 关卡 JSON / 任意配置）。</summary>
        public static string LoadText(string relPath)
        {
            return string.IsNullOrEmpty(relPath) ? null : ResourceHost.ReadText(relPath);
        }

        /// <summary>资源是否存在（不实际加载，仅探测）。</summary>
        public static bool ResourceExists(string relPath)
        {
            return !string.IsNullOrEmpty(relPath) && ResourceHost.Exists(relPath);
        }

        /// <summary>清空资源缓存（场景切换时可手动调用，避免内存增长）。</summary>
        public static void ClearResourceCache()
        {
            _spriteCache.Clear();
            _audioCache.Clear();
            _prefabCache.Clear();
        }
#else
        public static void Boot() { Debug.LogWarning("[LuaHost] xLua 未启用"); }
        public static void Shutdown() {}
        public static void Tick() {}
        public static bool IsInitialized => false;
        public static void IncludeLua(string relPath) {}
        public static void LoadModuleListFromFile(string relPath) {}
        public static void LoadAllModule() {}
        public static void CallGlobal(string funcName, params object[] args) {}
        public static T GetGlobal<T>(string name) { return default; }
        public static object[] DoString(string luaCode) { return null; }
        public static Sprite LoadSprite(string relPath) { return null; }
        public static Sprite LoadSprite(string relPath, bool logMissing) { return null; }
        public static AudioClip LoadAudio(string relPath) { return null; }
        public static GameObject LoadPrefab(string relPath) { return null; }
        public static string LoadText(string relPath) { return null; }
        public static bool ResourceExists(string relPath) { return false; }
        public static void ClearResourceCache() {}
#endif
    }
}

