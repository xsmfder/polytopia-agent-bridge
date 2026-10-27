// Own-unit passive growth. Native GetAge is independent of the combat XP counter.
using System;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static string UnitGrowthSnapshot(object unit, object gs, object data, bool own)
        {
            if (!own) return ProjectUnitGrowth(false, null, null, null, false, false);
            object next = null; bool nextReadable = false;
            try
            {
                var abilities = ReadNativeList(MemberWithAccessor(data, "unitAbilities"));
                bool grows = false;
                if (abilities != null) foreach (object ability in abilities)
                    if (SafeScalar(ability) == "Grow") grows = true;
                if (grows)
                {
                    next = TribeCatalogCall(URLogic(gs), "GetUnitWillGrowInto", new[] {"UnitData"}, new[] {data});
                    nextReadable = true;
                }
            }
            catch { nextReadable = false; }
            // The build gate checks the actual GameAssembly hash. The end-turn timing
            // rule is verified against natural Egg -> BabyDragon -> FireDragon samples.
            return ProjectUnitGrowth(true, data, CRInt(URUnitRule("GetAge", unit, gs)), next,
                nextReadable, URHPCurrentBuildVerified());
        }

        internal static string ProjectUnitGrowth(bool own, object data, int? age, object next,
            bool nextReadable, bool timingVerified)
        {
            string stage = null, target = null, status = own ? "unavailable" : "withheld";
            int? period = null, remaining = null;
            if (own)
            {
                stage = SafeScalar(MemberWithAccessor(data, "type"));
                var abilities = ReadNativeList(MemberWithAccessor(data, "unitAbilities"));
                bool grows = false;
                if (abilities != null) foreach (object ability in abilities)
                    if (SafeScalar(ability) == "Grow") grows = true;
                if (abilities != null && stage != null)
                {
                    if (!grows) status = "not_growing";
                    else
                    {
                        period = CRInt(MemberWithAccessor(data, "growthRate"));
                        target = nextReadable ? SafeScalar(MemberWithAccessor(next, "type")) : null;
                        if (timingVerified && target != null && target != stage
                            && age.HasValue && age.Value >= 0 && period.HasValue && period.Value > 0)
                        {
                            // Native growth runs before the round advances. An age already
                            // at the threshold still needs the next end_turn, hence +1.
                            long turns = Math.Max(0L, (long)period.Value - age.Value) + 1L;
                            if (turns <= Int32.MaxValue) { remaining = (int)turns; status = "growing"; }
                        }
                    }
                }
            }
            return new JsonObj().Str("schema", "unit-growth-1").Str("status", status)
                .NullOrStr("stage", stage).NullOrStr("nextStage", target)
                .Raw("ageTurns", own && age.HasValue && age.Value >= 0 ? CRN(age) : "null")
                .Raw("growthPeriodTurns", CRN(period)).Raw("turnsToNextStage", CRN(remaining))
                .Str("countdownUnit", "own end_turn completions from the current stable turn")
                .Str("timing", "native growth age check occurs before round advance; no combat XP or createdTurn subtraction; normal uninterrupted native turns assumed")
                .Str("authority", "owned UnitDataExtensions.GetAge, UnitData.growthRate and active GameLogicData.GetUnitWillGrowInto; countdown timing gated by verified native GameAssembly")
                .Close();
        }
    }
}
