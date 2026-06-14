using System.Collections.Generic;
using UnityEngine;
#if XLUA
using XLua;
#endif

namespace HeroDefense.Config
{
    /// <summary>
    /// 配置表 Lua 桥接
    ///
    /// Lua 用法:
    ///   local cfg = CS.HeroDefense.Config.ConfigBridge
    ///
    ///   local allTowers = cfg.GetTableList("tower")
    ///   local archer = cfg.GetTableInfo("tower", "id", "archer_01")
    ///   print(archer.name, archer.damage)
    ///
    ///   local rangedTowers = cfg.GetTableInfoList("tower", "category", "ranged")
    ///
    ///   local filter = { category = "ranged", tier = 2 }
    ///   local result = cfg.GetTableInfoMulti("tower", filter)
    /// </summary>
#if XLUA
    [LuaCallCSharp]
#endif
    public static class ConfigBridge
    {
        public static void LoadAll()
        {
            ConfigManager.Instance.Load();
        }

        public static void Reload(string tableName)
        {
            ConfigManager.Instance.Reload(tableName);
        }

        public static List<Dictionary<string, object>> GetTableList(string tableName)
        {
            return ConfigManager.Instance.GetTableList(tableName);
        }

        public static Dictionary<string, object> GetTableInfo(string tableName, string field, string value)
        {
            return ConfigManager.Instance.GetTableInfo(tableName, field, value);
        }

        public static List<Dictionary<string, object>> GetTableInfoList(string tableName, string field, string value)
        {
            return ConfigManager.Instance.GetTableInfoList(tableName, field, value);
        }

        public static Dictionary<string, object> GetTableInfoMulti(string tableName, Dictionary<string, object> conditions)
        {
            return ConfigManager.Instance.GetTableInfo(tableName, conditions);
        }

        public static List<Dictionary<string, object>> GetTableInfoListMulti(string tableName, Dictionary<string, object> conditions)
        {
            return ConfigManager.Instance.GetTableInfoList(tableName, conditions);
        }

        // ======== 枚举查询 ========

        public static int GetEnumValue(string enumType, string enumName)
        {
            return EnumRegistry.GetValue(enumType, enumName);
        }

        public static string GetEnumName(string enumType, int enumValue)
        {
            return EnumRegistry.GetName(enumType, enumValue);
        }

        // ======== 通用 ========

        public static int GetRowCount(string tableName)
        {
            var list = ConfigManager.Instance.GetTableList(tableName);
            return list?.Count ?? 0;
        }

        // ======== 行内字段访问（Lua 侧 [] / . 在 generic Dictionary 上不可用）========

        public static object GetFieldRaw(Dictionary<string, object> row, string field)
        {
            if (row == null || string.IsNullOrEmpty(field)) return null;
            return row.TryGetValue(field, out var v) ? v : null;
        }

        public static int GetFieldInt(Dictionary<string, object> row, string field, int defaultValue = 0)
        {
            if (row == null || !row.TryGetValue(field, out var v) || v == null) return defaultValue;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is float f) return (int)f;
            if (v is double d) return (int)d;
            return int.TryParse(v.ToString(), out var r) ? r : defaultValue;
        }

