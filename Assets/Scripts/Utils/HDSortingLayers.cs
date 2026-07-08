namespace HeroDefense.Utils
{
    /// <summary>
    /// HeroDefense 渲染分层常量。与 ProjectSettings/TagManager.asset 的 m_SortingLayers 保持一致。
    /// 写代码时统一通过这些常量传 SpriteRenderer.sortingLayerName，不要写裸字符串。
    ///
    /// 渲染顺序（从下到上）：Background → Grid → Castle → Tower → Enemy → Projectile → VFX → UIWorld。
    /// 技能 Sprite VFX 运行时会按目标主体 SpriteRenderer 的 layer/order-1 渲染，避免挡住武将图。
    /// </summary>
    public static class HDSortingLayers
    {
        public const string Default = "Default";
        public const string Background = "Background";
        public const string Grid = "Grid";
        public const string Castle = "Castle";
        public const string Tower = "Tower";
        public const string Enemy = "Enemy";
        public const string Projectile = "Projectile";
        public const string VFX = "VFX";
        public const string UIWorld = "UIWorld";
    }
}
