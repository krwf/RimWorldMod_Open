using RimWorld;
using Verse;

namespace KRWF.RimKata
{
    [DefOf]
    public static class RimKataDefOf
    {
        [MayRequireBiotech]
        public static GeneDef RimKata_G;
        public static HediffDef RimKata_A_Effect;
        [MayRequireRoyalty]
        public static AbilityDef RimKata_P;
        [MayRequireIdeology]
        public static PreceptDef RimKata_I;
        public static JobDef RimKata_Attack;
        public static JobDef RimKata_EquipSecondary;
        public static RulePackDef RimKata_ParryBattleLog;
        public static ThingCategoryDef Grenades;
        public static SoundDef BulletImpact_Metal;

        static RimKataDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(RimKataDefOf));
        }
    }
}
