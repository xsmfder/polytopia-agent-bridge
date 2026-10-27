// Real first-arrow flag semantics plus strict ability, identity and proof boundaries.
using System;
using System.Collections.Generic;
using PolytopiaAgent;

internal static class CymantiRuntimeTests
{
    static int passed;
    static void Check(bool yes,string message)
    { if(!yes)throw new Exception(message);passed++;Console.WriteLine("PASS "+message); }
    static BridgeMod.CombatFrame Frame()
    { return new BridgeMod.CombatFrame{Episode="native-phychi",Me="1",Turn="7",Complete=true}; }
    static void Main()
    {
        Check(BridgeMod.CRHookTargetAllowed(typeof(SwarmCommand),typeof(ActionBaseFixture),typeof(CommandBaseFixture)),
            "audited SwarmCommand derives from CommandBase and must actually install");
        Check(BridgeMod.CRHookTargetAllowed(typeof(AttackActionFixture),typeof(ActionBaseFixture),typeof(CommandBaseFixture)),
            "existing ActionBase hooks remain allowed");
        Check(!BridgeMod.CRHookTargetAllowed(typeof(OtherCommand),typeof(ActionBaseFixture),typeof(CommandBaseFixture)),
            "unknown CommandBase types are not swept into hooks");
        Check(!BridgeMod.CRHookTargetAllowed(typeof(SwarmCommand),typeof(ActionBaseFixture),null),
            "unverified CommandBase cannot authorize command hook");
        Check(!BridgeMod.CRHookTargetAllowed(typeof(Invalid.SwarmCommand),typeof(ActionBaseFixture),typeof(CommandBaseFixture)),
            "matching name without native command inheritance is insufficient");
        var before=Frame();var after=Frame();
        var actor=new BridgeMod.CombatUnitView{Id="10",Owner="1",Type="Phychi",Coord="(6,6)",
            Health=50,Attacked=false,CanAttack=true,DoubleAttack=true,Effects="[\"DoubleReady\"]"};
        var survivor=new BridgeMod.CombatUnitView{Id="10",Owner="1",Type="Phychi",Coord="(6,6)",
            Health=50,Attacked=false,CanAttack=true,DoubleAttack=true,Effects="[]"};
        var target=new BridgeMod.CombatUnitView{Id="2",Owner="2",Type="Warrior",Coord="(4,5)",Health=100};
        var damaged=new BridgeMod.CombatUnitView{Id="2",Owner="2",Type="Warrior",Coord="(4,5)",Health=90};
        before.Units[actor.Id]=actor;before.Units[target.Id]=target;
        after.Units[survivor.Id]=survivor;after.Units[damaged.Id]=damaged;
        var ticket=new BridgeMod.CombatTicket{Action="attack",UnitId=actor.Id,Coord=target.Coord,
            Before=before,Accepted=true,Dispatched=true,NativePrimaryBefore=target,NativePrimaryAfter=damaged};
        ticket.NativeAttackTargets.Add(target.Id);
        string basis;
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)=="confirmed"
            && basis.Contains("DoubleReady"),"actual first arrow consumes DoubleReady and keeps attack available");
        Check(survivor.Attacked==false && survivor.CanAttack==true,"truthful remaining attack flags preserved");
        ticket.NativeAttackTargets.Clear();
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)==null,"no exact native primary attack means unknown");
        ticket.NativeAttackTargets.Add(target.Id);ticket.NativePrimaryAfter=null;
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)==null,"effect flag alone cannot replace actual damage");
        ticket.NativePrimaryAfter=damaged;survivor.DoubleAttack=null;
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)==null,"unknown native capability cannot confirm double attack");
        survivor.DoubleAttack=true;survivor.Effects="[\"DoubleReady\"]";
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)==null,"unchanged DoubleReady cannot prove consumption");
        survivor.Effects=null;
        Check(!BridgeMod.CRDoubleAttackConsumed(actor,survivor),"unreadable effects stay unknown");
        survivor.Effects="[]";survivor.Owner="2";
        Check(!BridgeMod.CRDoubleAttackConsumed(actor,survivor),"foreign survivor cannot replace own attacker");
        survivor.Owner="1";after.Episode="other";
        Check(BridgeMod.CRAttackEffectConfirmation(ticket,after,out basis)==null,"double attack proof does not cross episode");

        var moth=new BridgeMod.CombatUnitView{Id="30",Owner="1",Type="Moth",Coord="(2,3)",Infiltrate=true};
        before.Units[moth.Id]=moth;
        var infiltrate=new BridgeMod.CombatTicket{Before=before,Action="attack",UnitId=moth.Id,Coord="(2,2)",Accepted=true};
        Check(BridgeMod.CRInfiltrationActorMatches(infiltrate,moth,"1","(2,2)"),"actual Moth Infiltrate capability binds without type whitelist");
        moth.Infiltrate=false;
        Check(!BridgeMod.CRInfiltrationActorMatches(infiltrate,moth,"1","(2,2)"),"Moth name without actual capability is insufficient");
        moth.Infiltrate=true;
        Check(!BridgeMod.CRInfiltrationActorMatches(infiltrate,moth,"2","(2,2)"),"native player must match known own actor");

        var poisoned=new BridgeMod.CombatUnitView{Id="40",Owner="1",Coord="(1,1)",Health=100,Effects="[\"Poisoned\"]"};
        var cured=new BridgeMod.CombatUnitView{Id="40",Owner="1",Coord="(1,1)",Health=100,Effects="[]"};
        Check(BridgeMod.CRHealingChanged(poisoned,cured),"native Poisoned removal confirms pure detox at full HP");
        poisoned.Effects="[\"Poison\"]";
        Check(!BridgeMod.CRHealingChanged(poisoned,cured),"incorrect old Poison name cannot substitute for actual effect");
        Console.WriteLine("CymantiRuntimeTests: "+passed+" passed");
    }
    public class ActionBaseFixture{}
    public class CommandBaseFixture{}
    public class SwarmCommand:CommandBaseFixture{}
    public class OtherCommand:CommandBaseFixture{}
    public class AttackActionFixture:ActionBaseFixture{}
    public class Invalid{public class SwarmCommand{}}
}
