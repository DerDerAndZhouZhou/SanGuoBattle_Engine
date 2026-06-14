using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroDefense.Config
{
    /// <summary>
    /// .tab/.txt 配置表解析器
    /// 格式（4 行表头）:
    ///   第1行 = 字段名（keys）
    ///   第2行 = 类型（types）
    ///   第3行 = 默认值（每列字面量；留空时 fallback 到该类型硬编码默认 GetDefault）
    ///   第4行 = 注释行（被忽略，仅给策划/编辑器/工具读；不能整行空白否则被 prefilter 吃掉）
    ///   第5行起 = 数据
    ///   # / // 开头行 + 空行 在 prefilter 阶段被剔除。
    ///
    /// 默认值 fallback 链（高→低）：
    ///   1. 数据行有值 → 用数据行
    ///   2. 数据行空字段 → 用第3行默认值字面量解析的值
    ///   3. 第3行该列也是空字符串 → 用类型硬编码默认（int=0 / string="" / array=空 / json="{}"）
    ///
    /// 支持类型:
    ///   int, long, float, double, bool, string
    ///   int[], long[], float[], double[], string[], bool[] (用 , 分隔)
    ///   Enum_XXX (枚举类型，填枚举名，解析为 int 值)
    ///   json (原始JSON字符串)
    /// </summary>
    public static class TabParser
    {
        private const char COL_SEP = '\t';
        private const char ARRAY_SEP = ',';

        public static List<Dictionary<string, object>> Parse(string tabContent)
        {
            var result = new List<Dictionary<string, object>>();
            var rawLines = tabContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            // 预过滤：# / // 注释行恒过滤；空白行的处理分 3 段
            //   ① keys 行之前（文件顶部 # 注释后的空白） → 过滤（避免错位为 lines[0]）
            //   ② keys 之后 / 4 行 schema 内（第 3 行默认值行允许整行只有 Tab） → 保留
            //      （原因：若 IsNullOrWhiteSpace 直接吃，第 4 行注释行会顶替默认值角色 → string 列默认值变中文注释 bug）
            //   ③ 数据段（lines.Count >= 4） → 过滤（数据中的空白行无意义）
            var lines = new List<string>();
            bool seenFirstSchema = false;
            for (int r = 0; r < rawLines.Length; r++)
            {
                string raw = rawLines[r];
                string t = raw.TrimStart();
                // 注释行：# 或 //。容错：WPS/Excel 保存 tab 文件时会把含逗号的注释行用双引号包裹
                //（# ...,... → "# ...,..."），导致 "# 开头不再匹配 # → 注释行顶替 keys 表头 → 全列错位。
                // 故剥掉一个前导 " 后再判 #//（只命中 "# / "// ，不误伤首字段为引号字符串的数据行）。
                string tc = t.StartsWith("\"") ? t.Substring(1) : t;
                if (tc.StartsWith("#") || tc.StartsWith("//")) continue;
                bool isBlank = string.IsNullOrWhiteSpace(raw);
                if (!seenFirstSchema)
                {
                    if (isBlank) continue;          // 段 ①
                    seenFirstSchema = true;
                }
                else if (isBlank && lines.Count >= 4)
                {
                    continue;                       // 段 ③
                }
                lines.Add(raw);
            }

            if (lines.Count < 4)
            {
                Debug.LogWarning("[TabParser] 行数不足4行(keys/types/defaults/comment)");
                return result;
            }

            var keys = lines[0].Split(COL_SEP);
            var types = lines[1].Split(COL_SEP);
            var defaultsLine = lines[2].Split(COL_SEP);
            // lines[3] = 注释行，整行忽略（人类可读说明）
            int colCount = keys.Length;

            var defaultValues = new object[colCount];
            for (int c = 0; c < colCount; c++)
            {
                string type = c < types.Length ? types[c].Trim() : "string";
                string defaultRaw = c < defaultsLine.Length ? defaultsLine[c].Trim() : "";
                defaultValues[c] = string.IsNullOrEmpty(defaultRaw)
                    ? GetDefault(type)
                    : ParseValue(defaultRaw, type);
            }

            for (int i = 4; i < lines.Count; i++)
            {
                string line = lines[i];
                // 注释和空行已在 prefilter 阶段过滤，这里直接走数据解析

                var cols = line.Split(COL_SEP);
                var row = new Dictionary<string, object>();

                for (int c = 0; c < colCount; c++)
                {
                    string key = keys[c].Trim();
                    if (string.IsNullOrEmpty(key)) continue;

                    string type = c < types.Length ? types[c].Trim() : "string";
                    string raw = c < cols.Length ? cols[c].Trim() : "";

                    row[key] = string.IsNullOrEmpty(raw) ? defaultValues[c] : ParseValue(raw, type);
                }

                result.Add(row);
            }

            return result;
        }

        public static object ParseValue(string raw, string type)
        {
            if (string.IsNullOrEmpty(raw)) return GetDefault(type);

            switch (type.ToLower())
            {
                case "int": return int.TryParse(raw, out int iv) ? iv : 0;
                case "long": return long.TryParse(raw, out long lv) ? lv : 0L;
                case "float": return float.TryParse(raw, out float fv) ? fv : 0f;
                case "double": return double.TryParse(raw, out double dv) ? dv : 0.0;
                case "bool": return raw == "1" || raw.ToLower() == "true";
                case "string": return raw;
                case "int[]": return ParseArray<int>(Dequote(raw), s => int.TryParse(s, out int v) ? v : 0);
                case "long[]": return ParseArray<long>(Dequote(raw), s => long.TryParse(s, out long v) ? v : 0L);
                case "float[]": return ParseArray<float>(Dequote(raw), s => float.TryParse(s, out float v) ? v : 0f);
                case "double[]": return ParseArray<double>(Dequote(raw), s => double.TryParse(s, out double v) ? v : 0.0);
                case "string[]": return SplitAndTrim(Dequote(raw));
                case "bool[]": return ParseArray<bool>(Dequote(raw), s => s == "1" || s.ToLower() == "true");
                case "json": return Dequote(raw);
                default:
                    if (EnumRegistry.IsEnumType(type))
                    {
                        string enumType = EnumRegistry.ExtractEnumType(type);
                        return EnumRegistry.GetValue(enumType, raw);
                    }
                    return raw;
            }
        }

        // CSV/spreadsheet 反引号容错（2026-06-07）：WPS/Excel 保存 tab 文件会把含逗号的字段
        //（json 如 [[9,1,0,0]] / 数组如 100,101,120,121）整体用双引号包裹、内部 " 翻倍成 ""。
        // json / 数组类型解析前剥掉外层引号 + 还原 "" → "，否则 formula_list 等门控解析为 nil → 全失效（见 CLAUDE.md §10）。
        private static string Dequote(string s)
        {
            if (s != null && s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                return s.Substring(1, s.Length - 2).Replace("\"\"", "\"");
            return s;
        }

        private static T[] ParseArray<T>(string raw, Func<string, T> parser)
        {
            var parts = raw.Split(ARRAY_SEP);
            var arr = new T[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                arr[i] = parser(parts[i].Trim());
            return arr;
        }

        private static string[] SplitAndTrim(string raw)
        {
            var parts = raw.Split(ARRAY_SEP);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();
            return parts;
        }

        private static object GetDefault(string type)
        {
            switch (type.ToLower())
            {
                case "int": return 0;
                case "long": return 0L;
                case "float": return 0f;
                case "double": return 0.0;
                case "bool": return false;
                case "string": return "";
                case "int[]": return new int[0];
                case "long[]": return new long[0];
                case "float[]": return new float[0];
                case "double[]": return new double[0];
                case "string[]": return new string[0];
                case "bool[]": return new bool[0];
                case "json": return "{}";
                default:
                    if (EnumRegistry.IsEnumType(type)) return 0;
                    return "";
            }
        }
    }
}
