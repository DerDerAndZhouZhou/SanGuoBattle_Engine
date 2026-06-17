using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroDefense.Core
{
    /// <summary>
    /// 存档管理器 - 使用 PlayerPrefs 存储游戏进度。
    /// TODO: 后期替换为抖音小游戏云存档 API。
    /// </summary>
    public class SaveManager : MonoBehaviour
    {
        private static SaveManager _instance;
        private static bool _isQuitting = false;

        public static SaveManager Instance
        {
            get
            {
                if (_isQuitting) return null;
                if (_instance == null)
                {
                    GameObject go = new GameObject("[SaveManager]");
                    _instance = go.AddComponent<SaveManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private const string SAVE_KEY = "HeroDefense_SaveData";
        private const string SETTINGS_KEY = "HeroDefense_Settings";

        private PlayerSaveData _saveData;
        private GameSettings _settings;

        // 存储后端抽象（五层重组 v3 阶段 B2·2026-06-18）：默认本地 PlayerPrefs；
        // 接抖音/微信云存档或自建服务器只需 SetBackend(新实现)，业务（Lua Save_*/Profile）零感知。
        private ISaveBackend _backend = new LocalPrefsBackend();

        // 通用 KV 存档运行时索引（核心循环 P0-2：关卡进度等）。
        // LoadAll 时由 _saveData.kvKeys/kvValues 双 list 重建；Save 前 Flush 回写。
        private Dictionary<string, string> _kv;

        public PlayerSaveData SaveData => _saveData;
        public GameSettings Settings => _settings;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            LoadAll();
        }

        private void OnApplicationQuit()
        {
            _isQuitting = true;
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _isQuitting = true;
                _instance = null;
            }
        }

        public void LoadAll()
        {
            string saveJson = _backend.GetString(SAVE_KEY, "");
            _saveData = !string.IsNullOrEmpty(saveJson)
                ? JsonUtility.FromJson<PlayerSaveData>(saveJson)
                : new PlayerSaveData();

            string settingsJson = _backend.GetString(SETTINGS_KEY, "");
            _settings = !string.IsNullOrEmpty(settingsJson)
                ? JsonUtility.FromJson<GameSettings>(settingsJson)
                : new GameSettings();

            RebuildKvIndex();
        }

        public void SaveAll()
        {
            FlushKvIndex();
            _backend.SetString(SAVE_KEY, JsonUtility.ToJson(_saveData));
            _backend.SetString(SETTINGS_KEY, JsonUtility.ToJson(_settings));
            _backend.Flush();
        }

        public void SaveProgress()
        {
            FlushKvIndex();
            _backend.SetString(SAVE_KEY, JsonUtility.ToJson(_saveData));
            _backend.Flush();
        }

        // ======== 通用 KV 存档（核心循环 P0-2 / P0-3，经 SaveBridge 暴露给 Lua）========

        /// <summary>双 list → 运行时 Dictionary 索引（LoadAll 调）。</summary>
        private void RebuildKvIndex()
        {
            _kv = new Dictionary<string, string>();
            if (_saveData?.kvKeys == null || _saveData.kvValues == null) return;
            int n = Mathf.Min(_saveData.kvKeys.Count, _saveData.kvValues.Count);
            for (int i = 0; i < n; i++)
            {
                if (!string.IsNullOrEmpty(_saveData.kvKeys[i]))
                    _kv[_saveData.kvKeys[i]] = _saveData.kvValues[i];
            }
        }

        /// <summary>运行时 Dictionary → 双 list（Save 前调，确保序列化）。</summary>
        private void FlushKvIndex()
        {
            if (_kv == null || _saveData == null) return;
            _saveData.kvKeys.Clear();
            _saveData.kvValues.Clear();
            foreach (var pair in _kv)
            {
                _saveData.kvKeys.Add(pair.Key);
                _saveData.kvValues.Add(pair.Value);
            }
        }

        public string GetKV(string key, string def)
        {
            if (_kv == null) RebuildKvIndex();
            return (!string.IsNullOrEmpty(key) && _kv.TryGetValue(key, out var v)) ? v : def;
        }

        public void SetKV(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_kv == null) RebuildKvIndex();
            _kv[key] = value ?? "";
            SaveProgress();   // 即时落盘，关卡进度掉电不丢
        }

        public bool HasKV(string key)
        {
            if (_kv == null) RebuildKvIndex();
            return !string.IsNullOrEmpty(key) && _kv.ContainsKey(key);
        }

        public void AddCoins(int amount)
        {
            _saveData.totalCoins += amount;
            SaveProgress();
            EventManager.Instance?.TriggerEvent(GameEvents.COINS_EARNED, amount);
        }

        public bool SpendCoins(int amount)
        {
            if (_saveData.totalCoins >= amount)
            {
                _saveData.totalCoins -= amount;
                SaveProgress();
                EventManager.Instance?.TriggerEvent(GameEvents.COINS_SPENT, amount);
                return true;
            }
            return false;
        }

        public void AddGems(int amount)
        {
            _saveData.totalGems += amount;
            SaveProgress();
            EventManager.Instance?.TriggerEvent(GameEvents.GEMS_EARNED, amount);
        }

        public void ClearSaveData()
        {
            _saveData = new PlayerSaveData();
            _settings = new GameSettings();
            _backend.DeleteAll();
        }

        /// <summary>切换存储后端（本地/抖音云/自建服务器）。切换后重新加载存档。
        /// 平台启动码按需调用（如抖音平台 boot 时 SetBackend(new DouyinCloudBackend())）；业务 Lua 零感知。</summary>
        public void SetBackend(ISaveBackend backend)
        {
            if (backend == null) return;
            _backend = backend;
            LoadAll();
        }
    }

    /// <summary>玩家存档数据。</summary>
    [Serializable]
    public class PlayerSaveData
    {
        public int totalCoins = 0;
        public int totalGems = 0;
        public int highestFloor = 0;        // 最高通关楼层（肉鸽进度）
        public int runsCompleted = 0;       // 累计通关次数

        // 通用 KV 存档（核心循环 P0-2：关卡进度等）。
        // JsonUtility 不支持 Dictionary → 用两个平行 list（同下标配对）表达 map。
        public List<string> kvKeys   = new List<string>();
        public List<string> kvValues = new List<string>();
    }

    /// <summary>游戏设置数据。</summary>
    [Serializable]
    public class GameSettings
    {
        public float bgmVolume = 1f;
        public float sfxVolume = 1f;
        public bool isMuted = false;
        public bool vibrationEnabled = true;
    }

    /// <summary>存储后端抽象（五层重组 v3 阶段 B2·2026-06-18）。
    /// SaveManager 只认这个接口存取存档 blob（SAVE_KEY/SETTINGS_KEY 两个键）；
    /// 本地=PlayerPrefs；后续抖音/微信云存档、自建服务器各实现一个，SetBackend 即切换，业务零感知。</summary>
    public interface ISaveBackend
    {
        string GetString(string key, string def);
        void SetString(string key, string value);
        void Flush();      // 提交落盘（本地 = PlayerPrefs.Save；云 = 上传）
        void DeleteAll();  // 清空全部存档
    }

    /// <summary>本地存储后端：PlayerPrefs（编辑器 / PC / 真机本地）。SaveManager 默认实现。</summary>
    public class LocalPrefsBackend : ISaveBackend
    {
        public string GetString(string key, string def) { return PlayerPrefs.GetString(key, def); }
        public void SetString(string key, string value) { PlayerPrefs.SetString(key, value); }
        public void Flush() { PlayerPrefs.Save(); }
        public void DeleteAll() { PlayerPrefs.DeleteAll(); }
    }
}
