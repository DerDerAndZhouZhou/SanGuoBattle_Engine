using System.Collections.Generic;
using UnityEngine;
using HeroDefense.Config;

namespace HeroDefense.Core
{
    /// <summary>
    /// 音频管理器 - 管理 BGM 和音效播放。
    /// 音效资源由 Lua 侧注册（Resource_LoadSprite 同理的图片桥接），此处只管播放。
    ///
    /// 2026-05-06 重构：
    ///   - 删 _bgmSource / _sfxSource SerializeField，永远走 AddComponent 自动创建路径
    ///   - 默认音量从 GameConfig.txt 读（key=audio_default_bgm_volume / audio_default_sfx_volume）
    ///   - ReloadConfig() 公共方法：BootInitializer Step 6.5 后或外部触发重读
    /// </summary>
    public class AudioManager : MonoBehaviour
    {
        private static AudioManager _instance;
        public static AudioManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("[AudioManager]");
                    _instance = go.AddComponent<AudioManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        AudioSource _bgmSource;
        AudioSource _sfxSource;

        private Dictionary<string, AudioClip> _audioClips = new Dictionary<string, AudioClip>();

        private float _bgmVolume = 1f;
        private float _sfxVolume = 1f;
        private bool _isMuted = false;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);

            SetupAudioSources();
            ReloadConfig();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void SetupAudioSources()
        {
            // 永远自动 AddComponent（不依赖 Inspector 拖拽）
            _bgmSource = gameObject.AddComponent<AudioSource>();
            _bgmSource.loop = true;
            _bgmSource.playOnAwake = false;

            _sfxSource = gameObject.AddComponent<AudioSource>();
            _sfxSource.loop = false;
            _sfxSource.playOnAwake = false;
        }

        /// <summary>
        /// 从 GameConfig.txt 读默认音量。Awake 自动调一次；
        /// 若 ConfigManager 尚未加载（Boot 早期）则保留默认 1f，可由外部稍后再调一次。
        /// </summary>
        public void ReloadConfig()
        {
            var cm = ConfigManager.Instance;
            if (cm == null) return;
            try { cm.LoadIfNeeded(); } catch { return; }

            var rowBgm = cm.GetTableInfo("GameConfig", "key", "audio_default_bgm_volume");
            if (rowBgm != null)
            {
                _bgmVolume = ParseFloat(cm.GetValue<string>(rowBgm, "value", "1"), 1f);
                if (_bgmSource != null) _bgmSource.volume = _bgmVolume;
            }
            var rowSfx = cm.GetTableInfo("GameConfig", "key", "audio_default_sfx_volume");
            if (rowSfx != null)
            {
                _sfxVolume = ParseFloat(cm.GetValue<string>(rowSfx, "value", "1"), 1f);
            }
        }

        public void RegisterClip(string clipName, AudioClip clip)
        {
            if (!_audioClips.ContainsKey(clipName))
            {
                _audioClips[clipName] = clip;
            }
        }

        public void PlayBGM(string bgmName)
        {
            if (_isMuted) return;
            if (_audioClips.ContainsKey(bgmName))
            {
                _bgmSource.clip = _audioClips[bgmName];
                _bgmSource.volume = _bgmVolume;
                _bgmSource.Play();
            }
            else
            {
                Debug.LogWarning("[AudioManager] BGM未找到: " + bgmName);
            }
        }

        public void StopBGM()
        {
            _bgmSource.Stop();
        }

        public void PlaySFX(string sfxName)
        {
            if (_isMuted) return;
            if (_audioClips.ContainsKey(sfxName))
            {
                _sfxSource.PlayOneShot(_audioClips[sfxName], _sfxVolume);
            }
            else
            {
                Debug.LogWarning("[AudioManager] SFX未找到: " + sfxName);
            }
        }

        public void SetBGMVolume(float volume)
        {
            _bgmVolume = Mathf.Clamp01(volume);
            _bgmSource.volume = _bgmVolume;
        }

        public void SetSFXVolume(float volume)
        {
            _sfxVolume = Mathf.Clamp01(volume);
        }

        public void SetMute(bool mute)
        {
            _isMuted = mute;
            _bgmSource.mute = mute;
            _sfxSource.mute = mute;
        }

        public void ToggleMute()
        {
            SetMute(!_isMuted);
        }

        static float ParseFloat(string s, float fallback)
        {
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
