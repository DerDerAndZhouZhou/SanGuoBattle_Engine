using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 网格坐标系唯一映射点（CLAUDE.md §10 R-V9 / R-V13 / R-13）。
    ///
    /// 设计约束：
    ///   - 8×12 网格（v4 2026-05-14 用户拍板，原 v3 是 8×14）
    ///   - cell 像素 128(W)×96(H)，源自 bg 1920×1280（v5 高度改 1280）：战场宽=1920, 战场高=1024(=1280×4/5), 网格宽=1536=1920×4/5/12=128, 网格高=768=1024×3/4/8=96
    ///   - 营帐位置 v5 (4,1)-(5,2)（原 v4 (5,1)-(6,2)）
    ///   - **逻辑坐标**：row/col 左上原点 (1,1) → (rows, cols)；row 向下递增（与设计文档一致）
    ///   - **Unity world**：worldY = -(row - 1) * cellSize（Y 越下 worldY 越小 / 越负 → sortingOrder 越大）
    ///   - 业务 Lua **禁直接用 worldY**，必须走 GridMap / GridSortingService（避免坐标系混用）
    ///   - 0 SerializeField — 全部数据从 grid.txt 读
    ///
    /// 营帐区（不可放置）：默认 (row=5,col=1) 起 2×2 = (5,1)(5,2)(6,1)(6,2)
    ///
    /// 启动期 BattleSceneController.TryEnterReady 调 <see cref="InitFromConfig(int)"/> 初始化。
    /// </summary>
    public static class GridMap
    {
        // ============ 静态状态（从 grid.txt 初始化） ============
        public static int Rows = 8;
        public static int Cols = 12;  // v4 2026-05-14 用户拍板 8×12（原 8×14）
        // R1a 2026-06-10：单格世界尺寸非方格（编辑器实测 1.28×0.96）。原单 CellSize=1.0 是逻辑值、与真实场景
        // 步长不符（仅 Cells==null 兜底公式时命中）→ 拆 X/Y 双轴入 grid.tab(cell_w/cell_h)，统一散落 3 处硬编码
        // （CellView.EnsureCellScale 视觉缩放 / UnitView 包围盒 / 本类兜底公式+lattice 默认步长）。
        public static float CellSizeX = 1.28f;   // 单格世界宽（grid.tab cell_w）
        public static float CellSizeY = 0.96f;   // 单格世界高（grid.tab cell_h；CellToWorld 中按 -Y 方向用）

        // 营帐矩形（不可放置区）：左上 (CampRectRow0, CampRectCol0) + 宽 CampRectW × 高 CampRectH
        // v5 2026-05-14：camp 移到 (4,1)-(5,2)
        public static int CampRectRow0 = 4;
        public static int CampRectCol0 = 1;
        public static int CampRectW = 2;
        public static int CampRectH = 2;

        // 当前关卡 grid_id（用于热重载）
        public static int CurrentGridId = -1;

        private static bool _initialized;

        // ============ O(1) 反查格阵缓存（R0 性能前置 2026-06-10）============
        // WorldToCell 旧实现在 Cells!=null（编辑器手摆 cell = 本项目现实）时走 O(Rows×Cols) 最近邻全扫，
        // 是怪移动/寻路每帧每怪的热路径炸弹（tech-research §3 风险2）。改为：InitFromScene 后从实际 cell
        // 反推一次「原点 + 双轴步长」，WorldToCell 走 round 反查（O(1)）。对非正方形 cell / 网格整体偏移都成立，
        // 且 clamp 进界后与最近邻结果等价。仅当 cell 阵列被编辑器手调成非规则时降级回最近邻慢路径。
        private static bool _latticeValid;
        private static float _latOriginX, _latOriginY;   // cell(1,1) 中心世界坐标
        private static float _latStepX, _latStepY;        // 每列 +x / 每行 +y（行 y 通常为负）

        // ============ 三区（R1b 2026-06-10）：己方区(左N列) / 公共区(中间) / 敌方区(右M列）============
        // 己方区列数 = 基地等级驱动（camp_level.own_zone_cols），由 Lua 经 Battle_SetZones 推入（战斗开始 + 基地升级）。
        // 敌方区列数：通关模式关卡配置 / 假对局对手 bot 基地等级镜像（R6 接；当前默认 0 = 无敌方区）。
        public static int OwnZoneCols = 2;
        public static int EnemyZoneCols = 0;

        /// <summary>从 grid.txt 加载指定 id 的网格配置；幂等多次调用安全。</summary>
        public static void InitFromConfig(int gridId)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null)
                {
                    Debug.LogWarning("[GridMap] ConfigManager.Instance 为 null，沿用默认 8×14");
                    ApplyDefaults();
                    return;
                }

                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("grid", "id", gridId);
                if (row == null)
                {
                    Debug.LogWarning($"[GridMap] grid.txt 中找不到 id={gridId}，沿用默认 8×14");
                    ApplyDefaults();
                    return;
                }

                Rows = cm.GetValue<int>(row, "rows", 8);
                Cols = cm.GetValue<int>(row, "cols", 10);
                CellSizeX = cm.GetValue<float>(row, "cell_w", 1.28f);
                CellSizeY = cm.GetValue<float>(row, "cell_h", 0.96f);

                // camp_rect 字段是 int[]：row0,col0,w,h（TabParser 已按 int[] 解析）
                ParseCampRect(row);

                CurrentGridId = gridId;
                _initialized = true;
                Debug.Log($"[GridMap] InitFromConfig(grid_id={gridId}): {Rows}×{Cols} cell=({CellSizeX}×{CellSizeY}) camp=({CampRectRow0},{CampRectCol0},{CampRectW},{CampRectH})");
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GridMap] InitFromConfig 异常: {e.Message}");
                ApplyDefaults();
            }
        }

        private static void ApplyDefaults()
        {
            Rows = 8; Cols = 10; CellSizeX = 1.28f; CellSizeY = 0.96f;  // v7 8×10 可玩网格（cell 1.28×0.96）
            CampRectRow0 = 0; CampRectCol0 = 0; CampRectW = 0; CampRectH = 0;  // v7 双基地在网格外,无 in-grid 营帐
            _latticeValid = false;   // 格阵缓存在 InitFromScene 后由 ComputeLattice 重建
            _initialized = true;
        }

        private static void ParseCampRect(Dictionary<string, object> row)
        {
            if (!row.TryGetValue("camp_rect", out var raw) || raw == null) return;

            // raw 可能是 int[] / List<int> / List<object> / 逗号字符串"5,1,2,2"
            int[] parts = null;

            if (raw is int[] arr) parts = arr;
            else if (raw is List<int> list) parts = list.ToArray();
            else if (raw is List<object> objList)
            {
                parts = new int[objList.Count];
                for (int i = 0; i < objList.Count; i++)
                    int.TryParse(System.Convert.ToString(objList[i]), out parts[i]);
            }
            else
            {
                var s = raw.ToString();
                if (!string.IsNullOrEmpty(s))
                {
                    var split = s.Split(',');
                    parts = new int[split.Length];
                    for (int i = 0; i < split.Length; i++)
                        int.TryParse(split[i].Trim(), out parts[i]);
                }
            }

            if (parts != null && parts.Length >= 4)
            {
                CampRectRow0 = parts[0];
                CampRectCol0 = parts[1];
                CampRectW = parts[2];
                CampRectH = parts[3];
            }
        }

        // ============ 场景节点表（编辑器预摆 cell GameObject） ============
        public static CellView[,] Cells;  // [row, col] 1-based，[0,*] 与 [*,0] 弃用

        /// <summary>读取 GameScene 中 Tag=Grid_Container 下的 CellView 节点，填到 Cells 表。</summary>
        public static void InitFromScene()
        {
            try
            {
                var container = GameObject.FindWithTag("Grid_Container");
                if (container == null)
                {
                    Debug.LogWarning("[GridMap] 未找到 Tag=Grid_Container 的 GameObject — cell 节点表为空");
                    Cells = null;
                    _latticeValid = false;
                    return;
                }
                Cells = new CellView[Rows + 1, Cols + 1];
                int found = 0;
                foreach (var cv in container.GetComponentsInChildren<CellView>(true))
                {
                    if (cv.Row >= 1 && cv.Row <= Rows && cv.Col >= 1 && cv.Col <= Cols)
                    {
                        Cells[cv.Row, cv.Col] = cv;
                        // 启动时按 IsUnlocked / IsCamp 刷新底色（未解锁 alpha=0，解锁 alpha=0.35）
                        // 否则场景保存的颜色（如 alpha=0.06）会一直显示直到第一次拖拽
                        cv.RefreshBase();
                        found++;
                    }
                }
                Debug.Log($"[GridMap] InitFromScene: 加载 {found} 个 cell 节点");
                ComputeLattice();   // R0：cell 填好后反推格阵参数，启用 WorldToCell O(1) 快路径
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[GridMap] InitFromScene 异常: {e.Message}");
                Cells = null;
                _latticeValid = false;
            }
        }

        // ============ 坐标转换（优先读场景节点位置，回落到公式） ============

        /// <summary>逻辑 (row, col) → Unity world XY。优先读 Cells[row,col].transform.position（编辑器可调），失败回落到公式。</summary>
        public static Vector2 CellToWorld(int row, int col)
        {
            if (Cells != null && row >= 1 && row <= Rows && col >= 1 && col <= Cols)
            {
                var cv = Cells[row, col];
                if (cv != null)
                {
                    var p = cv.transform.position;
                    return new Vector2(p.x, p.y);
                }
            }
            // 回落 1（R2 收尾 2026-06-11）：lattice 有效时用真实格阵外推（原点+步长，支持网格外格如基地列 col=0/11）。
            // 旧逻辑原点公式与场景实际坐标差一个平移(场景 origin=(-5.76,2.08))，命中此分支会落到屏幕外。
            if (_latticeValid)
                return new Vector2(_latOriginX + (col - 1) * _latStepX, _latOriginY + (row - 1) * _latStepY);
            // 回落 2：无场景 cell 也无 lattice（纯逻辑测试）→ 逻辑原点公式。col=1 起为 X 轴 0；row 越大 worldY 越小
            float wx = (col - 1) * CellSizeX + 0.5f * CellSizeX;
            float wy = -(row - 1) * CellSizeY - 0.5f * CellSizeY;
            return new Vector2(wx, wy);
        }

        /// <summary>Unity world XY → 逻辑 (row, col)。优先按场景节点最近邻，回落 floor 公式。
        /// 越界 → row/col 仍按公式输出，调用方需自检 <see cref="IsCellInBounds"/>。</summary>
        /// <summary>InitFromScene 后从实际 cell 反推格阵参数（原点 + 双轴步长），供 WorldToCell O(1) 反查。
        /// 阵列非规则（编辑器手调过个别 cell 位置）→ _latticeValid=false → WorldToCell 降级最近邻。</summary>
        private static void ComputeLattice()
        {
            _latticeValid = false;
            if (Cells == null || Rows < 1 || Cols < 1) return;
            var c11 = Cells[1, 1];
            if (c11 == null) return;
            Vector2 o = c11.transform.position;
            _latOriginX = o.x; _latOriginY = o.y;

            float sx = CellSizeX;
            if (Cols >= 2 && Cells[1, 2] != null) sx = Cells[1, 2].transform.position.x - o.x;
            float sy = -CellSizeY;
            if (Rows >= 2 && Cells[2, 1] != null) sy = Cells[2, 1].transform.position.y - o.y;
            if (Mathf.Abs(sx) < 1e-4f || Mathf.Abs(sy) < 1e-4f) return;   // 退化（单行/单列/重叠）→ 慢路径
            _latStepX = sx; _latStepY = sy;

            // 规则性校验：用步长预测远角 cell，偏差超 1/4 步长 = 非规则阵列 → 降级最近邻
            var far = Cells[Rows, Cols];
            if (far != null)
            {
                Vector2 fp = far.transform.position;
                float predX = _latOriginX + (Cols - 1) * _latStepX;
                float predY = _latOriginY + (Rows - 1) * _latStepY;
                if (Mathf.Abs(fp.x - predX) > Mathf.Abs(_latStepX) * 0.25f ||
                    Mathf.Abs(fp.y - predY) > Mathf.Abs(_latStepY) * 0.25f)
                {
                    Debug.LogWarning("[GridMap] cell 阵列非规则（疑编辑器手调过 cell 位置）→ WorldToCell 走最近邻慢路径");
                    return;
                }
            }
            _latticeValid = true;
            Debug.Log($"[GridMap] 格阵缓存就绪 origin=({_latOriginX:F2},{_latOriginY:F2}) step=({_latStepX:F3},{_latStepY:F3}) → WorldToCell O(1)");
        }

        /// <summary>Unity world XY → 逻辑 (row, col)。规则阵列走 O(1) round 反查（默认快路径）；
        /// 非规则阵列降级最近邻；无 cell 用 floor 公式。前两路 clamp 进界（与旧最近邻一致）；公式路不 clamp（调用方自检 <see cref="IsCellInBounds"/>）。</summary>
        public static (int row, int col) WorldToCell(Vector2 worldXY)
        {
            // 快路径：规则格阵 O(1) round 反查（clamp 进界 → 与最近邻结果等价）
            if (_latticeValid)
            {
                int col = Mathf.Clamp(Mathf.RoundToInt((worldXY.x - _latOriginX) / _latStepX) + 1, 1, Cols);
                int row = Mathf.Clamp(Mathf.RoundToInt((worldXY.y - _latOriginY) / _latStepY) + 1, 1, Rows);
                return (row, col);
            }
            // 慢路径：cell 被编辑器手调成非规则阵列 → 最近邻全扫
            if (Cells != null)
            {
                float bestSqr = float.MaxValue;
                int bestR = 1, bestC = 1;
                for (int r = 1; r <= Rows; r++)
                {
                    for (int c = 1; c <= Cols; c++)
                    {
                        var cv = Cells[r, c]; if (cv == null) continue;
                        var p = (Vector2)cv.transform.position;
                        float d = (p - worldXY).sqrMagnitude;
                        if (d < bestSqr) { bestSqr = d; bestR = r; bestC = c; }
                    }
                }
                return (bestR, bestC);
            }
            // 回落：无 cell → floor 公式（不 clamp，调用方自检越界）
            int col2 = Mathf.FloorToInt(worldXY.x / CellSizeX) + 1;
            int row2 = Mathf.FloorToInt(-worldXY.y / CellSizeY) + 1;
            return (row2, col2);
        }

        public static int WorldToCellRow(float worldY) => WorldToCell(new Vector2(0, worldY)).row;
        public static int WorldToCellCol(float worldX) => WorldToCell(new Vector2(worldX, 0)).col;

        // ============ 边界 / 营帐查询 ============

        public static bool IsCellInBounds(int row, int col)
        {
            return row >= 1 && row <= Rows && col >= 1 && col <= Cols;
        }

        /// <summary>是否在营帐/基地区（v7：双基地移到网格外 → camp_rect 置空(0,0,0,0) → 恒 false → 80 格全可部署）。
        /// place_logic / drag_logic / Place_DropUnlockCard 经此判定；空 camp_rect 时不阻挡任何格。
        /// 怪到达左基地 = 失败：reach 走 lane 末点到达（见 BattleBridge.ResolveWaypointsForLane），不依赖此函数。</summary>
        public static bool IsCellInCamp(int row, int col)
        {
            return row >= CampRectRow0
                && row < CampRectRow0 + CampRectH
                && col >= CampRectCol0
                && col < CampRectCol0 + CampRectW;
        }

        // ============ 三区判定（R1b 2026-06-10）：纯整数比较 O(1) ============

        /// <summary>设置三区列数（己方区左 N 列 / 敌方区右 M 列）。Lua 在战斗开始 + 基地升级时经 Battle_SetZones 推入。
        /// 防重叠：N+M&gt;Cols 时己方区优先、压缩敌方区。</summary>
        public static void InitZones(int ownCols, int enemyCols)
        {
            OwnZoneCols = Mathf.Clamp(ownCols, 0, Cols);
            EnemyZoneCols = Mathf.Clamp(enemyCols, 0, Cols);
            if (OwnZoneCols + EnemyZoneCols > Cols) EnemyZoneCols = Mathf.Max(0, Cols - OwnZoneCols);
            Debug.Log($"[GridMap] 三区 own=左{OwnZoneCols}列 enemy=右{EnemyZoneCols}列 public=中{Mathf.Max(0, Cols - OwnZoneCols - EnemyZoneCols)}列");
        }

        /// <summary>己方区：左起 OwnZoneCols 列（背包卡只能落这里）。</summary>
        public static bool IsCellInOwnZone(int row, int col)
            => IsCellInBounds(row, col) && col <= OwnZoneCols;

        /// <summary>敌方区：右起 EnemyZoneCols 列。</summary>
        public static bool IsCellInEnemyZone(int row, int col)
            => IsCellInBounds(row, col) && EnemyZoneCols > 0 && col > Cols - EnemyZoneCols;

        /// <summary>公共区：中间（非己方、非敌方）。</summary>
        public static bool IsCellInPublicZone(int row, int col)
            => IsCellInBounds(row, col) && !IsCellInOwnZone(row, col) && !IsCellInEnemyZone(row, col);

        // ============ cellId 互转（Lua/SaveManager 用） ============

        public static int RowColToCellId(int row, int col) => (row - 1) * Cols + (col - 1);

        public static (int row, int col) CellIdToRowCol(int cellId)
        {
            int row = (cellId / Cols) + 1;
            int col = (cellId % Cols) + 1;
            return (row, col);
        }

        /// <summary>测试 / Editor 用：是否已初始化（已读过 grid.txt）。</summary>
        public static bool IsInitialized => _initialized;
    }
}
