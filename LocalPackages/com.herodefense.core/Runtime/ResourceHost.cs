using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UnityEngine;

namespace HeroDefense.Engine.Host
{
    /// <summary>
    /// 环境无关的资源读取层。业务代码不感知资源来源。
    /// 平台支持：
    ///   Editor:      {ProjectRoot}/../Game/
    ///   Standalone:  exe 所在目录向上搜 settings/Enum.tab
    ///   WebGL/其他:  主 = persistentDataPath/Game；回落 = StreamingAssets/Game
    ///
    /// 设计思路：
    ///   - 所有 IO 通过 _baseDir（主）+ _fallbackBaseDir（回落）两条路径查询
    ///   - Read/Exists 先看主路径，miss 才看回落（兼容 "首启 baseline + 热更增量" 场景）
    ///   - EnumerateFiles 在 WebGL 下无法 Directory.GetFiles，改读内存 manifest
    ///   - manifest 由 Boot() 自动尝试加载（不存在不报错）
    /// </summary>
    public static class ResourceHost
    {
        private static string _baseDir;
        private static string _fallbackBaseDir;
        private static bool _initialized;

        /// <summary>manifest.json 中声明过的文件清单（相对路径 → 条目）；未加载时为 null。</summary>
        private static Dictionary<string, ManifestFileEntry> _manifest;

        public static string BaseDir => _baseDir;
        public static string FallbackBaseDir => _fallbackBaseDir;

        /// <summary>manifest 是否已成功加载；EnumerateFiles 在 WebGL 等受限平台会优先走此清单。</summary>
        public static bool HasManifest => _manifest != null && _manifest.Count > 0;

        public static void Boot()
        {
            if (_initialized) return;

#if UNITY_EDITOR
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            _baseDir = Path.GetFullPath(Path.Combine(projectRoot, "..", "Game"));
            _fallbackBaseDir = null;
#elif UNITY_STANDALONE
            string exeDir = Path.GetDirectoryName(Application.dataPath);
            string searchDir = exeDir;
            bool found = false;
            for (int i = 0; i < 5; i++)
            {
                // 包内布局优先：exeDir/Game/settings/Enum.tab（PC 内测包 Build Windows 用此布局）
                if (File.Exists(Path.Combine(searchDir, "Game", "settings", "Enum.tab")))
                {
                    _baseDir = Path.GetFullPath(Path.Combine(searchDir, "Game"));
                    found = true;
                    break;
                }
                // 源工程布局兼容：searchDir/settings/Enum.tab（exe 放在 Game/dist/Windows/ 时向上搜到 Game/）
                if (File.Exists(Path.Combine(searchDir, "settings", "Enum.tab")))
                {
                    _baseDir = Path.GetFullPath(searchDir);
                    found = true;
                    break;
                }
                var parent = Directory.GetParent(searchDir);
                if (parent == null) break;
                searchDir = parent.FullName;
            }
            if (!found)
            {
                _baseDir = Path.GetFullPath(Path.Combine(exeDir, "..", ".."));
                Debug.LogWarning($"[ResourceHost] Standalone 向上搜索未找到 (Game/)settings/Enum.tab，fallback 到 {_baseDir}");
            }
            _fallbackBaseDir = null;
#else
            _baseDir = Path.Combine(Application.persistentDataPath, "Game");
            _fallbackBaseDir = Path.Combine(Application.streamingAssetsPath, "Game");
#endif

            _initialized = true;
            Debug.Log($"[ResourceHost] baseDir = {_baseDir}" +
                      (string.IsNullOrEmpty(_fallbackBaseDir) ? "" : $"，fallback = {_fallbackBaseDir}"));

            TryLoadManifest();
            LoadAtlasIndex();  // 2026-05-29 (Q3): 扫 Game/resources/art/atlas/*.xml 建索引，LoadSprite 会优先查它

            if (!Directory.Exists(Path.Combine(_baseDir, "settings")) && !ManifestHasPrefix("settings/"))
            {
                if (!string.IsNullOrEmpty(_fallbackBaseDir) && Directory.Exists(Path.Combine(_fallbackBaseDir, "settings")))
                    Debug.Log($"[ResourceHost] settings/ 走 fallback 路径: {_fallbackBaseDir}");
                else
                    Debug.LogError($"[ResourceHost] baseDir 下未找到 settings/ 目录: {_baseDir}");
            }
        }

