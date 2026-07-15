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
    /// Scene3D/2.5D 战场布局桥接。
    /// 玩法层仍使用 GridMap 的 2D row/col 坐标，Scene3D 负责地图视觉、格子坐标和区域。
    /// </summary>
    public static class Battlefield3DLayoutBridge
    {
        public const string DefaultLayoutPath = "ui/scene3d/gamescene.xml";
        private const string RuntimeRootName = "__Battlefield3D_RuntimeGrid";
        private const string RuntimeVisualRootName = "__Battlefield3D_RuntimeVisuals";
        private static readonly Dictionary<string, Sprite> TileSpriteCache = new Dictionary<string, Sprite>();

        public struct BuildResult
        {
            public CellView[,] cells;
            public string[,] zones;
            public bool hasZones;
            public int found;
            public string path;
        }

        struct TileSpec
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
            public string material;
            public float elevation;
            public bool walkable;
        }

        struct MaterialSpec
        {
            public Color top;
            public Color side;
            public Color edge;
        }

        struct BandSpec
        {
            public int rowFrom;
            public int rowTo;
            public int colFrom;
            public int colTo;
            public bool hasZone;
            public string zone;
            public bool hasMaterial;
            public string material;
            public bool hasElevation;
            public float elevation;
            public bool hasWalkable;
            public bool walkable;
        }

        public static bool HasActiveLayout { get; private set; }

        public static bool IsEnabled()
        {
            return GameConfigBool("battlefield_layout3d_enabled", false);
        }

        public static string ResolveLayoutPath()
        {
            return GameConfigString("battlefield_layout3d_path", DefaultLayoutPath);
        }

        public static bool TryBuildGrid(out BuildResult result)
        {
            result = default(BuildResult);
            HasActiveLayout = false;
            if (!IsEnabled()) return false;

            XDocument doc;
            string path;
            if (!TryReadDocument(out doc, out path)) return false;
            ApplyCameraSettings(doc.Root);

            XElement grid = FindGridElement(doc.Root);
            if (grid == null)
            {
                Debug.LogWarning($"[Battlefield3D] 未找到 <Grid>: {path}");
                return false;
            }

            int rows = AttrInt(grid, "rows", AttrInt(doc.Root, "rows", GridMap.Rows));
            int cols = AttrInt(grid, "cols", AttrInt(doc.Root, "cols", GridMap.Cols));
            if (rows != GridMap.Rows || cols != GridMap.Cols)
                Debug.LogWarning($"[Battlefield3D] 布局网格 {rows}x{cols} 与 grid.tab {GridMap.Rows}x{GridMap.Cols} 不一致，按 grid.tab 裁剪/补齐");

            var specs = BuildTileSpecs(doc.Root, grid, GridMap.Rows, GridMap.Cols);
            if (specs.Count <= 0)
            {
                Debug.LogWarning($"[Battlefield3D] 没有可用 tile 布局: {path}");
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

                var go = new GameObject($"Scene3D_Cell_{s.row}_{s.col}", typeof(SpriteRenderer), typeof(CellView));
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

            Battlefield2DLayoutBridge.ClearRuntimeObjects();

            result = new BuildResult { cells = cells, zones = zones, hasZones = hasZones, found = found, path = path };
            HasActiveLayout = found > 0;
            Debug.Log($"[Battlefield3D] 已从 {path} 构建运行时网格: {found}/{GridMap.Rows * GridMap.Cols} zones={hasZones}");
            return found > 0;
        }

        public static void ClearRuntimeObjects()
        {
            DestroyRuntimeRoot();
            DestroyVisualRoot();
            HasActiveLayout = false;
        }

        public static void RestoreLegacyGridIfNeeded()
        {
            if (GameConfigBool("battlefield_layout_hide_scene_grid", true))
                SetLegacyGridVisible(true);
        }

        public static bool ApplyVisuals()
        {
            if (!IsEnabled() || !HasActiveLayout) return false;

            XDocument doc;
            string path;
            if (!TryReadDocument(out doc, out path)) return false;

            XElement grid = FindGridElement(doc.Root);
            if (grid == null) return false;

            var specs = BuildTileSpecs(doc.Root, grid, GridMap.Rows, GridMap.Cols);
            var materials = BuildMaterials(doc.Root);
            int applied = ApplyVisualElements(doc.Root, specs, materials);
            if (applied > 0) Debug.Log($"[Battlefield3D] 已应用Scene3D视觉层: {applied} tiles/props");
            return applied > 0;
        }

        public static bool TryGetCampWallLayout(string side, out Battlefield2DLayoutBridge.CampWallLayout layout)
        {
            layout = default(Battlefield2DLayoutBridge.CampWallLayout);
            if (!IsEnabled()) return false;

            XDocument doc;
            string path;
            if (!TryReadDocument(out doc, out path)) return false;

            XElement el = FindCampWallElement(doc.Root, side);
            if (el == null) return false;

            layout = new Battlefield2DLayoutBridge.CampWallLayout
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

        static bool TryReadDocument(out XDocument doc, out string path)
        {
            doc = null;
            path = ResolveLayoutPath();
            string xml = ResourceHost.ReadText(path);
            if (string.IsNullOrEmpty(xml))
            {
                Debug.LogWarning($"[Battlefield3D] 布局开启但读不到 XML: {path}");
                return false;
            }

            try { doc = XDocument.Parse(xml); }
            catch (Exception e)
            {
                Debug.LogError($"[Battlefield3D] XML 解析失败 {path}: {e.Message}");
                return false;
            }

            if (doc.Root == null || !string.Equals(doc.Root.Name.LocalName, "Battlefield3D", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[Battlefield3D] 根节点不是 <Battlefield3D>: {path}");
                return false;
            }
            return true;
        }

        static List<TileSpec> BuildTileSpecs(XElement root, XElement grid, int rows, int cols)
        {
            var explicitTiles = BuildExplicitTiles(grid);
            if (explicitTiles.Count > 0) return explicitTiles;
            return BuildGeneratedTiles(root, rows, cols);
        }

        static List<TileSpec> BuildExplicitTiles(XElement grid)
        {
            var result = new List<TileSpec>();
            foreach (var el in grid.Elements())
            {
                if (!string.Equals(el.Name.LocalName, "Tile", StringComparison.OrdinalIgnoreCase)) continue;
                int row = AttrInt(el, "row", 0);
                int col = AttrInt(el, "col", 0);
                if (row <= 0 || col <= 0) continue;
                bool hasZone = HasAttr(el, "zone");
                result.Add(new TileSpec
                {
                    row = row,
                    col = col,
                    x = AttrFloat(el, "x", 0f),
                    y = AttrFloat(el, "y", 0f),
                    z = AttrFloat(el, "z", 0f),
                    width = AttrFloat(el, "width", GridMap.CellSizeX),
                    height = AttrFloat(el, "height", GridMap.CellSizeY),
                    zone = NormalizeCellZone(AttrString(el, "zone", "public")),
                    hasZone = hasZone,
                    material = AttrString(el, "material", "grass"),
                    elevation = AttrFloat(el, "elevation", AttrFloat(el, "heightOffset", 0f)),
                    walkable = AttrBool(el, "walkable", true)
                });
            }
            return result;
        }

        static List<TileSpec> BuildGeneratedTiles(XElement root, int rows, int cols)
        {
            XElement tg = FindTileGridElement(root);
            if (tg == null) return new List<TileSpec>();

            float cellW = AttrFloat(tg, "cellW", GridMap.CellSizeX);
            float cellH = AttrFloat(tg, "cellH", GridMap.CellSizeY);
            float rowStepY = AttrFloat(tg, "rowStepY", cellH);
            float originX = AttrFloat(tg, "originX", -(cols - 1) * cellW * 0.5f);
            float originY = AttrFloat(tg, "originY", (rows - 1) * cellH * 0.5f);
            bool staggerRows = AttrBool(tg, "staggerRows", false);
            float staggerX = AttrFloat(tg, "staggerX", staggerRows ? cellW * 0.5f : 0f);
            string material = AttrString(tg, "defaultMaterial", "grass");
            string zone = NormalizeCellZone(AttrString(tg, "defaultZone", "public"));
            var bands = BuildBandSpecs(tg, rows, cols);

            var result = new List<TileSpec>(rows * cols);
            for (int r = 1; r <= rows; r++)
            {
                float y = originY - (r - 1) * rowStepY;
                float rowOffsetX = staggerRows && (r % 2 == 0) ? staggerX : 0f;
                for (int c = 1; c <= cols; c++)
                {
                    string cellZone = zone;
                    string cellMaterial = material;
                    float elevation = 0f;
                    bool walkable = true;
                    ApplyBands(bands, r, c, ref cellZone, ref cellMaterial, ref elevation, ref walkable);

                    result.Add(new TileSpec
                    {
                        row = r,
                        col = c,
                        x = originX + rowOffsetX + (c - 1) * cellW,
                        y = y,
                        z = 0f,
                        width = cellW,
                        height = cellH,
                        zone = cellZone,
                        hasZone = true,
                        material = cellMaterial,
                        elevation = elevation,
                        walkable = walkable
                    });
                }
            }
            return result;
        }

        static List<BandSpec> BuildBandSpecs(XElement tg, int rows, int cols)
        {
            var result = new List<BandSpec>();
            if (tg == null) return result;

            foreach (var el in tg.Elements())
            {
                string tag = el.Name.LocalName;
                if (!string.Equals(tag, "Band", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tag, "ZoneBand", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tag, "PathBand", StringComparison.OrdinalIgnoreCase))
                    continue;

                int rowFrom = AttrInt(el, "rowFrom", AttrInt(el, "fromRow", 1));
                int rowTo = AttrInt(el, "rowTo", AttrInt(el, "toRow", rows));
                int colFrom = AttrInt(el, "colFrom", AttrInt(el, "fromCol", 1));
                int colTo = AttrInt(el, "colTo", AttrInt(el, "toCol", cols));
                result.Add(new BandSpec
                {
                    rowFrom = Mathf.Clamp(Mathf.Min(rowFrom, rowTo), 1, rows),
                    rowTo = Mathf.Clamp(Mathf.Max(rowFrom, rowTo), 1, rows),
                    colFrom = Mathf.Clamp(Mathf.Min(colFrom, colTo), 1, cols),
                    colTo = Mathf.Clamp(Mathf.Max(colFrom, colTo), 1, cols),
                    hasZone = HasAttr(el, "zone"),
                    zone = NormalizeCellZone(AttrString(el, "zone", "public")),
                    hasMaterial = HasAttr(el, "material"),
                    material = AttrString(el, "material", ""),
                    hasElevation = HasAttr(el, "elevation") || HasAttr(el, "heightOffset"),
                    elevation = AttrFloat(el, "elevation", AttrFloat(el, "heightOffset", 0f)),
                    hasWalkable = HasAttr(el, "walkable"),
                    walkable = AttrBool(el, "walkable", true)
                });
            }

            return result;
        }

        static void ApplyBands(List<BandSpec> bands, int row, int col, ref string zone, ref string material, ref float elevation, ref bool walkable)
        {
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                if (row < b.rowFrom || row > b.rowTo || col < b.colFrom || col > b.colTo) continue;
                if (b.hasZone) zone = b.zone;
                if (b.hasMaterial && !string.IsNullOrEmpty(b.material)) material = b.material;
                if (b.hasElevation) elevation = b.elevation;
                if (b.hasWalkable) walkable = b.walkable;
            }
        }

        static Dictionary<string, MaterialSpec> BuildMaterials(XElement root)
        {
            var result = new Dictionary<string, MaterialSpec>(StringComparer.OrdinalIgnoreCase);
            result["grass"] = new MaterialSpec { top = Hex("#617847"), side = Hex("#3f5b32"), edge = Hex("#28391f") };
            result["road"] = new MaterialSpec { top = Hex("#7a6b50"), side = Hex("#514632"), edge = Hex("#332d22") };
            result["water"] = new MaterialSpec { top = Hex("#315f73"), side = Hex("#203f52"), edge = Hex("#162c38") };
            result["neutral"] = new MaterialSpec { top = Hex("#5d6b49"), side = Hex("#405034"), edge = Hex("#26331f") };
            result["enemy"] = new MaterialSpec { top = Hex("#8b4b44"), side = Hex("#5e302e"), edge = Hex("#311b1a") };
            result["own"] = new MaterialSpec { top = Hex("#4f7d45"), side = Hex("#335b31"), edge = Hex("#1c351d") };
            result["path"] = new MaterialSpec { top = Hex("#7bae58"), side = Hex("#527b3d"), edge = Hex("#2f4a25") };

            XElement materials = FindChild(root, "Materials");
            if (materials == null) return result;
            foreach (var el in materials.Elements())
            {
                if (!string.Equals(el.Name.LocalName, "Material", StringComparison.OrdinalIgnoreCase)) continue;
                string id = AttrString(el, "id", "");
                if (string.IsNullOrEmpty(id)) continue;
                result[id] = new MaterialSpec
                {
                    top = Hex(AttrString(el, "top", "#617847")),
                    side = Hex(AttrString(el, "side", "#3f5b32")),
                    edge = Hex(AttrString(el, "edge", "#28391f"))
                };
            }
            return result;
        }

        static int ApplyVisualElements(XElement root, List<TileSpec> specs, Dictionary<string, MaterialSpec> materials)
        {
            DestroyVisualRoot();
            var visualRoot = RecreateVisualRoot();
            int applied = 0;
            int rowDepth = AttrInt(FindTileGridElement(root), "rowDepth", 8);

            for (int i = 0; i < specs.Count; i++)
            {
                var s = specs[i];
                if (s.row < 1 || s.row > GridMap.Rows || s.col < 1 || s.col > GridMap.Cols) continue;
                if (ApplyTileVisual(s, rowDepth, materials, visualRoot.transform)) applied++;
            }

            XElement props = FindChild(root, "Props");
            if (props != null)
            {
                foreach (var el in props.Elements())
                {
                    if (!string.Equals(el.Name.LocalName, "Prop", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(AttrString(el, "type", ""), "camp_wall", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ApplyPropVisual(el, visualRoot.transform)) applied++;
                }
            }

            return applied;
        }

        static bool ApplyTileVisual(TileSpec s, int rowDepth, Dictionary<string, MaterialSpec> materials, Transform parent)
        {
            MaterialSpec mat;
            if (!materials.TryGetValue(string.IsNullOrEmpty(s.material) ? "grass" : s.material, out mat))
                mat = materials["grass"];

            var sprite = GetTileSprite(mat, s.elevation);
            var go = new GameObject($"Scene3D_Tile_{s.row}_{s.col}");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(s.x, s.y, s.z);

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = HDSortingLayers.Background;
            sr.sortingOrder = -140 + s.row * Mathf.Max(1, rowDepth) + s.col;
            sr.color = Color.white;

            if (sprite != null && sprite.bounds.size.x > 0f && sprite.bounds.size.y > 0f)
            {
                go.transform.localScale = new Vector3(s.width / sprite.bounds.size.x, s.height / sprite.bounds.size.y, 1f);
            }
            return true;
        }

        static bool ApplyPropVisual(XElement el, Transform parent)
        {
            if (!AttrBool(el, "visible", true)) return false;
            string spriteKey = AttrString(el, "asset", AttrString(el, "sprite", ""));
            if (string.IsNullOrEmpty(spriteKey)) return false;
            var sprite = LoadSceneSprite(spriteKey);
            if (sprite == null)
            {
                Debug.LogWarning($"[Battlefield3D] Prop 贴图缺失: {spriteKey}");
                return false;
            }

            string name = AttrString(el, "name", "prop");
            var go = new GameObject("Scene3D_Prop_" + name);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(AttrFloat(el, "x", 0f), AttrFloat(el, "y", 0f), AttrFloat(el, "z", 0f));

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingLayerName = AttrString(el, "sortingLayer", HDSortingLayers.Background);
            sr.sortingOrder = AttrInt(el, "sortingOrder", -60);
            sr.flipX = AttrBool(el, "flipX", false);
            ApplySpriteScale(go, sprite, el);
            return true;
        }

        static Sprite GetTileSprite(MaterialSpec mat, float elevation)
        {
            string key = $"hex_{ColorUtility.ToHtmlStringRGBA(mat.top)}_{ColorUtility.ToHtmlStringRGBA(mat.side)}_{ColorUtility.ToHtmlStringRGBA(mat.edge)}";
            Sprite cached;
            if (TileSpriteCache.TryGetValue(key, out cached) && cached != null) return cached;

            const int w = 128;
            const int h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.name = "Scene3D_Tile_" + key;
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = mat.top;
            tex.SetPixels32(pixels);

            var hex = new[]
            {
                new Vector2Int(31, 8),
                new Vector2Int(96, 8),
                new Vector2Int(122, 64),
                new Vector2Int(96, 120),
                new Vector2Int(31, 120),
                new Vector2Int(6, 64)
            };
            FillPolygon(tex, hex, mat.top);

            var lowerShade = new[]
            {
                new Vector2Int(6, 64),
                new Vector2Int(31, 120),
                new Vector2Int(96, 120),
                new Vector2Int(122, 64),
                new Vector2Int(96, 82),
                new Vector2Int(31, 82)
            };
            FillPolygon(tex, lowerShade, Shade(mat.side, 1.05f));
            DrawPolygon(tex, hex, mat.edge);
            DrawLine(tex, new Vector2Int(6, 64), new Vector2Int(122, 64), Shade(mat.edge, 1.2f));

            tex.Apply(false, true);

            var sprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = tex.name;
            TileSpriteCache[key] = sprite;
            return sprite;
        }

        static void ApplyCameraSettings(XElement root)
        {
            if (root == null) return;
            float size = AttrFloat(root, "cameraSize", 0f);
            if (size <= 0f) return;

            Camera cam = null;
            var go = GameObject.Find("BattleCamera");
            if (go != null) cam = go.GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            if (cam == null || !cam.orthographic) return;

            cam.orthographicSize = size;
        }

        static void FillPolygon(Texture2D tex, Vector2Int[] poly, Color color)
        {
            int minX = tex.width, maxX = 0, minY = tex.height, maxY = 0;
            for (int i = 0; i < poly.Length; i++)
            {
                minX = Mathf.Min(minX, poly[i].x);
                maxX = Mathf.Max(maxX, poly[i].x);
                minY = Mathf.Min(minY, poly[i].y);
                maxY = Mathf.Max(maxY, poly[i].y);
            }

            minX = Mathf.Clamp(minX, 0, tex.width - 1);
            maxX = Mathf.Clamp(maxX, 0, tex.width - 1);
            minY = Mathf.Clamp(minY, 0, tex.height - 1);
            maxY = Mathf.Clamp(maxY, 0, tex.height - 1);

            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (PointInPolygon(x, y, poly)) tex.SetPixel(x, y, color);
        }

        static bool PointInPolygon(int x, int y, Vector2Int[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                float denom = poly[j].y - poly[i].y;
                if (Mathf.Abs(denom) < 0.0001f) continue;
                bool crosses = ((poly[i].y > y) != (poly[j].y > y))
                    && (x < (poly[j].x - poly[i].x) * (y - poly[i].y) / denom + poly[i].x);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        static void DrawPolygon(Texture2D tex, Vector2Int[] poly, Color color)
        {
            for (int i = 0; i < poly.Length; i++)
                DrawLine(tex, poly[i], poly[(i + 1) % poly.Length], color);
        }

        static void DrawLine(Texture2D tex, Vector2Int a, Vector2Int b, Color color)
        {
            int x0 = a.x, y0 = a.y, x1 = b.x, y1 = b.y;
            int dx = Mathf.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                if (x0 >= 0 && x0 < tex.width && y0 >= 0 && y0 < tex.height) tex.SetPixel(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        static Color ZoneTint(string zone)
        {
            zone = NormalizeCellZone(zone);
            if (zone == GridMap.ZoneOwn) return new Color(0.92f, 1f, 0.86f, 1f);
            if (zone == GridMap.ZoneEnemy) return new Color(1f, 0.88f, 0.86f, 1f);
            return Color.white;
        }

        static Color Shade(Color c, float mul)
        {
            return new Color(c.r * mul, c.g * mul, c.b * mul, c.a);
        }

        static Color Hex(string value)
        {
            Color c;
            return ColorUtility.TryParseHtmlString(value, out c) ? c : Color.white;
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

        static void SetLegacyGridVisible(bool visible)
        {
            try
            {
                var legacy = GameObject.FindWithTag("Grid_Container");
                if (legacy != null) legacy.SetActive(visible);
            }
            catch { }
        }

        static XElement FindGridElement(XElement root)
        {
            if (root == null) return null;
            if (string.Equals(root.Name.LocalName, "Grid", StringComparison.OrdinalIgnoreCase)) return root;
            foreach (var el in root.Descendants())
                if (string.Equals(el.Name.LocalName, "Grid", StringComparison.OrdinalIgnoreCase)) return el;
            return null;
        }

        static XElement FindTileGridElement(XElement root)
        {
            return FindChild(root, "TileGrid");
        }

        static XElement FindCampWallElement(XElement root, string side)
        {
            if (root == null) return null;
            foreach (var el in root.Descendants())
            {
                string tag = el.Name.LocalName;
                bool candidate = string.Equals(tag, "CampWall", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(tag, "Prop", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(AttrString(el, "type", ""), "camp_wall", StringComparison.OrdinalIgnoreCase));
                if (!candidate) continue;
                if (string.Equals(AttrString(el, "side", ""), side, StringComparison.OrdinalIgnoreCase)) return el;
            }
            return null;
        }

        static XElement FindChild(XElement root, string tag)
        {
            if (root == null) return null;
            foreach (var el in root.Elements())
                if (string.Equals(el.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase)) return el;
            return null;
        }

        static bool HasAttr(XElement el, string name)
        {
            return el.Attribute(name) != null;
        }

        static int AttrInt(XElement el, string name, int fallback)
        {
            if (el == null) return fallback;
            var a = el.Attribute(name);
            if (a == null) return fallback;
            int v;
            return int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static float AttrFloat(XElement el, string name, float fallback)
        {
            if (el == null) return fallback;
            var a = el.Attribute(name);
            if (a == null) return fallback;
            float v;
            return float.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static bool AttrBool(XElement el, string name, bool fallback)
        {
            if (el == null) return fallback;
            var a = el.Attribute(name);
            if (a == null) return fallback;
            var v = a.Value.Trim();
            if (string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            return fallback;
        }

        static string AttrString(XElement el, string name, string fallback)
        {
            if (el == null) return fallback;
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
