using System;
using UnityEngine;
using HeroDefense.Config;
using HeroDefense.Utils;

namespace HeroDefense.SDK
{
    /// <summary>
    /// 广告管理器 - 激励视频广告。
    /// 失败复活、双倍奖励等场景使用。
    ///
    /// 2026-05-06 重构：_rewardedVideoAdId 从 GameConfig.txt 读（key=ad_rewarded_video_id）。
    /// </summary>
    public class AdManager : SingletonMono<AdManager>
    {
        const string DEFAULT_AD_ID = "your_ad_unit_id";
        string _rewardedVideoAdId = DEFAULT_AD_ID;

        private bool _isAdLoaded = false;
        public bool IsAdLoaded => _isAdLoaded;

        private Action<bool> _rewardCallback;

        protected override void OnSingletonInit()
        {
            ReloadConfig();
            PreloadRewardedAd();
        }

        /// <summary>
        /// 从 GameConfig.txt 读广告位 ID。Boot 早期 ConfigManager 未加载完时保留默认值，
        /// 外部需要时可重调本方法刷新。
        /// </summary>
        public void ReloadConfig()
        {
            var cm = ConfigManager.Instance;
            if (cm == null) return;
            try { cm.LoadIfNeeded(); } catch { return; }
            var row = cm.GetTableInfo("GameConfig", "key", "ad_rewarded_video_id");
            if (row != null)
            {
                _rewardedVideoAdId = cm.GetValue<string>(row, "value", DEFAULT_AD_ID);
            }
        }

        public void PreloadRewardedAd()
        {
#if UNITY_WEBGL && DOUYIN_MINIGAME
            // TODO: tt.createRewardedVideoAd({ adUnitId: _rewardedVideoAdId }).load()
            Debug.Log($"[AdManager] 预加载激励视频广告 unitId={_rewardedVideoAdId}");
#else
            _isAdLoaded = true;
            Debug.Log("[AdManager] 编辑器模拟：广告已加载");
#endif
        }

        /// <summary>
        /// 展示激励视频广告。
        /// callback(true) = 用户看完获得奖励；callback(false) = 中途关闭。
        /// </summary>
        public void ShowRewardedAd(Action<bool> callback)
        {
            _rewardCallback = callback;

#if UNITY_WEBGL && DOUYIN_MINIGAME
            if (!_isAdLoaded)
            {
                Debug.LogWarning("[AdManager] 广告未加载完成");
                callback?.Invoke(false);
                return;
            }
            // TODO: ad.show() + 监听 onClose 回调判断 isEnded
            Debug.Log("[AdManager] 展示激励视频广告");
#else
            Debug.Log("[AdManager] 编辑器模拟：观看广告完成，发放奖励");
            callback?.Invoke(true);
            PreloadRewardedAd();
#endif
        }

        /// <summary>战斗失败复活用。</summary>
        public void ShowReviveAd(Action<bool> callback) => ShowRewardedAd(callback);

        /// <summary>双倍金币奖励用。</summary>
        public void ShowDoubleRewardAd(Action<bool> callback) => ShowRewardedAd(callback);
    }
}
