using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace HeroDefense.Editor
{
    /// <summary>
    /// Step 11 优化 #1：Sprite Atlas 严打包（5 组）。
    ///
    /// 创建 5 个 SpriteAtlas asset，落在 Assets/Atlases/：
    ///   - unit_atlas     友军（兵种/武将/建筑） 256×256
    ///   - monster_atlas  怪物                    256×256
    ///   - ui_atlas       UI（icon/btn/panel/frame） 1024×1024
    ///   - shadow_atlas   阴影（单色椭圆等）        128×128
    ///   - vfx_atlas      特效粒子                  512×512
    ///
    /// 设计原则（v3 design.md §3）：
    ///   - 256×256 单位 / 64×64 卡 icon / ASTC 6×6 压缩（WebGL 走 ETC2 + 灰度回退）
    ///   - 不 assign sprites（Phase 1 美术资源未就位）— 仅创建 asset + 配置打包参数
    ///   - includeInBuild=true 让 Player Build 把 atlas 打进包
    ///   - Phase 2 美术就位后用 manage_asset modify 把 sprites 拖入 packables[]
    /// </summary>
    public static class HDAtlasBuilder
    {
        private const string AtlasDir = "Assets/Atlases";

        private static readonly (string name, int maxSize)[] Specs = new (string, int)[]
        {
            ("unit_atlas",    1024),  // 256×256 × N 单位
            ("monster_atlas", 1024),  // 256×256 × N 怪
            ("ui_atlas",      2048),  // UI 资源最多，单图 64-256
            ("shadow_atlas",   512),  // 阴影 4-8 张
            ("vfx_atlas",     1024),  // VFX 粒子 sprite
        };

        [MenuItem("Tools/HeroDefense/Build Sprite Atlases")]
        public static void BuildAll()
        {
            EnsureAtlasDir();

            int created = 0;
            int existed = 0;
            foreach (var (n, maxSize) in Specs)
            {
                string path = $"{AtlasDir}/{n}.spriteatlasv2";
                // v2 atlas (SpriteAtlasAsset) preferred since Unity 2021.2+。Tuanjie 1.8.4 兼容。
                if (File.Exists(path))
                {
                    existed++;
                    continue;
                }

                // 创建空 SpriteAtlasAsset → 写盘 → 经 Importer 配置（Unity 2022+ 推荐路径）
                var atlas = new SpriteAtlasAsset();
                SpriteAtlasAsset.Save(atlas, path);
                AssetDatabase.ImportAsset(path);

                var importer = AssetImporter.GetAtPath(path) as SpriteAtlasImporter;
                if (importer != null)
                {
                    var packSettings = new SpriteAtlasPackingSettings
                    {
                        blockOffset = 1,
                        enableRotation = false,
                        enableTightPacking = false,
                        padding = 4
                    };
                    var textureSettings = new SpriteAtlasTextureSettings
                    {
                        readable = false,
                        generateMipMaps = false,
                        sRGB = true,
                        filterMode = FilterMode.Bilinear
                    };
                    var platformSettings = new TextureImporterPlatformSettings
                    {
                        name = "WebGL",
                        maxTextureSize = maxSize,
                        format = TextureImporterFormat.ASTC_6x6,
                        compressionQuality = (int)UnityEditor.TextureCompressionQuality.Normal,
                        overridden = true
                    };
                    importer.packingSettings = packSettings;
                    importer.textureSettings = textureSettings;
                    importer.SetPlatformSettings(platformSettings);
                    importer.includeInBuild = true;
                    EditorUtility.SetDirty(importer);
                    importer.SaveAndReimport();
                }
                Debug.Log($"[HDAtlasBuilder] 创建 atlas: {path} (maxSize={maxSize})");
                created++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[HDAtlasBuilder] 完成。新建 {created} 个，已存在 {existed} 个，目录 {AtlasDir}/");
        }

        private static void EnsureAtlasDir()
        {
            if (!Directory.Exists(AtlasDir))
            {
                Directory.CreateDirectory(AtlasDir);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>报告（菜单：Tools/HeroDefense/List Sprite Atlases）— 列出所有 atlas 当前 packable 数。</summary>
        [MenuItem("Tools/HeroDefense/List Sprite Atlases")]
        public static void ListAll()
        {
            if (!Directory.Exists(AtlasDir))
            {
                Debug.LogWarning($"[HDAtlasBuilder] 目录不存在: {AtlasDir}（先跑 Build Sprite Atlases）");
                return;
            }
            var guids = AssetDatabase.FindAssets("t:SpriteAtlas", new[] { AtlasDir });
            var v2Guids = AssetDatabase.FindAssets("t:SpriteAtlasAsset", new[] { AtlasDir });
            int total = guids.Length + v2Guids.Length;
            Debug.Log($"[HDAtlasBuilder] {AtlasDir}/ 共 {total} 个 atlas（v1+v2）");
            var seen = new HashSet<string>();
            foreach (var g in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (seen.Add(p)) Debug.Log($"  {p}");
            }
            foreach (var g in v2Guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (seen.Add(p)) Debug.Log($"  {p}");
            }
        }
    }
}
