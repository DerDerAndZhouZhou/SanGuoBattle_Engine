using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Config
{
    /// <summary>
    /// 配置表枚举注册中心
    /// 从 Enum.tab 加载枚举定义，供 TabParser 解析 Enum_ 类型字段时使用。
    ///
    /// Enum.tab 格式（固定3列）:
    ///   EnumType    EnumName    EnumValue
    ///   string      string      int
    ///   (默认值行)
    ///   TOWER_TYPE  ttArcher    1
    ///   TOWER_TYPE  ttMage      2
    ///   ENEMY_TYPE  etGrunt     1
    ///   ENEMY_TYPE  etElite     2
    ///
    /// 配置表中使用:
    ///   类型填 Enum_TOWER_TYPE，数据填 ttArcher → 解析为 int 1
    /// </summary>
    public static class EnumRegistry
    {
        private static readonly Dictionary<string, Dictionary<string, int>> _enums
            = new Dictionary<string, Dictionary<string, int>>();

        private static readonly Dictionary<string, Dictionary<int, string>> _enumReverse
            = new Dictionary<string, Dictionary<int, string>>();

        private static bool _loaded;

        public static bool IsLoaded => _loaded;

        public static void LoadIfNeeded()
        {
            if (_loaded) return;
            Load();
        }

        public static void Load()
        {
            _enums.Clear();
            _enumReverse.Clear();

            var text = ResourceHost.ReadText("settings/Enum.tab");
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogError("[EnumRegistry] Enum.tab 未找到（这是严重的配置缺失：所有 Enum_ 类型字段将解析为 0，Lua 侧枚举常量也不会注入）。请检查 Game/settings/Enum.tab 是否存在。");
                return;
            }

            var rows = TabParser.Parse(text);
            int count = 0;

            foreach (var row in rows)
            {
                string enumType = row.ContainsKey("EnumType") ? row["EnumType"]?.ToString() : "";
                string enumName = row.ContainsKey("EnumName") ? row["EnumName"]?.ToString() : "";
                int enumValue = 0;
                if (row.ContainsKey("EnumValue"))
                {
                    var val = row["EnumValue"];
                    if (val is int iv) enumValue = iv;
                    else int.TryParse(val?.ToString(), out enumValue);
                }

                if (string.IsNullOrEmpty(enumType) || string.IsNullOrEmpty(enumName))
                    continue;

                if (!_enums.ContainsKey(enumType))
                    _enums[enumType] = new Dictionary<string, int>();
                _enums[enumType][enumName] = enumValue;

                if (!_enumReverse.ContainsKey(enumType))
                    _enumReverse[enumType] = new Dictionary<int, string>();
                _enumReverse[enumType][enumValue] = enumName;

                count++;
            }

            PopulateStaticFields();

            _loaded = true;
            Debug.Log($"[EnumRegistry] 加载 {_enums.Count} 种枚举, {count} 个值");
        }

        private const string ENUM_CONTAINER_NAMESPACE = "HeroDefense.Config";

        /// <summary>
        /// 按 EnumType 名反射查找同名 static class 容器，把每个 EnumName 对应的值写入同名 static int 字段。
        /// 容器声明在 Scripts/Config/Generated/ConfigEnums.cs 中。
        /// </summary>
        private static void PopulateStaticFields()
        {
            var asm = typeof(EnumRegistry).Assembly;

            foreach (var typeEntry in _enums)
            {
                string containerName = $"{ENUM_CONTAINER_NAMESPACE}.{typeEntry.Key}";
                var containerType = asm.GetType(containerName);
                if (containerType == null)
                {
                    Debug.LogWarning($"[EnumRegistry] 未找到枚举容器类型: {containerName}（请在 ConfigEnums.cs 添加对应 static class）");
                    continue;
                }

                foreach (var pair in typeEntry.Value)
                {
                    var field = containerType.GetField(pair.Key, BindingFlags.Public | BindingFlags.Static);
                    if (field == null)
                    {
                        Debug.LogWarning($"[EnumRegistry] {containerName} 缺少字段 {pair.Key}（请同步 ConfigEnums.cs）");
                        continue;
                    }

                    if (field.FieldType != typeof(int))
                    {
                        Debug.LogWarning($"[EnumRegistry] {containerName}.{pair.Key} 类型不是 int，跳过");
                        continue;
                    }

                    field.SetValue(null, pair.Value);
                }
            }
        }

        public static int GetValue(string enumType, string enumName, int defaultValue = 0)
        {
            if (_enums.TryGetValue(enumType, out var map))
                if (map.TryGetValue(enumName, out int val))
                    return val;

            Debug.LogWarning($"[EnumRegistry] 未找到枚举: {enumType}.{enumName}");
            return defaultValue;
        }

        public static string GetName(string enumType, int enumValue, string defaultName = "")
        {
            if (_enumReverse.TryGetValue(enumType, out var map))
                if (map.TryGetValue(enumValue, out string name))
                    return name;
            return defaultName;
        }

        public static bool HasEnumType(string enumType)
        {
            return _enums.ContainsKey(enumType);
        }

        public static bool IsEnumType(string typeStr)
        {
            return typeStr != null && typeStr.StartsWith("Enum_");
        }

        public static string ExtractEnumType(string typeStr)
        {
            return typeStr.Substring(5);
        }

        public static Dictionary<string, int> GetAllValues(string enumType)
        {
            _enums.TryGetValue(enumType, out var map);
            return map;
        }

#if XLUA
        /// <summary>
        /// 把已加载的枚举注入 Lua 全局，使 Lua 侧可直接用 TOWER_TYPE.ttArcher。
        /// 必须在 LuaEnv 创建后、require('main') 之前调用。
        /// </summary>
        public static void InjectToLua(LuaEnv luaEnv)
        {
            if (luaEnv == null || _enums.Count == 0) return;

            var sb = new StringBuilder();
            foreach (var kv in _enums)
            {
                sb.Append(kv.Key).Append(" = {");
                bool first = true;
                foreach (var pair in kv.Value)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(pair.Key).Append('=').Append(pair.Value);
                }
                sb.Append("}\n");
            }

            luaEnv.DoString(sb.ToString(), "EnumRegistry.InjectToLua");
            Debug.Log($"[EnumRegistry] 已向 Lua 注入 {_enums.Count} 个枚举表");
        }
#endif
    }
}
