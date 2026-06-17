using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI
{
    /// <summary>
    /// 静态 helper：按 ui_asset.txt id 一行调用完成 sprite + tint + 子 Text 设置。
    /// 业务代码统一通过此 helper 设置 UI 视觉，避免重复写 ConfigManager / LoadSprite / ParseColor 模板代码。
    /// </summary>
    public static class UiAssetHelper
    {
        // hotfix-2：placeholder 缓存（避免重复生成同一 spriteId 的 Texture）
        static readonly Dictionary<string, Sprite> _placeholderCache = new Dictionary<string, Sprite>();

        /// <summary>从 ui_asset.txt 取一行（id, sprite_key, tint_color, text）。</summary>
        public static AssetEntry Lookup(string assetId)
        {
            var entry = new AssetEntry { id = assetId };
            var cm = ConfigManager.Instance;
            if (cm == null) return entry;
            var row = cm.GetTableInfo("ui_asset", "id", assetId);
            if (row == null) { return entry; }   // hotfix-2：删 LogWarning（缺资源走 placeholder，不刷屏）
            entry.spriteKey = cm.GetValue(row, "sprite_key", "");
            entry.tintColor = ParseHexColor(cm.GetValue(row, "tint_color", ""));
            entry.text = cm.GetValue(row, "text", "");
            return entry;
        }

        /// <summary>把 ui_asset 应用到 Image（设 sprite + color）。返回是否成功；hotfix-2：失败时 fallback 到 placeholder。</summary>
        public static bool ApplyToImage(Image img, string assetId)
        {
            if (img == null) return false;
            var entry = Lookup(assetId);
            Sprite sprite = null;
            if (!string.IsNullOrEmpty(entry.spriteKey))
                sprite = ResourceHost.LoadSprite("resources/art/" + entry.spriteKey);

            if (sprite == null)
            {
                // hotfix-2：找不到资源 → 生成程序化 placeholder（颜色 hash 自 assetId）
                sprite = GetOrCreatePlaceholderSprite(assetId);
                img.sprite = sprite;
                img.color = entry.tintColor.HasValue ? entry.tintColor.Value : Color.white;
                return true; // 仍返回 true（已显示 placeholder）
            }
            img.sprite = sprite;
            img.color = entry.tintColor.HasValue ? entry.tintColor.Value : Color.white;
            return true;
        }

        /// <summary>把 ui_asset 应用到 SpriteRenderer。hotfix-2：失败时 fallback 到 placeholder。</summary>
        public static bool ApplyToSpriteRenderer(SpriteRenderer sr, string assetId)
        {
            if (sr == null) return false;
            var entry = Lookup(assetId);
            Sprite sprite = null;
            if (!string.IsNullOrEmpty(entry.spriteKey))
                sprite = ResourceHost.LoadSprite("resources/art/" + entry.spriteKey);
            if (sprite == null) sprite = GetOrCreatePlaceholderSprite(assetId);
            sr.sprite = sprite;
            sr.color = entry.tintColor.HasValue ? entry.tintColor.Value : Color.white;
            return true;
        }

        /// <summary>
        /// hotfix-2：按 spriteId 生成 32×32 彩色 placeholder Sprite（颜色 hash 自 spriteId 的可读 family）。
        /// 关键策略：
        ///   - 法师 mage 蓝色 (#4080E0)，等级越高越深
        ///   - 射手 archer 绿色 (#40C060)
        ///   - 剑客 swordsman 红色 (#E06040)
        ///   - 神话 mythic / hero（含 ice/wanderer/idol）金色 #E0B040
        ///   - empty_cell 空格占位 灰色 #909090
        ///   - default / 其他 紫色 #7B5DC6
        ///   - 内含 1 px 白色边框，让多格 sprite 之间可分辨
        /// 缓存：同 spriteId 复用，避免每帧生成。
        /// </summary>
        public static Sprite GetOrCreatePlaceholderSprite(string spriteId)
        {
            if (_placeholderCache.TryGetValue(spriteId ?? "", out var cached) && cached != null)
                return cached;

            Color baseColor = ResolveColorForSpriteId(spriteId);
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PH_" + (spriteId ?? "null");

            var border = new Color(1f, 1f, 1f, 0.85f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    bool onBorder = (x == 0 || y == 0 || x == size - 1 || y == size - 1);
                    tex.SetPixel(x, y, onBorder ? border : baseColor);
                }
            }
            tex.Apply();

            var spr = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            spr.name = "PH_" + (spriteId ?? "null");
            _placeholderCache[spriteId ?? ""] = spr;
            return spr;
        }

        /// <summary>把 spriteId 映射到 placeholder 基础颜色（family 识别）。</summary>
        static Color ResolveColorForSpriteId(string spriteId)
        {
            if (string.IsNullOrEmpty(spriteId))
                return new Color(0.48f, 0.36f, 0.78f); // 默认紫
            string id = spriteId.ToLowerInvariant();
            // 法师 — 蓝色，按等级加深
            if (id.Contains("mage"))
            {
                int lv = ExtractLv(id);
                return DeepenWithLevel(new Color(0.25f, 0.50f, 0.88f), lv);
            }
            if (id.Contains("archer"))
            {
                int lv = ExtractLv(id);
                return DeepenWithLevel(new Color(0.25f, 0.75f, 0.38f), lv);
            }
            if (id.Contains("swordsman") || id.Contains("knight"))
            {
                int lv = ExtractLv(id);
                return DeepenWithLevel(new Color(0.88f, 0.38f, 0.25f), lv);
            }
            if (id.Contains("ice_queen") || id.Contains("wanderer") || id.Contains("idol_girl") || id.StartsWith("icon_hero_"))
            {
                // 神话英雄 — 金色
                return new Color(0.88f, 0.69f, 0.25f);
            }
            if (id.Contains("empty_cell"))
            {
                return new Color(0.56f, 0.56f, 0.56f); // empty_cell 空格占位 灰色
            }
            if (id.Contains("default") || id.Contains("frame_purple"))
            {
                return new Color(0.48f, 0.36f, 0.78f); // 紫
            }
            // 兜底：根据字符串 hash 给一个可重现的随机色（饱和度低）
            int h = spriteId.GetHashCode();
            float r = ((h & 0xFF) / 255f) * 0.5f + 0.30f;
            float g = (((h >> 8) & 0xFF) / 255f) * 0.5f + 0.30f;
            float b = (((h >> 16) & 0xFF) / 255f) * 0.5f + 0.30f;
            return new Color(r, g, b);
        }

        /// <summary>从 spriteId 中提取 lv 数字（如 _lv1 / _lv4）。无则返回 1。</summary>
        static int ExtractLv(string id)
        {
            int idx = id.IndexOf("_lv");
            if (idx < 0) return 1;
            int p = idx + 3;
            if (p >= id.Length) return 1;
            int n = 0; bool any = false;
            while (p < id.Length && id[p] >= '0' && id[p] <= '9')
            {
                n = n * 10 + (id[p] - '0'); p++; any = true;
            }
            return any ? n : 1;
        }

        /// <summary>等级越高色越深（lv 1 ~ 4 → 0% ~ 60% 暗化）。</summary>
        static Color DeepenWithLevel(Color c, int lv)
        {
            float t = Mathf.Clamp01((lv - 1) / 3f) * 0.55f; // lv1=0%, lv2=18%, lv3=37%, lv4=55%
            return new Color(c.r * (1f - t), c.g * (1f - t), c.b * (1f - t), c.a);
        }

        /// <summary>解析 #RRGGBB / #RRGGBBAA hex 字符串为 Color；空或非法返回 null。</summary>
        public static Color? ParseHexColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 && hex.Length != 8) return null;
            try
            {
                byte r = System.Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = System.Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = System.Convert.ToByte(hex.Substring(4, 2), 16);
                byte a = hex.Length == 8 ? System.Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;
                return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            }
            catch { return null; }
        }

        public struct AssetEntry
        {
            public string id;
            public string spriteKey;
            public Color? tintColor;  // null = 不 tint（用 white）
            public string text;       // "" = 不加子 Text
        }
    }
}