        private static void EnsureBooted() { if (!_initialized) Boot(); }

        // ==================================================================
        // 读取 API
        // ==================================================================

        public static bool Exists(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            EnsureBooted();
            string primary = ResolvePath(relPath, _baseDir);
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary)) return true;
            if (!string.IsNullOrEmpty(_fallbackBaseDir))
            {
                string fb = ResolvePath(relPath, _fallbackBaseDir);
                if (!string.IsNullOrEmpty(fb) && File.Exists(fb)) return true;
            }
            return false;
        }

        public static string ReadText(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            EnsureBooted();
            string primary = ResolvePath(relPath, _baseDir);
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary))
                return ReadAllTextShared(primary);
            if (!string.IsNullOrEmpty(_fallbackBaseDir))
            {
                string fb = ResolvePath(relPath, _fallbackBaseDir);
                if (!string.IsNullOrEmpty(fb) && File.Exists(fb))
                    return ReadAllTextShared(fb);
            }
            return null;
        }

        public static byte[] ReadBytes(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            EnsureBooted();
            string primary = ResolvePath(relPath, _baseDir);
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary))
                return ReadAllBytesShared(primary);
            if (!string.IsNullOrEmpty(_fallbackBaseDir))
            {
                string fb = ResolvePath(relPath, _fallbackBaseDir);
                if (!string.IsNullOrEmpty(fb) && File.Exists(fb))
                    return ReadAllBytesShared(fb);
            }
            return null;
        }

        // 配置/资源读取容错（2026-06-07）：策划常在 WPS/Excel 里开着 .tab 配置表编辑，
        // 而 File.ReadAllText/ReadAllBytes 隐含 FileShare.Read → 与 WPS 的写权限冲突 →
        // IOException(Sharing violation) → ConfigManager.Load 抛异常 → 启动链断。
        // 改用 FileShare.ReadWrite 打开：即便别的进程正以读写方式开着同一文件也能读到
        // （实测 WPS 占用 npc.tab 时 share=ReadWrite 可读通，share=Read 失败）。
        private static string ReadAllTextShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))   // 默认 UTF-8 + BOM 探测，与 File.ReadAllText 一致
                return sr.ReadToEnd();
        }

        private static byte[] ReadAllBytesShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        // ==================================================================
        // Sprite 加载（M1.5 美术配置铁律 + 项目约定 PPU=100）
        // 业务代码统一通过此方法把配置表 *_key 字段转成 Sprite，再喂 SpriteRenderer / Image。
        // 缓存：同 relPath 解码一次，后续命中 Dictionary。
        // 失败：返回 null + LogWarning（调用方应有 default 占位回落）。
        // ==================================================================

        private const float DEFAULT_PIXELS_PER_UNIT = 100f;
        private static readonly Dictionary<string, Sprite> _spriteCache = new Dictionary<string, Sprite>();

        /// <summary>
        /// 按 Game/ 相对路径加载 PNG 为 Sprite（PPU=100）。
        /// 例：LoadSprite("resources/art/grid/cell_unlocked.png")，对应 Game/resources/art/grid/cell_unlocked.png。
        /// 配置表的 sprite_key 字段建议存不带后缀的相对路径前缀（如 "grid/cell_unlocked"），
        /// 业务代码补成 "resources/art/{key}.png" 后调用本方法。
        /// </summary>
        public static Sprite LoadSprite(string relPath) => LoadSprite(relPath, true);

        /// <summary>logMissing=false：文件缺失时静默返回 null（多扩展名回落探测用，不算错误）。
        /// 2026-05-29 (Q3) 改造：先查 atlas 索引，命中切子矩形；未命中走旧单 PNG 路径。</summary>
        public static Sprite LoadSprite(string relPath, bool logMissing)
        {
            if (string.IsNullOrEmpty(relPath)) return null;
            if (_spriteCache.TryGetValue(relPath, out var cached) && cached != null) return cached;

            // ★ atlas 优先路径
            if (_atlasIndex.TryGetValue(relPath, out var entry))
            {
                var atlasTex = LoadAtlasTexture(entry.TextureRelPath);
                if (atlasTex != null)
                {
                    // Unity 的 Rect.y 从底部起算；atlas XML 的 y 从顶部起算 → 翻转
                    float unityY = atlasTex.height - entry.Y - entry.H;
                    var sub = Sprite.Create(atlasTex,
                        new Rect(entry.X, unityY, entry.W, entry.H),
                        new Vector2(entry.PivotX, entry.PivotY),
                        DEFAULT_PIXELS_PER_UNIT);
                    _spriteCache[relPath] = sub;
                    return sub;
                }
                // atlas 纹理加载失败 → 兜底走旧单 PNG（写日志但不退出）
                if (logMissing) Debug.LogWarning($"[ResourceHost] atlas key 命中但纹理加载失败 {relPath} → 兜底单 PNG");
            }

            // 单 PNG 路径（向后兼容）
            byte[] bytes = ReadBytes(relPath);
            if (bytes == null || bytes.Length == 0)
            {
                if (logMissing) Debug.LogWarning($"[ResourceHost] LoadSprite 失败: {relPath}");
                return null;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[ResourceHost] LoadSprite 解码失败: {relPath}");
                return null;
            }
            var sprite = Sprite.Create(tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                DEFAULT_PIXELS_PER_UNIT);
            _spriteCache[relPath] = sprite;
            return sprite;
        }

        /// <summary>清空 sprite 缓存（场景切换时可手动调用，避免内存增长）。同时清 atlas 纹理缓存。</summary>
        public static void ClearSpriteCache()
        {
            _spriteCache.Clear();
            _atlasTextureCache.Clear();
        }

        // ==================================================================
        // Atlas XML 索引（2026-05-29 Q3 新增）
        //
        // 约定:
        //   - Game/resources/art/atlas/<atlas_name>.xml + Game/resources/art/atlas/<atlas_name>.png
        //   - 启动期扫描所有 *.xml 建索引；LoadSprite 优先查 atlas
        //   - 未配 atlas 的 key 走单 PNG 兜底 → 双轨可共存
        //
        // XML 格式（HeroDefense 自定义 v1）:
        //   <Atlas name="hero_lv_bu" texture="resources/art/atlas/hero_lv_bu.png" width="2048" height="2048">
        //     <Frame key="resources/art/hero/lv_bu_idle_0.png" x="0"   y="0"   w="256" h="384" pivot_x="0.5" pivot_y="0" />
        //     <Frame key="resources/art/hero/lv_bu_idle_1.png" x="256" y="0"   w="256" h="384" pivot_x="0.5" pivot_y="0" />
        //     ...
        //   </Atlas>
        //
        // key 字段写**完整 relPath**（与 LoadSprite(relPath) 调用一致；含 "resources/art/" 前缀 + ".png" 后缀），
        // 不做字符串处理，最简单匹配。
        //
        // 坐标系: Frame 的 x/y 是 atlas image 中**从顶部起算**（TexturePacker 默认），
        //   ResourceHost 加载时自动翻转到 Unity Rect.y（底部起）。
        // ==================================================================

        private struct AtlasFrameEntry
        {
            public string AtlasName;
            public string TextureRelPath;
            public int X, Y, W, H;
            public float PivotX, PivotY;
        }

        private static readonly Dictionary<string, AtlasFrameEntry> _atlasIndex = new Dictionary<string, AtlasFrameEntry>();
        private static readonly Dictionary<string, Texture2D> _atlasTextureCache = new Dictionary<string, Texture2D>();

        /// <summary>已加载的 atlas 名称数 + frame key 总数（调试用）。</summary>
        public static int AtlasCount => _atlasTextureCache.Count;
        public static int AtlasFrameKeyCount => _atlasIndex.Count;

        /// <summary>扫 Game/resources/art/atlas/*.xml 建索引。Boot 自动调用。可被外部 reload。</summary>
        public static void LoadAtlasIndex()
        {
            _atlasIndex.Clear();
            // 注意：_atlasTextureCache 不清 — 复用已加载纹理
            var xmlFiles = EnumerateFiles("resources/art/atlas", "*.xml");
            if (xmlFiles == null || xmlFiles.Count == 0)
            {
                Debug.Log("[ResourceHost] resources/art/atlas/ 无 .xml 文件，atlas 路径未启用（单 PNG 兜底）");
                return;
            }
            int atlasOk = 0, atlasFail = 0;
            foreach (var xmlRelPath in xmlFiles)
            {
                try
                {
                    string xmlText = ReadText(xmlRelPath);
                    if (string.IsNullOrEmpty(xmlText)) { atlasFail++; continue; }
                    ParseAtlasXml(xmlText, xmlRelPath);
                    atlasOk++;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[ResourceHost] atlas xml 解析失败 {xmlRelPath}: {e.Message}");
                    atlasFail++;
                }
            }
            Debug.Log($"[ResourceHost] atlas 索引加载完毕: {_atlasIndex.Count} frame keys / {atlasOk} atlas xml" +
                      (atlasFail > 0 ? $" / {atlasFail} 失败" : ""));
        }

        private static void ParseAtlasXml(string xmlText, string sourcePath)
        {
            var doc = XDocument.Parse(xmlText);
            var atlasNode = doc.Root;
            if (atlasNode == null || atlasNode.Name.LocalName != "Atlas")
            {
                Debug.LogWarning($"[ResourceHost] atlas xml 根节点应为 <Atlas>: {sourcePath}");
                return;
            }
            string name = atlasNode.Attribute("name")?.Value ?? "?";
            string textureRelPath = atlasNode.Attribute("texture")?.Value;
            if (string.IsNullOrEmpty(textureRelPath))
            {
                Debug.LogWarning($"[ResourceHost] atlas {name} 缺 texture attr: {sourcePath}");
                return;
            }

            int frameCount = 0, dup = 0;
            foreach (var frameNode in atlasNode.Elements("Frame"))
            {
                var key = frameNode.Attribute("key")?.Value;
                if (string.IsNullOrEmpty(key)) continue;
                var entry = new AtlasFrameEntry
                {
                    AtlasName      = name,
                    TextureRelPath = textureRelPath,
                    X              = ParseIntAttr(frameNode.Attribute("x"), 0),
                    Y              = ParseIntAttr(frameNode.Attribute("y"), 0),
                    W              = ParseIntAttr(frameNode.Attribute("w"), 0),
                    H              = ParseIntAttr(frameNode.Attribute("h"), 0),
                    PivotX         = ParseFloatAttr(frameNode.Attribute("pivot_x"), 0.5f),
                    PivotY         = ParseFloatAttr(frameNode.Attribute("pivot_y"), 0.5f),
                };
                if (_atlasIndex.ContainsKey(key)) dup++;
                _atlasIndex[key] = entry;  // 后者覆盖前者
                frameCount++;
            }
            Debug.Log($"[ResourceHost]   atlas {name}: {frameCount} frames" + (dup > 0 ? $"（其中 {dup} 个 key 与已加载 atlas 重复，已覆盖）" : ""));
        }

        private static int ParseIntAttr(XAttribute attr, int def)
        {
            if (attr == null) return def;
            return int.TryParse(attr.Value, out var v) ? v : def;
        }

        private static float ParseFloatAttr(XAttribute attr, float def)
        {
            if (attr == null) return def;
            return float.TryParse(attr.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        private static Texture2D LoadAtlasTexture(string textureRelPath)
        {
            if (_atlasTextureCache.TryGetValue(textureRelPath, out var cached) && cached != null) return cached;
            byte[] bytes = ReadBytes(textureRelPath);
            if (bytes == null || bytes.Length == 0)
            {
                Debug.LogWarning($"[ResourceHost] atlas texture 找不到: {textureRelPath}");
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning($"[ResourceHost] atlas texture 解码失败: {textureRelPath}");
                return null;
            }
            _atlasTextureCache[textureRelPath] = tex;
            return tex;
        }

        /// <summary>枚举相对目录下匹配 pattern 的文件（不递归）。</summary>
        public static List<string> EnumerateFiles(string relDir, string searchPattern)
        {
            return EnumerateFiles(relDir, searchPattern, SearchOption.TopDirectoryOnly);
        }

        /// <summary>
        /// 枚举相对目录下的文件，可选递归。在 WebGL 等 Directory.GetFiles 受限平台上，
        /// 会优先读 manifest 清单筛选；manifest 缺失时回落到目录扫描（若底层 API 不支持则返回空）。
        /// 返回相对 baseDir 的路径，'/' 分隔。
        /// </summary>
        public static List<string> EnumerateFiles(string relDir, string searchPattern, SearchOption option)
        {
            EnsureBooted();
            var result = new List<string>();
            if (string.IsNullOrEmpty(searchPattern)) searchPattern = "*";
            string normDir = NormalizeRelDir(relDir);

            if (_manifest != null)
            {
                FilterFromManifest(normDir, searchPattern, option, result);
                if (result.Count > 0) return result;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            Debug.LogWarning($"[ResourceHost] EnumerateFiles({relDir}, {searchPattern}): WebGL 下必须依赖 manifest.json，当前未加载到条目。");
            return result;
#else
            string primary = ResolvePath(normDir, _baseDir);
            TryEnumerateDirectory(primary, _baseDir, searchPattern, option, result);
            if (result.Count == 0 && !string.IsNullOrEmpty(_fallbackBaseDir))
            {
                string fb = ResolvePath(normDir, _fallbackBaseDir);
                TryEnumerateDirectory(fb, _fallbackBaseDir, searchPattern, option, result);
            }
            return result;
#endif
        }

        private static void TryEnumerateDirectory(string fullDir, string rootDir, string pattern, SearchOption option, List<string> result)
        {
            if (string.IsNullOrEmpty(fullDir) || !Directory.Exists(fullDir)) return;
            try
            {
                foreach (var file in Directory.GetFiles(fullDir, pattern, option))
                {
                    string rel = MakeRelative(file, rootDir);
                    if (!string.IsNullOrEmpty(rel)) result.Add(rel);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ResourceHost] EnumerateFiles 扫描失败 {fullDir}: {e.Message}");
            }
        }

        // ==================================================================
        // manifest 管理
        // ==================================================================

        public static bool TryLoadManifest()
        {
            EnsureBooted();
            _manifest = null;

            string primary = ResolvePath("manifest.json", _baseDir);
            string chosen = null;
            if (!string.IsNullOrEmpty(primary) && File.Exists(primary)) chosen = primary;
            else if (!string.IsNullOrEmpty(_fallbackBaseDir))
            {
                string fb = ResolvePath("manifest.json", _fallbackBaseDir);
                if (!string.IsNullOrEmpty(fb) && File.Exists(fb)) chosen = fb;
            }
            if (chosen == null) return false;

            try
            {
                string json = File.ReadAllText(chosen);
                _manifest = ParseManifestJson(json);
                Debug.Log($"[ResourceHost] manifest.json 已加载（{_manifest.Count} 条目），来源: {chosen}");
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ResourceHost] manifest.json 解析失败: {e.Message}");
                _manifest = null;
                return false;
            }
        }

        private static bool ManifestHasPrefix(string prefix)
        {
            if (_manifest == null) return false;
            foreach (var k in _manifest.Keys)
                if (k.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void FilterFromManifest(string normDir, string pattern, SearchOption option, List<string> result)
        {
            string prefix = string.IsNullOrEmpty(normDir) ? "" : normDir + "/";
            foreach (var kv in _manifest)
            {
                string path = kv.Key;
                if (!string.IsNullOrEmpty(prefix) && !path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;

                string tail = string.IsNullOrEmpty(prefix) ? path : path.Substring(prefix.Length);
                if (option == SearchOption.TopDirectoryOnly && tail.Contains("/")) continue;

                string leafName = Path.GetFileName(tail);
                if (!WildcardMatch(leafName, pattern)) continue;

                result.Add(path);
            }
        }

        /// <summary>极简通配符匹配：仅支持 * 和 ?（大小写不敏感），够用于 *.txt / *.lua 场景。</summary>
        private static bool WildcardMatch(string name, string pattern)
        {
            if (pattern == "*" || pattern == "*.*") return true;
            int ni = 0, pi = 0;
            int starN = -1, starP = -1;
            while (ni < name.Length)
            {
                if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(name[ni])))
                {
                    ni++; pi++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    starP = pi++;
                    starN = ni;
                }
                else if (starP != -1)
                {
                    pi = starP + 1;
                    ni = ++starN;
                }
                else return false;
            }
            while (pi < pattern.Length && pattern[pi] == '*') pi++;
            return pi == pattern.Length;
        }

        private static Dictionary<string, ManifestFileEntry> ParseManifestJson(string json)
        {
            var dict = new Dictionary<string, ManifestFileEntry>();
            if (string.IsNullOrEmpty(json)) return dict;

            int i = 0;
            while (i < json.Length)
            {
                int braceOpen = json.IndexOf('{', i);
                if (braceOpen < 0) break;
                int braceClose = json.IndexOf('}', braceOpen + 1);
                if (braceClose < 0) break;
                string segment = json.Substring(braceOpen + 1, braceClose - braceOpen - 1);
                string path = ExtractJsonString(segment, "path");
                string hash = ExtractJsonString(segment, "hash");
                long size = ExtractJsonLong(segment, "size");
                if (!string.IsNullOrEmpty(path) && !path.Contains("{"))
                {
                    dict[path.Replace('\\', '/')] = new ManifestFileEntry { path = path, hash = hash ?? "", size = size };
                }
                i = braceClose + 1;
            }
            return dict;
        }

        private static string ExtractJsonString(string segment, string key)
        {
            string needle = "\"" + key + "\"";
            int k = segment.IndexOf(needle, System.StringComparison.Ordinal);
            if (k < 0) return null;
            int colon = segment.IndexOf(':', k + needle.Length);
            if (colon < 0) return null;
            int q1 = segment.IndexOf('"', colon + 1);
            if (q1 < 0) return null;
            int q2 = segment.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return segment.Substring(q1 + 1, q2 - q1 - 1);
        }

        private static long ExtractJsonLong(string segment, string key)
        {
            string needle = "\"" + key + "\"";
            int k = segment.IndexOf(needle, System.StringComparison.Ordinal);
            if (k < 0) return 0;
            int colon = segment.IndexOf(':', k + needle.Length);
            if (colon < 0) return 0;
            int start = colon + 1;
            while (start < segment.Length && (segment[start] == ' ' || segment[start] == '\t')) start++;
            int end = start;
            while (end < segment.Length && (char.IsDigit(segment[end]) || segment[end] == '-')) end++;
            if (end == start) return 0;
            long.TryParse(segment.Substring(start, end - start), out long v);
            return v;
        }

        public struct ManifestFileEntry
        {
            public string path;
            public string hash;
            public long size;
        }

        // ==================================================================
        // 路径辅助
        // ==================================================================

        private static string NormalizeRelDir(string relDir)
        {
            if (string.IsNullOrEmpty(relDir)) return "";
            return relDir.Replace('\\', '/').Trim('/');
        }

        private static string ResolvePath(string relPath, string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir)) return null;
            relPath = relPath?.TrimStart('/', '\\') ?? "";
            return Path.Combine(rootDir, relPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string MakeRelative(string fullPath, string rootDir)
        {
            if (string.IsNullOrEmpty(rootDir) || string.IsNullOrEmpty(fullPath)) return null;
            if (fullPath.Length <= rootDir.Length) return null;
            string rel = fullPath.Substring(rootDir.Length).TrimStart(Path.DirectorySeparatorChar, '/');
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
