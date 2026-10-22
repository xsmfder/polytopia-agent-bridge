// Real request failure formatter, new-ticket recovery and mutation-only refusal gates.
using System;
using System.Collections.Generic;
using System.Reflection;
using PolytopiaAgent;

internal static class RequestFailureTests
{
    private const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static readonly BridgeMod Host=new BridgeMod();
    private static int passed;
    private static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    private static object Call(string name,params object[] args){return typeof(BridgeMod).GetMethod(name,F).Invoke(Host,args);}
    private static void Set(string name,object value){typeof(BridgeMod).GetField(name,F).SetValue(Host,value);}
    private static Dictionary<string,object> Frame(string text){return BridgeJsonCodec.Object(text);}
    private static Dictionary<string,object> Failure(Exception e,bool entered,string stream="s",long previous=0,bool audit=false,long request=55)
    {return Frame((string)Call("UnhandledRequestFailure",e,request,entered,stream,previous,audit));}
    private static BridgeMod.CombatTicket Ticket(long id,long request=55,string stream="s")
    {return new BridgeMod.CombatTicket{Id=id,RequestId=request,Stream=stream};}
    public static int Main()
    {try{Run();Console.WriteLine("ALL PASS "+passed);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
    private static void Run()
    {
        Set("_combatStream","s");Set("_combatFrame",new BridgeMod.CombatFrame{Episode="ep",Me="2"});
        var tickets=(Dictionary<long,BridgeMod.CombatTicket>)typeof(BridgeMod).GetField("_combatTickets",F).GetValue(Host);
        var before=Failure(new FormatException("not_public_payload"),false);
        Check((string)before["errorCode"]=="INVALID_REQUEST_JSON"&&(string)before["outcome"]=="not_dispatched"
            &&object.Equals(before["dispatched"],false),"invalid input before router carries nonexecution proof");
        Check(!before.ContainsKey("requestDetail")&&!before.ContainsKey("settlementTicket")
            &&!((string)before["error"]).Contains("not_public_payload"),"parsing errors expose neither private detail nor a guessed ticket");
        var late=Failure(new FormatException("not_public_payload"),true);
        Check((string)late["errorCode"]=="REQUEST_FAILED"&&(string)late["outcome"]=="unknown"
            &&!late.ContainsKey("dispatched")&&!late.ContainsKey("accepted"),"late FormatException cannot masquerade as malformed input before dispatch");
        Check(!late.ContainsKey("requestDetail")&&object.Equals(late["diagnosticsWithheld"],true),"normal late error withholds raw exception detail");
        var audit=Failure(new InvalidOperationException("diagnostic only"),true,audit:true);
        Check((string)audit["requestDetail"]=="InvalidOperationException: diagnostic only","explicit routed audit preserves diagnostic exception text");
        Check(!Failure(new FormatException("untrusted audit flag"),false,audit:true).ContainsKey("requestDetail"),
            "unvalidated request cannot grant its own diagnostic output flag");
        tickets[7]=Ticket(7);
        Check(!Failure(new Exception(),true,previous:7).ContainsKey("settlementTicket"),"old same-request-ID ticket is not reused for new failure");
        tickets[8]=Ticket(8,56);tickets[9]=Ticket(9);
        Check(long.Parse(BridgeJsonCodec.Scalar(Failure(new Exception(),true,previous:7)["settlementTicket"]))==9,
            "new unique matching request ticket is returned without sampling native state");
        tickets[10]=Ticket(10);
        Check(!Failure(new Exception(),true,previous:7).ContainsKey("settlementTicket"),"ambiguous new tickets remain unknown without arbitrary selection");
        Check(!Failure(new Exception(),false,previous:7).ContainsKey("settlementTicket"),"pre-router exception cannot acquire execution ticket");
        tickets.Clear();Set("_combatStream","new");tickets[1]=Ticket(1,55,"new");
        Check(long.Parse(BridgeJsonCodec.Scalar(Failure(new Exception(),true,stream:"s",previous:99)["settlementTicket"]))==1,
            "new stream watermark permits its actual first allocated ticket");
        Check(!Failure(new Exception(),true,stream:"s",previous:99,request:0).ContainsKey("settlementTicket"),
            "missing or untrusted request zero never binds a ticket");
        tickets.Clear();tickets[0]=Ticket(0,55,"new");
        Check(!Failure(new Exception(),true,stream:"s",previous:99).ContainsKey("settlementTicket"),"invalid ticket zero is not exposed");
        var args=new Dictionary<string,string>();
        var unit=Frame((string)Call("ExecuteUnitAction",args));
        Check((string)unit["errorCode"]=="NO_ACTIVE_GAME"&&object.Equals(unit["dispatched"],false),
            "actual unit mutation gate reports nonexecution before lookup or callback");
        var tile=Frame((string)Call("TileActionsPublic",args,true));
        Check(object.Equals(tile["dispatched"],false),"actual tile mutation gate reports nonexecution");
        var read=Frame((string)Call("TileActionsPublic",args,false));
        Check(!read.ContainsKey("dispatched")&&(string)read["verdict"]=="unknown","passive tile read keeps read-error semantics");
        var tribe=Frame((string)Call("TribeDiplomacyPublic","peace_response","1","false"));
        Check((string)tribe["schema"]=="tribe-action-1"&&object.Equals(tribe["dispatched"],false),
            "actual diplomacy mutation gate retains schema and proves no submission");
        foreach(string input in new[]{"true","TRUE","false","0","1"})
        {
            object[] a={input,true,null};Check((bool)Call("TryActionBoolean",a),"explicit supported wire boolean accepted: "+input);
            Check((bool)a[2]==(input=="true"||input=="TRUE"||input=="1"),"wire boolean value retained: "+input);
        }
        object[] invalid={"typo",true,null};Check(!(bool)Call("TryActionBoolean",invalid),"typo cannot default to accepting peace");
        object[] missing={"",true,null};Check((bool)Call("TryActionBoolean",missing)&&(bool)missing[2],"documented legacy raw default remains explicit");
        Check((string)Call("DefaultableActionArgument","{}","accept")=="","absent legacy field retains declared default");
        foreach(string raw in new[]{"{\"accept\":null}","{\"accept\":{}}","{\"accept\":[]}","{\"accept\":\"\"}"})
        {
            string parsed=(string)Call("DefaultableActionArgument",raw,"accept");object[] a={parsed,true,null};
            Check(!(bool)Call("TryActionBoolean",a),"explicit null/structured/empty field cannot default to accepting peace");
        }
        Check((string)Call("DefaultableActionArgument","{\"accept\":false}","accept")=="false", "native wire false remains explicit false");
    }
}
