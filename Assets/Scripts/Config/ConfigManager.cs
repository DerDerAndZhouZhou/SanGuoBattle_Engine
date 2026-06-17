using System.Collections.Generic;
using System.IO;
using UnityEngine;
using HeroDefense.Engine.Host;

namespace HeroDefense.Config
{
    /// <summary>
    /// 配置表管理器 — 单例
    /// 自动加载 Game/settings/ 下所有 .tab 配置文件（经 ResourceHost 抽象层）。
    ///
    /// 数据结构:
    ///   _tables["tower"] = [
    ///     { "id": "archer_01", "damage": 10, "range": 3.5, ... },
    ///     { "id": "mage_01",   "damage": 8,  "range": 4.0, ... },
    ///     ...
    ///   ]
    ///
    /// 查询接口:
    ///   GetTableList("tower")                              → 全表数据
    ///   GetTableInfo("tower", "id", "archer_01")           → 第一条匹配行
    ///   GetTableInfoList("tower", "category", "ranged")    → 所有匹配行
    /// </summary>
    public class ConfigManager
    {
        private static ConfigManager _instance;
        public static ConfigManager Instance => _instance ?? (_instance = new ConfigManager());

        private readonly Dictionary<string, List<Dictionary<string, object>>> _tables
            = new Dictionary<string, List<Dictionary<string, object>>>();

        private bool _loaded;
        public bool IsLoaded => _loaded;

        /// <summary>幂等加载：首次调用实际执行 Load()，后续调用直接返回。</summary>
        public void LoadIfNeeded()
        {
            if (_loaded) return;
            Load();
        }

        /// <summary>加载 Game/settings/ 下所有 .tab 文件（通过 ResourceHost 抽象层）。</summary>
        public void Load()
        {
            _tables.Clear();

            EnumRegistry.Load();

            var files = ResourceHost.EnumerateFiles("settings", "*.tab");
            foreach (var rel in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(rel);
                if (fileName == "Enum") continue;
                if (fileName.EndsWith("example", System.StringComparison.OrdinalIgnoreCase)) continue;

                string text = ResourceHost.ReadText(rel);
                if (string.IsNullOrEmpty(text)) continue;

                var rows = TabParser.Parse(text);
                if (rows.Count > 0)
                {
                    _tables[fileName] = rows;
                    Debug.Log($"[ConfigManager] {fileName}: {rows.Count} 行");
                }
            }

            _loaded = true;
            Debug.Log($"[ConfigManager] 加载完成: {_tables.Count} 张表");
        }

        public void Reload(string tableName)
        {
            var text = ResourceHost.ReadText($"settings/{tableName}.tab");
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning($"[ConfigManager] Reload: {tableName}.tab 未找到");
                return;
            }
            _tables[tableName] = TabParser.Parse(text);
        }

        // ======== 查询接口 ========

        public List<Dictionary<string, object>> GetTableList(string tableName)
        {
            _tables.TryGetValue(tableName, out var list);
            return list;
        }

        public Dictionary<string, object> GetTableInfo(string tableName, Dictionary<string, object> conditions)
        {
            var list = GetTableList(tableName);
            if (list == null) return null;

            foreach (var row in list)
            {
                if (MatchConditions(row, conditions))
                    return row;
            }
            return null;
        }

        public Dictionary<string, object> GetTableInfo(string tableName, string field, object value)
        {
            var list = GetTableList(tableName);
            if (list == null) return null;

            foreach (var row in list)
            {
                if (row.TryGetValue(field, out var v) && ValueEquals(v, value))
                    return row;
            }
            return null;
        }

        public List<Dictionary<string, object>> GetTableInfoList(string tableName, Dictionary<string, object> conditions)
        {
            var result = new List<Dictionary<string, object>>();
            var list = GetTableList(tableName);
            if (list == null) return result;

            foreach (var row in list)
            {
                if (MatchConditions(row, conditions))
                    result.Add(row);
            }
            return result;
        }

        public List<Dictionary<string, object>> GetTableInfoList(string tableName, string field, object value)
        {
            var result = new List<Dictionary<string, object>>();
            var list = GetTableList(tableName);
            if (list == null) return result;

            foreach (var row in list)
            {
                if (row.TryGetValue(field, out var v) && ValueEquals(v, value))
                    result.Add(row);
            }
            return result;
        }

        public T GetValue<T>(Dictionary<string, object> row, string field, T defaultVal = default)
        {
            if (row == null || !row.TryGetValue(field, out var val)) return defaultVal;
            try { return (T)System.Convert.ChangeType(val, typeof(T)); }
            catch { return defaultVal; }
        }

        // ======== 内部方法 ========

        private bool MatchConditions(Dictionary<string, object> row, Dictionary<string, object> conditions)
        {
            foreach (var cond in conditions)
            {
                if (!row.TryGetValue(cond.Key, out var rowVal)) return false;
                if (!ValueEquals(rowVal, cond.Value)) return false;
            }
            return true;
        }

        private bool ValueEquals(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            return a.ToString() == b.ToString();
        }
    }
}
