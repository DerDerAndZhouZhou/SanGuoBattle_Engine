using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Config;

namespace HeroDefense.UI
{
    /// <summary>
    /// mainWindow 顶部全局常驻资源栏（占位）：
    /// - 左侧：玩家头像（icon_avatar_default）+ 昵称
    /// - 右侧：4 资源（gold / diamond / stamina / video_ticket）icon + 数值
    /// 5 Tab 切换时常驻不消失。M2.4 接 GameManager / EconomyManager 真实数据。
    ///
    /// 子组件查找规则（2026-05-06 重构）：本 controller 挂在 TopBar panel 自身上，
    /// 子组件按命名规约通过 UIFinder 递归查找；找不到 → LogWarning。
    /// </summary>
    public class MainWindowTopBar : MonoBehaviour
    {
        Image _avatarImage;
        Text _nicknameText;
        Image _goldIcon;
        Text _goldText;
        Image _diamondIcon;
        Text _diamondText;
        Image _staminaIcon;
        Text _staminaText;
        Image _videoTicketIcon;
        Text _videoTicketText;

        const string PLACEHOLDER_NICKNAME = "玩家昵称";
        const int PLACEHOLDER_GOLD = 12345;
        const int PLACEHOLDER_DIAMOND = 999;
        const int PLACEHOLDER_STAMINA_CUR = 50;
        const int PLACEHOLDER_STAMINA_MAX = 50;
        const int PLACEHOLDER_VIDEO_TICKET = 3;

        void Start()
        {
            ConfigManager.Instance.LoadIfNeeded();
            ResolveChildren();

            UiAssetHelper.ApplyToImage(_avatarImage, "icon_avatar_default");
            UiAssetHelper.ApplyToImage(_goldIcon, "hud_gold");
            UiAssetHelper.ApplyToImage(_diamondIcon, "hud_diamond");
            UiAssetHelper.ApplyToImage(_staminaIcon, "hud_stamina_v2");
            UiAssetHelper.ApplyToImage(_videoTicketIcon, "hud_video_ticket");

            if (_nicknameText != null) _nicknameText.text = PLACEHOLDER_NICKNAME;
            if (_goldText != null) _goldText.text = PLACEHOLDER_GOLD.ToString();
            if (_diamondText != null) _diamondText.text = PLACEHOLDER_DIAMOND.ToString();
            if (_staminaText != null) _staminaText.text = PLACEHOLDER_STAMINA_CUR + "/" + PLACEHOLDER_STAMINA_MAX;
            if (_videoTicketText != null) _videoTicketText.text = PLACEHOLDER_VIDEO_TICKET.ToString();
        }

        void ResolveChildren()
        {
            // 适配 UIWindow.scene 现有结构（AvatarBlock + ResourceBar/Res*/Icon|Value 容器化布局）：
            //   AvatarBlock/AvatarImage  AvatarBlock/NicknameText
            //   ResourceBar/ResGold/Icon          /Value
            //   ResourceBar/ResDiamond/Icon       /Value
            //   ResourceBar/ResStamina/Icon       /Value
            //   ResourceBar/ResVideoTicket/Icon   /Value
            // 4 组 Res*/Icon|Value 重名，必须用 FindChildByPath 全路径区分。
            _avatarImage      = UIFinder.FindChildByName<Image>(transform, "AvatarImage");
            _nicknameText     = UIFinder.FindChildByName<Text>(transform, "NicknameText");
            _goldIcon         = UIFinder.FindChildByPath<Image>(transform, "ResourceBar/ResGold/Icon");
            _goldText         = UIFinder.FindChildByPath<Text>(transform,  "ResourceBar/ResGold/Value");
            _diamondIcon      = UIFinder.FindChildByPath<Image>(transform, "ResourceBar/ResDiamond/Icon");
            _diamondText      = UIFinder.FindChildByPath<Text>(transform,  "ResourceBar/ResDiamond/Value");
            _staminaIcon      = UIFinder.FindChildByPath<Image>(transform, "ResourceBar/ResStamina/Icon");
            _staminaText      = UIFinder.FindChildByPath<Text>(transform,  "ResourceBar/ResStamina/Value");
            _videoTicketIcon  = UIFinder.FindChildByPath<Image>(transform, "ResourceBar/ResVideoTicket/Icon");
            _videoTicketText  = UIFinder.FindChildByPath<Text>(transform,  "ResourceBar/ResVideoTicket/Value");
        }
    }
}
