using System;
using System.Collections;
using System.Collections.Generic;
using PolytopiaAgent;

namespace CymantiDecomposeFixture
{
    public class ImprovementData { public enum Type { Fungi, Mycelium, City } }
    public enum ImprovementEffect { decomposing=0, robbed=1 }
    public enum UnrelatedEffect { decomposing=0 }
    public class Improvement
    {
        public object type=ImprovementData.Type.Fungi, owner=(byte)0, level=(ushort)2;
        public object effects=new List<ImprovementEffect>();
    }
    public class Tile { public object owner=(byte)1, improvement=new Improvement(); }
    public class DecomposeAction { public string PlayerId="1", Coordinates="(7,7)"; }
    public class DestroyAction { public string PlayerId="1", Coordinates="(7,7)"; }
}

internal static class CymantiDecomposeTests
{
    static int count;
    static void Check(bool ok,string label){if(!ok)throw new Exception(label);count++;}
    static Dictionary<string,object> Parse(string json){return BridgeJsonCodec.Object(json);}
    static string State(bool? decomposing)
    {
        var tile=new CymantiDecomposeFixture.Tile();var improvement=(CymantiDecomposeFixture.Improvement)tile.improvement;
        improvement.effects=decomposing.HasValue
            ? (object)(decomposing.Value?new[]{CymantiDecomposeFixture.ImprovementEffect.decomposing}:new CymantiDecomposeFixture.ImprovementEffect[0]) : null;
        return BridgeMod.CymantiOwnedBuildingState(tile);
    }
    static BridgeMod.CombatFrame Frame(string state)
    {
        var frame=new BridgeMod.CombatFrame{Episode="decompose-case",Me="1",Turn="9",Complete=true};
        if(state!=null)frame.OwnBuildingStates["(7,7)"]=state;
        return frame;
    }
    static BridgeMod.CombatTicket Ticket(BridgeMod.CombatFrame frame)
    {return new BridgeMod.CombatTicket{Before=frame,Action="decompose",Coord="(7,7)",Accepted=true,Dispatched=true};}
    static string Confirmation(BridgeMod.CombatTicket ticket,BridgeMod.CombatFrame frame)
    {string basis;return BridgeMod.CymantiDecomposeConfirmation(ticket,frame,out basis);}
    public static int Main(){try{Run();Console.WriteLine("PASS CymantiDecomposeTests assertions="+count);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
    static void Run()
    {
        var tile=new CymantiDecomposeFixture.Tile();var improvement=(CymantiDecomposeFixture.Improvement)tile.improvement;
        foreach(var kind in new[]{CymantiDecomposeFixture.ImprovementData.Type.City,CymantiDecomposeFixture.ImprovementData.Type.Mycelium,CymantiDecomposeFixture.ImprovementData.Type.Fungi})
        {
            improvement.type=kind;improvement.owner=(byte)0;
            var nativeState=Parse(BridgeMod.CymantiOwnedBuildingState(tile));
            Check((string)nativeState["owner"]=="1" && (string)nativeState["type"]==kind.ToString(),"native zero improvement owner does not veto actual owned tile: "+kind);
            Check((string)nativeState["authority"]=="owned-TileData.owner+improvement.type/level/effects","ownership authority explicitly names TileData.owner");
        }
        improvement.type=CymantiDecomposeFixture.ImprovementData.Type.Fungi;improvement.owner=(byte)1;
        Check((string)Parse(BridgeMod.CymantiOwnedBuildingState(tile))["owner"]=="1","matching nonzero improvement owner remains supported");
        foreach(object unknown in new object[]{null,"0",false,-1,256})
        {
            improvement.owner=unknown;
            Check(BridgeMod.CymantiOwnedBuildingState(tile)==null,"unknown/malformed secondary owner is not silently treated as native zero");
        }
        improvement.owner=(byte)0;
        var row=Parse(BridgeMod.CymantiOwnedBuildingState(tile));
        Check((string)row["type"]=="Fungi" && BridgeJsonCodec.Scalar(row["level"])=="2" && !(bool)row["decomposing"],"real owned type/level and complete empty effects are retained");
        improvement.effects=new[]{CymantiDecomposeFixture.ImprovementEffect.decomposing,CymantiDecomposeFixture.ImprovementEffect.robbed};
        row=Parse(BridgeMod.CymantiOwnedBuildingState(tile));
        Check((bool)row["decomposing"] && ((IList)row["effects"]).Count==2,"zero-valued native decomposing is present, not numeric false");
        foreach(object bad in new object[]{null,new[]{"decomposing"},new[]{0},new[]{CymantiDecomposeFixture.UnrelatedEffect.decomposing},new[]{(CymantiDecomposeFixture.ImprovementEffect)99}})
        {
            improvement.effects=bad;row=Parse(BridgeMod.CymantiOwnedBuildingState(tile));
            Check(row["effects"]==null && row["decomposing"]==null,"unknown or malformed native effects never mean clear");
        }
        improvement.effects=new[]{CymantiDecomposeFixture.ImprovementEffect.decomposing,CymantiDecomposeFixture.ImprovementEffect.decomposing};
        Check(Parse(BridgeMod.CymantiOwnedBuildingState(tile))["decomposing"]==null,"duplicate malformed effects cannot certify state");
        improvement.owner=(byte)2;Check(BridgeMod.CymantiOwnedBuildingState(tile)==null,"explicit foreign nonzero improvement owner remains conservatively rejected");improvement.owner=(byte)0;
        tile.owner=(byte)0;Check(BridgeMod.CymantiOwnedBuildingState(tile)==null,"neutral tile cannot provide own building snapshot");tile.owner=(byte)1;
        improvement.type="Fungi";Check(BridgeMod.CymantiOwnedBuildingState(tile)==null,"untyped enum name is not native type evidence");improvement.type=CymantiDecomposeFixture.ImprovementData.Type.Fungi;
        improvement.level=true;Check(Parse(BridgeMod.CymantiOwnedBuildingState(tile))["level"]==null,"boolean cannot become building level");
        tile.improvement=null;Check(BridgeMod.CymantiOwnedBuildingState(tile)==null,"absent improvement is absent, not decomposed");

        var before=Frame(State(false));var after=Frame(State(true));var action=new CymantiDecomposeFixture.DecomposeAction();var ticket=Ticket(before);
        Check(BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"requested own false-state building binds exact native root");
        Check(!BridgeMod.CymantiDecomposeRecord(ticket,before,after,new CymantiDecomposeFixture.DecomposeAction()),"same fields on unrelated action cannot prove execution");
        Check(BridgeMod.CymantiDecomposeRecord(ticket,before,after,action),"exact native root proves requested building false-to-true");
        Check(Confirmation(ticket,after)=="confirmed","executed native state transition confirms Decompose");
        row=Parse(BridgeMod.CymantiDecomposeOutcome(ticket,after));
        Check((bool)row["nativeExecuted"] && (string)row["effectConfirmation"]=="confirmed" && row["after"]!=null,"outcome retains before/after effect instead of Destroy semantics");
        Check(!BridgeMod.CymantiDecomposeBind(ticket,before,new CymantiDecomposeFixture.DecomposeAction(),"DecomposeAction"),"second root cannot acquire bound ticket");
        Check(Confirmation(ticket,Frame(State(false)))=="unknown","reverted final effect cannot confirm old transition");
        Check(Confirmation(ticket,Frame(null))=="unknown","direct building removal is not silently treated as Decompose");
        Check(Parse(BridgeMod.CymantiDecomposeOutcome(ticket,Frame(null)))["after"]==null,"unknown final state does not emit successful applied state");

        ticket=Ticket(before);
        Check(!BridgeMod.CymantiDecomposeBind(ticket,before,new CymantiDecomposeFixture.DestroyAction(),"DecomposeAction"),"different native type cannot use field-shaped root");
        Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DestroyAction"),"wrong declared native kind cannot bind");
        foreach(string foreign in new[]{"0","2",null})
        {action.PlayerId=foreign;Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"neutral foreign or unknown player cannot bind");}action.PlayerId="1";
        action.Coordinates="(7,8)";Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"another tile cannot bind requested building");action.Coordinates="(7,7)";
        ticket.Action="destroy";Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction") && Confirmation(ticket,after)==null,"Destroy is a different contract");ticket.Action="decompose";
        Check(!BridgeMod.CymantiDecomposeBind(Ticket(Frame(State(true))),Frame(State(true)),action,"DecomposeAction"),"already decomposing is not newly applied");
        Check(!BridgeMod.CymantiDecomposeBind(Ticket(Frame(State(null))),Frame(State(null)),action,"DecomposeAction"),"unknown prior effect is not false");
        var foreignState=State(false).Replace("\"owner\":\"1\"","\"owner\":\"2\"");
        Check(!BridgeMod.CymantiDecomposeBind(Ticket(Frame(foreignState)),Frame(foreignState),action,"DecomposeAction"),"opponent snapshot cannot masquerade in own dictionary");

        foreach(string alteration in new[]{"episode","seat","turn","incomplete"})
        {
            var other=Frame(State(false));
            if(alteration=="episode")other.Episode="other";
            if(alteration=="seat")other.Me="2";
            if(alteration=="turn")other.Turn="10";
            if(alteration=="incomplete")other.Complete=false;
            ticket=Ticket(before);Check(!BridgeMod.CymantiDecomposeBind(ticket,other,action,"DecomposeAction"),"binding refuses "+alteration);
        }
        ticket=Ticket(before);Check(BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"fresh test root bound");
        Check(!BridgeMod.CymantiDecomposeRecord(ticket,before,Frame(null),action) && ticket.CymantiDecompose.Executed,"executed but direct removal remains unsupported");
        Check(Confirmation(ticket,Frame(null))=="unknown","accepted execution is not effect success");
        Check(!BridgeMod.CymantiDecomposeRecord(ticket,before,Frame(State(null)),action),"unreadable applied effects cannot prove transition");
        var altered=Frame(State(true).Replace("\"level\":2","\"level\":3"));
        Check(!BridgeMod.CymantiDecomposeRecord(ticket,before,altered,action),"different building level cannot substitute target state");
        altered=Frame(State(true).Replace("\"type\":\"Fungi\"","\"type\":\"Mycelium\""));
        Check(!BridgeMod.CymantiDecomposeRecord(ticket,before,altered,action),"different improvement cannot substitute target");
        var spoof=Frame(State(false).Replace("\"decomposing\":false","\"decomposing\":true"));
        Check(!BridgeMod.CymantiDecomposeBind(Ticket(spoof),spoof,action,"DecomposeAction"),"boolean contradicting effect list cannot bind");
        var oldAuthority=Frame(State(false).Replace("owned-TileData.owner+improvement.type/level/effects","owned-TileData.improvement.type/owner/level/effects"));
        Check(!BridgeMod.CymantiDecomposeBind(Ticket(oldAuthority),oldAuthority,action,"DecomposeAction"),"old incorrect ownership authority cannot supply new decomposition proof");
        ticket=Ticket(before);ticket.Accepted=false;Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"rejected ticket cannot bind");
        ticket=Ticket(before);ticket.FinalFrame=after;Check(!BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction"),"frozen final ticket cannot gain new evidence");
        ticket=Ticket(before);BridgeMod.CymantiDecomposeBind(ticket,before,action,"DecomposeAction");BridgeMod.CymantiDecomposeRecord(ticket,before,after,action);
        ticket.Dispatched=false;Check(Confirmation(ticket,after)=="unknown","undispatched ticket cannot claim completion");ticket.Dispatched=true;
        ticket.FinalFrame=after;Check(Confirmation(ticket,after)=="confirmed","already frozen valid final evidence stays confirmable");
    }
}