        public static float GetFieldFloat(Dictionary<string, object> row, string field, float defaultValue = 0f)
        {
            if (row == null || !row.TryGetValue(field, out var v) || v == null) return defaultValue;
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is int i) return i;
            if (v is long l) return l;
            return float.TryParse(v.ToString(), out var r) ? r : defaultValue;
        }

        public static string GetFieldString(Dictionary<string, object> row, string field, string defaultValue = "")
        {
            if (row == null || !row.TryGetValue(field, out var v) || v == null) return defaultValue;
            return v.ToString();
        }

        public static bool GetFieldBool(Dictionary<string, object> row, string field, bool defaultValue = false)
        {
            if (row == null || !row.TryGetValue(field, out var v) || v == null) return defaultValue;
            if (v is bool b) return b;
            var s = v.ToString();
            return s == "1" || s.Equals("true", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 读取 int[] 字段，返回 List&lt;int&gt;（xLua 友好，可直接 ipairs）。
        /// TabParser 对 int[] 字段会解析为 int[]；本 helper 把 int[] / List&lt;int&gt; / 单值 int 统一转 List&lt;int&gt;。
        /// 字段缺失或 null → 返回空 list（非 null，便于 Lua 侧 #arr 判空）。
        /// </summary>
        public static List<int> GetFieldIntArray(Dictionary<string, object> row, string field)
        {
            var result = new List<int>();
            if (row == null || !row.TryGetValue(field, out var v) || v == null) return result;
            if (v is int[] ia)
            {
                result.Capacity = ia.Length;
                for (int i = 0; i < ia.Length; i++) result.Add(ia[i]);
                return result;
            }
            if (v is List<int> li) return new List<int>(li);
            if (v is int single) { result.Add(single); return result; }
            // 兜底：字符串形式 "1,2,3"
            var s = v.ToString();
            if (string.IsNullOrEmpty(s)) return result;
            foreach (var part in s.Split(','))
            {
                if (int.TryParse(part.Trim(), out int parsed)) result.Add(parsed);
            }
            return result;
        }

        // ============ Phase 2.5 json 列读取（shape_mask 等嵌套数组）============
        //
        // TabParser 对 type=json 字段直接存 raw string（见 TabParser.ParseValue case "json"）。
        // shape_mask 形如 "[[1,1],[1,0]]" — Lua 侧需 List<List<int>>（xLua 自动转 nested Lua table）。
        // 本 helper 用最简单的递归 JSON 数组解析（不支持对象/字符串/转义，因为 shape_mask 只含 int / [ / ] / ,）。
        //
        // 返回类型：
        //   - 顶层是数组 → List&lt;object&gt; 每元素是 int 或 List&lt;object&gt;（嵌套数组）
        //   - null 或解析失败 → null
        //
        // Lua 端用法：
        //   local mask = Row_GetTable(row, "shape_mask")  -- 返回嵌套 Lua table
        //   for _, row_arr in ipairs(mask) do
        //       for _, v in ipairs(row_arr) do ... end
        //   end

        public static object GetFieldTable(Dictionary<string, object> row, string field)
        {
            if (row == null || string.IsNullOrEmpty(field)) return null;
            if (!row.TryGetValue(field, out var v) || v == null) return null;

            // 已是结构化数据（未来若 TabParser 升级）→ 直接返回
            if (v is List<object> lo) return lo;
            if (v is object[] oa) { var l = new List<object>(oa); return l; }

            var s = v.ToString();
            if (string.IsNullOrEmpty(s)) return null;
            // json 列空对象哨兵 "{}"（CLAUDE.md §5 json 默认值约定）→ 视为「无数组数据」静默返回 null，
            // 不当解析错误（GetFieldTable 面向数组形 json，"{}" 即等价于空数组的兜底默认）。
            var st = s.Trim();
            if (st == "{}" || st == "null") return null;
            int idx = 0;
            try
            {
                var parsed = ParseJsonArrayOrInt(s, ref idx);
                return parsed;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[ConfigBridge.GetFieldTable] 解析 '{field}' 失败: {e.Message} raw='{s}'");
                return null;
            }
        }

        // 简单 JSON 子集解析：仅 int / array of int / nested array
        // - SkipSpace → 跳空白
        // - 遇 '[' → 递归 ParseArray
        // - 遇数字 / '-' → ParseInt
        private static object ParseJsonArrayOrInt(string s, ref int idx)
        {
            SkipSpace(s, ref idx);
            if (idx >= s.Length) return null;
            char c = s[idx];
            if (c == '[') return ParseJsonArray(s, ref idx);
            return ParseJsonInt(s, ref idx);
        }

        private static List<object> ParseJsonArray(string s, ref int idx)
        {
            var list = new List<object>();
            if (s[idx] != '[') throw new System.Exception("expect [");
            idx++;
            SkipSpace(s, ref idx);
            if (idx < s.Length && s[idx] == ']') { idx++; return list; }
            while (idx < s.Length)
            {
                SkipSpace(s, ref idx);
                var item = ParseJsonArrayOrInt(s, ref idx);
                list.Add(item);
                SkipSpace(s, ref idx);
                if (idx >= s.Length) break;
                if (s[idx] == ',') { idx++; continue; }
                if (s[idx] == ']') { idx++; return list; }
                throw new System.Exception($"unexpected char '{s[idx]}' at {idx}");
            }
            throw new System.Exception("array not closed");
        }

        private static int ParseJsonInt(string s, ref int idx)
        {
            int start = idx;
            if (idx < s.Length && (s[idx] == '-' || s[idx] == '+')) idx++;
            while (idx < s.Length && s[idx] >= '0' && s[idx] <= '9') idx++;
            if (idx == start) throw new System.Exception($"expect digit at {idx}");
            int v = 0;
            int.TryParse(s.Substring(start, idx - start), out v);
            return v;
        }

        private static void SkipSpace(string s, ref int idx)
        {
            while (idx < s.Length && (s[idx] == ' ' || s[idx] == '\t' || s[idx] == '\r' || s[idx] == '\n')) idx++;
        }
    }
}
