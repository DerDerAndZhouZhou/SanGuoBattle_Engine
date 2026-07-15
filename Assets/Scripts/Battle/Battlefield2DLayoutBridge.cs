using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Engine.Host;
using HeroDefense.Utils;

namespace HeroDefense.Battle
{
    /// <summary>
    /// 2D 战场布局桥接。
    /// UI/2D 场景编辑器只导出布局 XML；本类把布局转换成现有世界战斗对象(CellView)，
    /// 让 Lua 侧继续通过 Battle_CellToWorld / Battle_SetCellHighlight 等接口工作。
    /// </summary>
    public static class Battlefield2DLayoutBridge
    {
        public const string DefaultLayoutPath = "ui/scene2d/battlefield_default.xml";
        private const string RuntimeRootName = "__Battlefield2D_RuntimeGrid";
        private const string RuntimeVisualRootName = "__Battlefield2D_RuntimeVisuals";

        public struct BuildResult
        {
            public CellView[,] cells;
            public string[,] zones;
            public bool hasZones;
            public int found;
            public string path;
        }

        public struct CampWallLayout
        {
            public string side;
            public bool visible;
            public float x;
            public float y;
            public float z;
            public float width;
            public float height;
            public string sortingLayer;
            public int sortingOrder;
            public bool flipX;
        }

        struct CellSpec
        {
            public int row;
            public int col;
            public float x;
            public float y;
            public float z;
            public float width;
            public float height;
            public string zone;
            public bool hasZone;
        }

        public static bool IsEnabled()
        {
            return GameConfigBool("battlefield_layout_enabled", false);
        }

        public static string ResolveLayoutPath()
        {
            return GameConfigString("battlefield_layout_path", DefaultLayoutPath);
        }

        public static bool TryBuildGrid(out BuildResult result)
        {
            result = default(BuildResult);
            if (!IsEnabled()) return false;

            string path = ResolveLayoutPath();
            string xml = ResourceHost.ReadText(path);
            if (string.IsNullOrEmpty(xml))
            {
                Debug.LogWarning($"[Battlefield2D] 布局开启但读不到 XML: {path}");
                return false;
            }

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception e)
            {
                Debug.LogError($"[Battlefield2D] XML 解析失败 {path}: {e.Message}");
                return false;
            }

            XElement grid = FindGridElement(doc.Root);
            if (grid == null)
            {
                Debug.LogWarning($"[Battlefield2D] 未找到 <Grid>: {path}");
                return false;
            }

            int rows = AttrInt(grid, "rows", GridMap.Rows);
            int cols = AttrInt(grid, "cols", GridMap.Cols);
            if (rows != GridMap.Rows || cols != GridMap.Cols)
                Debug.LogWarning($"[Battlefield2D] 布局网格 {rows}x{cols} 与 grid.tab {GridMap.Rows}x{GridMap.Cols} 不一致，按 grid.tab 裁剪/补齐");

            var specs = BuildCellSpecs(grid, GridMap.Rows, GridMap.Cols);
            if (specs.Count <= 0)
            {
                Debug.LogWarning($"[Battlefield2D] 没有可用 cell 布局: {path}");
                return false;
            }

            var root = RecreateRuntimeRoot();
            var cells = new CellView[GridMap.Rows + 1, GridMap.Cols + 1];
            var zones = new string[GridMap.Rows + 1, GridMap.Cols + 1];
            bool hasZones = false;
            int found = 0;

            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                if (s.row < 1 || s.row > GridMap.Rows || s.col < 1 || s.col > GridMap.Cols) continue;

                if (s.hasZone)
                {
                    zones[s.row, s.col] = s.zone;
                    hasZones = true;
                }

                var go = new GameObject($"Scene2D_Cell_{s.row}_{s.col}", typeof(SpriteRenderer), typeof(CellView));
                go.transform.SetParent(root.transform, false);
                go.transform.position = new Vector3(s.x, s.y, s.z);

                var sr = go.GetComponent<SpriteRenderer>();
                sr.sortingLayerName = HDSortingLayers.Grid;
                sr.sortingOrder = -50;
                sr.color = Color.clear;

                var cv = go.GetComponent<CellView>();
                cv.Row = s.row;
                cv.Col = s.col;
                cv.IsCamp = GridMap.IsCellInCamp(s.row, s.col);
                cv.SetVisualCellSize(s.width, s.height);
                cv.RefreshBase();

                cells[s.row, s.col] = cv;
                found++;
            }

            if (GameConfigBool("battlefield_layout_hide_scene_grid", true))
                SetLegacyGridVisible(false);

            Battlefield3DLayoutBridge.ClearRuntimeObjects();

