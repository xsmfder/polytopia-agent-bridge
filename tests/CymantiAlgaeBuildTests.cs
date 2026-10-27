using System;
using PolytopiaAgent;

public sealed class ImprovementData { public enum Type { Algae, Fungi } }
public sealed class BuildAction
{
    public byte PlayerId=1;
    public string Coordinates="(8,9)";
    public object Type=ImprovementData.Type.Algae;
}
internal static class CymantiAlgaeBuildTests
{
    static int count;
    static void Check(bool valid,string label){if(!valid)throw new Exception(label);count++;}
    static BridgeMod.CombatFrame Frame(string state)
    {
        var frame=new BridgeMod.CombatFrame{Complete=true,Me="1",Turn="26",Episode="algae"};
        if(state!=null)frame.OwnTileStates["(8,9)"]=state;return frame;
    }
    static BridgeMod.CombatTicket Ticket(BridgeMod.CombatFrame before)
    {return new BridgeMod.CombatTicket{Before=before,Action="build",Arg="Algae",Coord="(8,9)",Accepted=true,Dispatched=true};}
    static string Confirm(BridgeMod.CombatTicket ticket,BridgeMod.CombatFrame current)
    {string basis;return BridgeMod.CymantiAlgaeBuildConfirmation(ticket,current,out basis);}
    static void Main()
    {
        const string absent="{\"terrain\":\"Water\",\"effects\":[]}";
        const string present="{\"terrain\":\"Water\",\"effects\":[\"Algae\"]}";
        var before=Frame(absent);var after=Frame(present);var ticket=Ticket(before);var root=new BuildAction();
        Check(Confirm(ticket,after)=="unknown","a changed tile alone cannot fabricate native build execution");
        Check(BridgeMod.CymantiAlgaeBuildBind(ticket,before,root,"BuildAction"),"known own native Algae build binds");
        Check(!BridgeMod.CymantiAlgaeBuildRecord(ticket,before,new BuildAction()),"another identical build object cannot claim execution");
        Check(BridgeMod.CymantiAlgaeBuildRecord(ticket,before,root),"exact native root completion recorded");
        Check(Confirm(ticket,before)=="unknown","root completion without Algae effect remains unknown");
        before.OwnTileStates["(7,9)"]=present;
        Check(Confirm(ticket,before)=="unknown","another tile gaining Algae cannot confirm the named target");
        Check(Confirm(ticket,after)=="confirmed","named tile Algae absent-to-present confirms even without an improvement object");
        ticket.FinalFrame=after;
        Check(Confirm(ticket,after)=="confirmed","production frozen final frame retains completed native Algae proof");
        Check(BridgeMod.CymantiAlgaeBuildOutcome(ticket,after).Contains("\"nativeBuildExecuted\":true"),"structured native execution proof retained");
        Check(!BridgeMod.CymantiAlgaeBuildBind(ticket,before,root,"BuildAction"),"frozen final ticket cannot bind another native action");
        ticket.FinalFrame=null;
        var resourceOnly=Frame("{\"terrain\":\"Water\",\"effects\":[],\"resource\":\"Fruit\"}");
        Check(Confirm(ticket,resourceOnly)=="unknown","resource generation without Algae is insufficient");
        Check(Confirm(ticket,Frame(null))=="unknown","foreign or unreadable target scope cannot confirm");
        var changed=Frame(present);changed.Turn="27";Check(Confirm(ticket,changed)=="unknown","later-turn changes do not confirm an old build");
        ticket.Accepted=false;Check(Confirm(ticket,after)=="unknown","rejected command cannot be confirmed");ticket.Accepted=true;
        ticket.Dispatched=false;Check(Confirm(ticket,after)=="unknown","undispatched intent cannot be confirmed");ticket.Dispatched=true;
        var foreign=new BuildAction{PlayerId=2};Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(before),before,foreign,"BuildAction"),"foreign builder rejected");
        var wrong=new BuildAction{Coordinates="(7,9)"};Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(before),before,wrong,"BuildAction"),"wrong target rejected");
        wrong=new BuildAction{Type=ImprovementData.Type.Fungi};Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(before),before,wrong,"BuildAction"),"other actual improvement kind rejected");
        wrong.Type="Algae";Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(before),before,wrong,"BuildAction"),"text is not native improvement authority");
        Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(after),after,root,"BuildAction"),"pre-existing Algae cannot count as new construction");
        var unknown=Frame("{\"terrain\":\"Water\",\"effects\":null}");
        Check(!BridgeMod.CymantiAlgaeBuildBind(Ticket(unknown),unknown,root,"BuildAction"),"unknown before effect is not absent");
        var other=Ticket(before);other.Arg="Fungi";Check(Confirm(other,after)==null,"other build types retain their existing settlement path");
        Console.WriteLine("CymantiAlgaeBuildTests: "+count+" passed");
    }
}
