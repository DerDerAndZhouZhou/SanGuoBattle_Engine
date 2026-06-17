// 枚举容器类 —— 字段名必须与 Game/settings/Enum.tab 保持一致。
// 值在启动时由 EnumRegistry.Load() 反射填充（不依赖 UnityEditor）。
// 新增枚举类型时：① Enum.txt 添加行；② 本文件追加同名 static class + public static int 字段。
// 17 类枚举（v3 对局架构），与 Enum.txt 一一对应。

namespace HeroDefense.Config
{
    public static class BATTLE_PHASE
    {
        public static int bpInit;
        public static int bpPrep;
        public static int bpCombat;
        public static int bpVictory;
        public static int bpFailed;
        public static int bpPaused;
    }

    public static class UNIT_TYPE
    {
        public static int utSoldier;
        public static int utHero;
        public static int utBuilding;
    }

    public static class ATTACK_MODE
    {
        public static int amMelee;
        public static int amRanged;
        public static int amMix;
    }

    public static class ATTACK_TYPE
    {
        public static int atPhysical;
        public static int atMagic;
        public static int atMix;
    }

    public static class ELEMENT
    {
        public static int elNone;
        public static int elWater;
        public static int elFire;
        public static int elWind;
        public static int elEarth;
        public static int elThunder;
        public static int elElectric;
        public static int elPoison;
        public static int elPhysical;
    }

    public static class RARITY
    {
        public static int rNone;
        public static int rCommon;
        public static int rRare;
        public static int rEpic;
        public static int rLegend;
        public static int rMythic;
    }

    public static class MONSTER_TYPE
    {
        public static int mtGrunt;
        public static int mtFast;
        public static int mtTank;
        public static int mtElite;
        public static int mtBoss;
        public static int mtRanged;
        public static int mtFlying;
        public static int mtAssassin;
        public static int mtRegen;
        public static int mtStealth;
        public static int mtSuicide;
        public static int mtMiniBoss;
        public static int mtMegaBoss;
    }

    public static class REWARD_TYPE
    {
        public static int rtBuff;
        public static int rtHeroCard;
        public static int rtSkill;
        public static int rtGold;
        public static int rtDiamond;
        public static int rtCard;
    }

    public static class CARD_TYPE
    {
        public static int ctSoldier;
        public static int ctHero;
        public static int ctBuilding;
        public static int ctSkill;
    }

    public static class DRAG_CELL_STATE
    {
        public static int dcsNone;
        public static int dcsYellowAvail;
        public static int dcsDarkYellowSame;
        public static int dcsGreenDrop;
        public static int dcsRedBlock;
    }

    public static class OCCUPY_ANCHOR
    {
        public static int oaTopLeft;
        public static int oaCenter;
        public static int oaBottomLeft;
    }

    public static class WAVE_TYPE
    {
        public static int wtNormal;
        public static int wtElite;
        public static int wtSegmentBoss;
        public static int wtFinalBoss;
        public static int wtAirRaid;
        public static int wtMultiLane;
    }

    public static class BANNER_COLOR
    {
        public static int bcRed;
        public static int bcYellow;
        public static int bcDarkRed;
    }

    public static class CHAPTER_THEME
    {
        public static int ctYellowTurban;
        public static int ctDongZhuo;
        public static int ctWarlords;
        public static int ctGuandu;
        public static int ctChibi;
    }

    public static class LANE_TYPE
    {
        public static int ltGround;
        public static int ltAir;
    }

    public static class LANE_PREF
    {
        public static int lpRandom;
        public static int lpFixedTop;
        public static int lpFixedMid;
        public static int lpFixedBot;
        public static int lpMulti;
    }

    public static class ATK_TARGET
    {
        public static int atNearest;
        public static int atHeroFirst;
        public static int atBuildingFirst;
        public static int atNoAttack;
    }

    // 统一随机池系统：random_pool.txt 的 generate_type 列。
    public static class GENERATE_TYPE
    {
        public static int gtHeroById;
        public static int gtHeroByRarity;
        public static int gtHeroFromSet;
        public static int gtSoldier;
        public static int gtBuilding;
        public static int gtBuff;
        public static int gtSkillCard;
        public static int gtGold;
        public static int gtDiamond;
        public static int gtExp;
    }
}