            result = new BuildResult { cells = cells, zones = zones, hasZones = hasZones, found = found, path = path };
            Debug.Log($"[Battlefield2D] 已从 {path} 构建运行时网格: {found}/{GridMap.Rows * GridMap.Cols} zones={hasZones}");
            return found > 0;
        }

        public static void ClearRuntimeObjects()
        {
            DestroyRuntimeRoot();
            DestroyVisualRoot();
        }

        public static void RestoreLegacyGridIfNeeded()
        {
            if (GameConfigBool("battlefield_layout_hide_scene_grid", true))
                SetLegacyGridVisible(true);
        }

        public static void ApplyVisuals()
        {
            if (!IsEnabled()) return;

            string path = ResolveLayoutPath();
            string xml = ResourceHost.ReadText(path);
            if (string.IsNullOrEmpty(xml)) return;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch (Exception e)
            {
                Debug.LogError($"[Battlefield2D] 视觉层 XML 解析失败 {path}: {e.Message}");
                return;
            }

            ApplyVisualElements(doc.Root);
        }

        public static bool TryGetCampWallLayout(string side, out CampWallLayout layout)
        {
            layout = default(CampWallLayout);
            if (!IsEnabled()) return false;

            string path = ResolveLayoutPath();
            string xml = ResourceHost.ReadText(path);
            if (string.IsNullOrEmpty(xml)) return false;

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { return false; }

            XElement el = FindCampWallElement(doc.Root, side);
            if (el == null) return false;

            layout = new CampWallLayout
            {
                side = AttrString(el, "side", side),
                visible = AttrBool(el, "visible", true),
                x = AttrFloat(el, "x", 0f),
                y = AttrFloat(el, "y", 0f),
                z = AttrFloat(el, "z", 0f),
                width = AttrFloat(el, "width", 0f),
                height = AttrFloat(el, "height", 0f),
                sortingLayer = AttrString(el, "sortingLayer", HDSortingLayers.Castle),
                sortingOrder = AttrInt(el, "sortingOrder", -20),
                flipX = AttrBool(el, "flipX", string.Equals(side, "right", StringComparison.OrdinalIgnoreCase))
            };
            return true;
        }

        static List<CellSpec> BuildCellSpecs(XElement grid, int rows, int cols)
        {
            var explicitCells = BuildExplicitCells(grid);
            if (explicitCells.Count > 0) return explicitCells;
            return BuildGeneratedCells(grid, rows, cols);
        }

        static List<CellSpec> BuildExplicitCells(XElement grid)
        {
            var result = new List<CellSpec>();
            foreach (var el in grid.Elements())
            {
                if (!string.Equals(el.Name.LocalName, "Cell", StringComparison.OrdinalIgnoreCase)) continue;
                int row = AttrInt(el, "row", 0);
                int col = AttrInt(el, "col", 0);
                if (row <= 0 || col <= 0) continue;
                bool hasZone = HasAttr(el, "zone");
                result.Add(new CellSpec
                {
                    row = row,
                    col = col,
                    x = AttrFloat(el, "x", 0f),
                    y = AttrFloat(el, "y", 0f),
                    z = AttrFloat(el, "z", 0f),
                    width = AttrFloat(el, "width", GridMap.CellSizeX),
                    height = AttrFloat(el, "height", GridMap.CellSizeY),
                    zone = NormalizeCellZone(AttrString(el, "zone", "public")),
                    hasZone = hasZone
                });
            }
            return result;
        }

        static List<CellSpec> BuildGeneratedCells(XElement grid, int rows, int cols)
        {
            float cellW = AttrFloat(grid, "cellW", GridMap.CellSizeX);
            float cellH = AttrFloat(grid, "cellH", GridMap.CellSizeY);
            float z = AttrFloat(grid, "z", 0f);
            bool perspective = AttrBool(grid, "perspective", false);
            float topScale = AttrFloat(grid, "topScale", 1f);
            float bottomScale = AttrFloat(grid, "bottomScale", 1f);

            bool hasOrigin = HasAttr(grid, "originX") && HasAttr(grid, "originY");
            float originX = AttrFloat(grid, "originX", 0f);
            float originY = AttrFloat(grid, "originY", 0f);

            float centerX = AttrFloat(grid, "centerX", 0f);
            float centerY = AttrFloat(grid, "centerY", 0f);
            if (!hasOrigin && (!HasAttr(grid, "centerX") || !HasAttr(grid, "centerY")))
            {
                originX = -(cols - 1) * cellW * 0.5f;
                originY = (rows - 1) * cellH * 0.5f;
                hasOrigin = true;
            }

            var result = new List<CellSpec>(rows * cols);
            float startY = hasOrigin ? originY : centerY + (rows - 1) * cellH * 0.5f;
            for (int r = 1; r <= rows; r++)
            {
                float rowT = rows > 1 ? (float)(r - 1) / (rows - 1) : 0.5f;
                float rowScale = perspective ? Mathf.Lerp(topScale, bottomScale, rowT) : 1f;
                float stepX = cellW * Mathf.Max(0.01f, rowScale);
                float startX = hasOrigin ? originX : centerX - (cols - 1) * stepX * 0.5f;
                float y = startY - (r - 1) * cellH;
                for (int c = 1; c <= cols; c++)
                {
                    result.Add(new CellSpec
                    {
                        row = r,
                        col = c,
                        x = startX + (c - 1) * stepX,
                        y = y,
                        z = z,
                        width = stepX,
                        height = cellH,
                        zone = "public",
                        hasZone = false
                    });
                }
            }
            return result;
        }

        static GameObject RecreateRuntimeRoot()
        {
            DestroyRuntimeRoot();

            var root = new GameObject(RuntimeRootName);
            root.transform.position = Vector3.zero;
            return root;
        }

        static void DestroyRuntimeRoot()
        {
            var old = GameObject.Find(RuntimeRootName);
            if (old != null)
            {
                old.name = RuntimeRootName + "_Destroying";
                old.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(old);
                else UnityEngine.Object.DestroyImmediate(old);
            }
        }

        static GameObject RecreateVisualRoot()
        {
            DestroyVisualRoot();
            var root = new GameObject(RuntimeVisualRootName);
            root.transform.position = Vector3.zero;
            return root;
        }

        static void DestroyVisualRoot()
        {
            var old = GameObject.Find(RuntimeVisualRootName);
            if (old != null)
            {
                old.name = RuntimeVisualRootName + "_Destroying";
                old.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(old);
                else UnityEngine.Object.DestroyImmediate(old);
            }
        }

        static void ApplyVisualElements(XElement root)
        {
            if (root == null) return;

            DestroyVisualRoot();
            GameObject visualRoot = null;
            int applied = 0;
            foreach (var el in root.Elements())
            {
                if (!IsSpriteElement(el)) continue;
                if (visualRoot == null) visualRoot = RecreateVisualRoot();
                if (ApplySpriteElement(el, visualRoot.transform)) applied++;
            }
            if (applied > 0) Debug.Log($"[Battlefield2D] 已应用视觉层: {applied}");
        }

        static bool IsSpriteElement(XElement el)
        {
            if (el == null) return false;
            string tag = el.Name.LocalName;
            return string.Equals(tag, "Sprite", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, "Image", StringComparison.OrdinalIgnoreCase);
        }

        static bool ApplySpriteElement(XElement el, Transform parent)
        {
            if (!AttrBool(el, "visible", true)) return false;
            string spriteKey = AttrString(el, "sprite", AttrString(el, "key", ""));
            if (string.IsNullOrEmpty(spriteKey)) return false;

            var sprite = LoadSceneSprite(spriteKey);
            if (sprite == null)
            {
                Debug.LogWarning($"[Battlefield2D] 视觉层贴图缺失: {spriteKey}");
                return false;
            }

            string targetTag = AttrString(el, "targetTag", AttrString(el, "tag", ""));
            GameObject go = null;
            if (!string.IsNullOrEmpty(targetTag))
            {
                try { go = GameObject.FindWithTag(targetTag); }
                catch { go = null; }
                if (go == null)
                {
                    Debug.LogWarning($"[Battlefield2D] 视觉层找不到 Tag={targetTag}");
                    return false;
                }
            }
            else
            {
                string name = AttrString(el, "name", "sprite");
                go = new GameObject("Scene2D_Sprite_" + name);
                go.transform.SetParent(parent, false);
            }

            var sr = go.GetComponent<SpriteRenderer>();
            if (sr == null) sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = ParseColor(AttrString(el, "color", ""), Color.white);
            sr.sortingLayerName = AttrString(el, "sortingLayer", HDSortingLayers.Background);
            sr.sortingOrder = AttrInt(el, "sortingOrder", -100);

            var pos = go.transform.position;
            go.transform.position = new Vector3(
                AttrFloat(el, "x", pos.x),
                AttrFloat(el, "y", pos.y),
                AttrFloat(el, "z", pos.z));

            if (AttrBool(el, "fitCamera", false))
                FitSpriteToCamera(go, sprite);
            else
                ApplySpriteScale(go, sprite, el);

            return true;
        }

        static Sprite LoadSceneSprite(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string k = key.Replace('\\', '/').TrimStart('/');
            if (!k.StartsWith("resources/art/", StringComparison.OrdinalIgnoreCase))
                k = "resources/art/" + k;
            if (k.Contains(".")) return ResourceHost.LoadSprite(k, logMissing: false);
            var sp = ResourceHost.LoadSprite(k + ".png", logMissing: false);
            if (sp == null) sp = ResourceHost.LoadSprite(k + ".jpg", logMissing: false);
            return sp;
        }

        static void ApplySpriteScale(GameObject go, Sprite sprite, XElement el)
        {
            float width = AttrFloat(el, "width", 0f);
            float height = AttrFloat(el, "height", 0f);
            if (sprite != null && width > 0f && height > 0f && sprite.bounds.size.x > 0f && sprite.bounds.size.y > 0f)
            {
                go.transform.localScale = new Vector3(width / sprite.bounds.size.x, height / sprite.bounds.size.y, 1f);
                return;
            }

            float scale = AttrFloat(el, "scale", 1f);
            float sx = AttrFloat(el, "scaleX", scale);
            float sy = AttrFloat(el, "scaleY", scale);
            go.transform.localScale = new Vector3(sx, sy, 1f);
        }

        static void FitSpriteToCamera(GameObject go, Sprite sprite)
        {
            var cam = Camera.main;
            if (cam == null || !cam.orthographic || sprite == null) return;
            var size = sprite.bounds.size;
            if (size.x <= 0f || size.y <= 0f) return;
            float camH = cam.orthographicSize * 2f;
            float camW = camH * cam.aspect;
            float scale = Mathf.Max(camW / size.x, camH / size.y);
            go.transform.localScale = new Vector3(scale, scale, 1f);
            var cp = cam.transform.position;
            go.transform.position = new Vector3(cp.x, cp.y, go.transform.position.z);
        }

        static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            Color c;
            return ColorUtility.TryParseHtmlString(value, out c) ? c : fallback;
        }

        static void SetLegacyGridVisible(bool visible)
        {
            try
            {
                var legacy = GameObject.FindWithTag("Grid_Container");
                if (legacy != null) legacy.SetActive(visible);
            }
            catch { /* tag 缺失或场景无旧网格时忽略 */ }
        }

        static XElement FindGridElement(XElement root)
        {
            if (root == null) return null;
            if (string.Equals(root.Name.LocalName, "Grid", StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var el in root.Descendants())
                if (string.Equals(el.Name.LocalName, "Grid", StringComparison.OrdinalIgnoreCase)) return el;
            return null;
        }

        static XElement FindCampWallElement(XElement root, string side)
        {
            if (root == null) return null;
            foreach (var el in root.Descendants())
            {
                if (!string.Equals(el.Name.LocalName, "CampWall", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(AttrString(el, "side", ""), side, StringComparison.OrdinalIgnoreCase)) return el;
            }
            return null;
        }

        static bool HasAttr(XElement el, string name)
        {
            return el.Attribute(name) != null;
        }

        static int AttrInt(XElement el, string name, int fallback)
        {
            var a = el.Attribute(name);
            if (a == null) return fallback;
            int v;
            return int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static float AttrFloat(XElement el, string name, float fallback)
        {
            var a = el.Attribute(name);
            if (a == null) return fallback;
            float v;
            return float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static bool AttrBool(XElement el, string name, bool fallback)
        {
            var a = el.Attribute(name);
            if (a == null) return fallback;
            var v = a.Value.Trim();
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            return fallback;
        }

        static string AttrString(XElement el, string name, string fallback)
        {
            var a = el.Attribute(name);
            return a == null ? fallback : a.Value;
        }

        static string NormalizeCellZone(string value)
        {
            string v = string.IsNullOrEmpty(value) ? "public" : value.Trim().ToLowerInvariant();
            if (v == "own" || v == "player" || v == "friendly") return "own";
            if (v == "enemy" || v == "opponent" || v == "hostile") return "enemy";
            return "public";
        }

        static string GameConfigString(string key, string fallback)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return fallback;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", key);
                return row == null ? fallback : cm.GetValue<string>(row, "value", fallback);
            }
            catch { return fallback; }
        }

        static bool GameConfigBool(string key, bool fallback)
        {
            try
            {
                var cm = ConfigManager.Instance;
                if (cm == null) return fallback;
                cm.LoadIfNeeded();
                var row = cm.GetTableInfo("GameConfig", "key", key);
                return row == null ? fallback : cm.GetValue<bool>(row, "value", fallback);
            }
            catch { return fallback; }
        }
    }
}
