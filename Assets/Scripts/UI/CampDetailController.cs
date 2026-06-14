using UnityEngine;
using UnityEngine.UI;
using HeroDefense.Battle;
using HeroDefense.Engine.Host;
#if XLUA
using XLua;
#endif

namespace HeroDefense.UI
{
    /// <summary>
    /// 营帐详情弹窗控制器（Phase 2 Task 2.6 / Spec §8 D8.1）。
    ///
    /// 数据流：
    ///   - 挂在 UIWindow scene 的 `CampDetailPanel`（Tag `Panel_CampDetail`）上
    ///   - OnEnable 时通过 UIFinder 查 TitleLabel / HpLabel / DescLabel / UpgradeBtn / CloseBtn 子节点
    ///   - Refresh() 读 Lua `Camp_GetUpgradeInfo()` 与 BattleBridge.Battle_GetCampHp/MaxHp 拼字符串
    ///   - UpgradeBtn.onClick → 调 `Camp_Upgrade()` → Refresh()
    ///   - CloseBtn.onClick → SetActive(false)
    ///
    /// 设计约束（CLAUDE.md §1.2）：
    ///   - 0 [SerializeField]（UIFinder 替代）
    ///   - Controller 不写业务（升级判定全在 Lua），仅负责"查节点 / 渲染 / 派发回调"
    ///   - LuaTable Get 用 string→T 重载避 xLua userdata 坑（参考 DamageStatsController）
    /// </summary>
    public class CampDetailController : MonoBehaviour
    {
        Text _titleText, _hpText, _descText, _btnText;
        Button _upgradeBtn, _closeBtn;

        void OnEnable()
        {
            ResolveChildren();
            Refresh();
        }

        void ResolveChildren()
        {
            _titleText   = UIFinder.FindChildByName<Text>(transform, "TitleLabel");
            _hpText      = UIFinder.FindChildByName<Text>(transform, "HpLabel");
            _descText    = UIFinder.FindChildByName<Text>(transform, "DescLabel");
            _upgradeBtn  = UIFinder.FindChildByName<Button>(transform, "UpgradeBtn");
            _btnText     = _upgradeBtn != null ? _upgradeBtn.GetComponentInChildren<Text>() : null;
            _closeBtn    = UIFinder.FindChildByName<Button>(transform, "CloseBtn");

            if (_upgradeBtn != null)
            {
                _upgradeBtn.onClick.RemoveAllListeners();
                _upgradeBtn.onClick.AddListener(OnUpgradeClick);
            }
            if (_closeBtn != null)
            {
                _closeBtn.onClick.RemoveAllListeners();
                _closeBtn.onClick.AddListener(OnCloseClick);
            }
        }

        void Refresh()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;

                var fn = env.Global.Get<LuaFunction>("Camp_GetUpgradeInfo");
                if (fn == null)
                {
                    Debug.LogWarning("[CampDetail] Lua Camp_GetUpgradeInfo 未定义");
                    return;
                }
                object[] ret = fn.Call();
                fn.Dispose();
                if (ret == null || ret.Length == 0) return;
                var info = ret[0] as LuaTable;
                if (info == null) return;

                int curLv = SafeGetInt(info, "current_lv", 1);
                bool isMax = SafeGetBool(info, "is_max", false);

                int curHp = BattleBridge.Battle_GetCampHp();
                int maxHp = BattleBridge.Battle_GetCampMaxHp();

                if (_titleText != null) _titleText.text = $"营帐 lv{curLv}";
                if (_hpText != null) _hpText.text = $"血量 {curHp}/{maxHp}";

                if (isMax)
                {
                    if (_descText != null) _descText.text = "已满级";
                    if (_btnText != null) _btnText.text = "已满级";
                    if (_upgradeBtn != null) _upgradeBtn.interactable = false;
                }
                else
                {
                    int cost        = SafeGetInt(info, "cost", 0);
                    float hpMul     = SafeGetFloat(info, "next_hp_mul", 1.0f);
                    int gachaBoost  = SafeGetInt(info, "gacha_boost", 0);
                    int rewardBoost = SafeGetInt(info, "reward_boost", 0);

                    if (_descText != null)
                        _descText.text = $"下一级血 x{hpMul:F1}、传奇 +{gachaBoost}%、奖励 +{rewardBoost}%";
                    if (_btnText != null) _btnText.text = $"升级（花费 {cost} 金币）";
                    if (_upgradeBtn != null) _upgradeBtn.interactable = true;
                }

                info.Dispose();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CampDetail] Refresh: {e.Message}");
            }
#endif
        }

        void OnUpgradeClick()
        {
#if XLUA
            try
            {
                var env = LuaHost.Env;
                if (env == null) return;
                var fn = env.Global.Get<LuaFunction>("Camp_Upgrade");
                if (fn == null)
                {
                    Debug.LogWarning("[CampDetail] Lua Camp_Upgrade 未定义");
                    return;
                }
                fn.Call();
                fn.Dispose();
                Refresh();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[CampDetail] OnUpgradeClick: {e.Message}");
            }
#endif
        }

        void OnCloseClick()
        {
            gameObject.SetActive(false);
        }

#if XLUA
        // 安全 LuaTable getter：避 key 不存在时抛异常
        static int SafeGetInt(LuaTable t, string key, int def)
        {
            try { return t.Get<string, int>(key); } catch { return def; }
        }
        static float SafeGetFloat(LuaTable t, string key, float def)
        {
            try { return t.Get<string, float>(key); } catch { return def; }
        }
        static bool SafeGetBool(LuaTable t, string key, bool def)
        {
            try { return t.Get<string, bool>(key); } catch { return def; }
        }
#endif
    }
}
