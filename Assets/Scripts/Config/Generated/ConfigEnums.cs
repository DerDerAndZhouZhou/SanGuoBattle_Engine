// 字段名与 Product/Game/settings/Enum.tab 一致；
// EnumRegistry.Load() 在启动时通过反射写入值。
namespace HeroDefense.Config
{
    public static class ANIM_TYPE
    {
        public static int atFrame;
        public static int atSpine;
    }

    public static class TROOP_TYPE
    {
        public static int ttNone;
        public static int ttShield;
        public static int ttSpear;
        public static int ttBow;
        public static int ttCavalry;
    }
}
