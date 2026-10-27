// Cymanti effect semantics; only known own mechanical state may prove consumption.
using System;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal static bool CRHookTargetAllowed(Type type,Type actionBase,Type commandBase)
        {
            return type!=null && actionBase!=null && (actionBase.IsAssignableFrom(type)
                || type.Name=="SwarmCommand" && commandBase!=null && commandBase.IsAssignableFrom(type));
        }

        internal static bool CRDoubleAttackConsumed(CombatUnitView before, CombatUnitView after)
        {
            return before != null && after != null && before.Id == after.Id
                && before.Owner == after.Owner && before.DoubleAttack == true && after.DoubleAttack == true
                && before.Attacked == false && after.Attacked == false && after.CanAttack == true
                && after.Health > 0 && before.Effects != null && after.Effects != null
                && before.Effects.IndexOf("\"DoubleReady\"",StringComparison.Ordinal) >= 0
                && after.Effects.IndexOf("\"DoubleReady\"",StringComparison.Ordinal) < 0;
        }
    }
}
