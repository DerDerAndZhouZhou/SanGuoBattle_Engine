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
            string saveJson = PlayerPrefs.GetString(SAVE_KEY, "");
            _saveData = !string.IsNullOrEmpty(saveJson)
                ? JsonUtility.FromJson<PlayerSaveData>(saveJson)
                : new PlayerSaveData();

            string settingsJson = PlayerPrefs.GetString(SETTINGS_KEY, "");
            _settings = !string.IsNullOrEmpty(settingsJson)
                ? JsonUtility.FromJson<GameSettings>(settingsJson)
                : new GameSettings();

            RebuildKvIndex();
        }

        public void SaveAll()
        {
            FlushKvIndex();
            PlayerPrefs.SetString(SAVE_KEY, JsonUtility.ToJson(_saveData));
            PlayerPrefs.SetString(SETTINGS_KEY, JsonUtility.ToJson(_settings));
            PlayerPrefs.Save();
        }

        public void SaveProgress()
        {
            FlushKvIndex();
            PlayerPrefs.SetString(SAVE_KEY, JsonUtility.ToJson(_saveData));
            PlayerPrefs.Save();
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
            PlayerPrefs.DeleteAll();
            PlayerPrefs.Save();
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
}
