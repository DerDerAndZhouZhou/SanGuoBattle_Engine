using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Config;
using HeroDefense.Engine.Host;

namespace HeroDefense.UI
{
    /// <summary>
    /// 静态 helper：按 ui_asset.tab id 一行调用完成 sprite + tint + 子 Text 设置。
    /// 业务代码统一通过此 helper 设置 UI 视觉，避免重复写 ConfigManager / LoadSprite / ParseColor 模板代码。
    /// </summary>
    public static class UiAssetHelper
    {
        // placeholder 缓存，避免重复生成同一 spriteId 的 Texture。
        static readonly Dictionary<string, Sprite> _placeholderCache = new Dictionary<string, Sprite>();

        /// <summary>从 ui_asset.tab 取一行（id, sprite_key, tint_color, text）。</summary>
        public static AssetEntry Lookup(string assetId)
        {
            var entry = new AssetEntry { id = assetId };
            var cm = ConfigManager.Instance;
            if (cm == null) return entry;
            var row = cm.GetTableInfo("ui_asset", "id", assetId);
            if (row == null) return entry;
            entry.spriteKey = cm.GetValue(row, "sprite_key", "");
            entry.tintColor = ParseHexColor(cm.GetValue(row, "tint_color", ""));
            entry.text = cm.GetValue(row, "text", "");
            return entry;
        }

        /// <summary>把 ui_asset 应用到 Image；缺图时显示稳定 placeholder。</summary>
        public static bool ApplyToImage(Image img, string assetId)
        {
            if (img == null) return false;
            var entry = Lookup(assetId);
            Sprite sprite = null;
            if (!string.IsNullOrEmpty(entry.spriteKey))
                sprite = ResourceHost.LoadSprite("resources/art/" + entry.spriteKey);

            if (sprite == null)
            {
                sprite = GetOrCreatePlaceholderSprite(assetId);
                img.sprite = sprite;
                img.color = entry.tintColor.HasValue ? entry.tintColor.Value : Color.white;
                return true;
            }
            img.sprite = sprite;
            img.color = entry.tintColor.HasValue ? entry.tintColor.Value : Color.white;
            return true;
        }

        /// <summary>把 ui_asset 应用到 SpriteRenderer；缺图时显示稳定 placeholder。</summary>
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
        /// 按 spriteId 生成 32×32 彩色 placeholder。
        /// 武将、兵种、建筑和 UI 使用不同色族，并保留 1 像素边框。
        /// </summary>
        public static Sprite GetOrCreatePlaceholderSprite(string spriteId)
        {
            if (_placeholderCache.TryGetValue(spriteId ?? "", out var cached) && cached != null)
                return cached;

            Color baseColor = ResolveColorForSpriteId(spriteId);
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
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
            if (id.Contains("hero") || id.Contains("portrait"))
                return new Color(0.88f, 0.69f, 0.25f);
            if (id.Contains("troop"))
                return new Color(0.31f, 0.56f, 0.38f);
            if (id.Contains("building") || id.Contains("camp")
                    || id.Contains("tower") || id.Contains("barricade"))
                return new Color(0.55f, 0.37f, 0.24f);
            if (id.Contains("button") || id.Contains("btn"))
                return new Color(0.58f, 0.25f, 0.21f);
            if (id.Contains("frame") || id.Contains("banner"))
                return new Color(0.75f, 0.58f, 0.30f);
            if (id.Contains("empty"))
                return new Color(0.56f, 0.56f, 0.56f);

            // 兜底：根据字符串 hash 生成可重现的低饱和色。
            int h = spriteId.GetHashCode();
            float r = ((h & 0xFF) / 255f) * 0.5f + 0.30f;
            float g = (((h >> 8) & 0xFF) / 255f) * 0.5f + 0.30f;
            float b = (((h >> 16) & 0xFF) / 255f) * 0.5f + 0.30f;
            return new Color(r, g, b);
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
