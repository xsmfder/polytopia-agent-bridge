using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public sealed class StartScreen_UI2
{
    public sealed class Button { public Obj gameObject = new Obj(); public bool ButtonEnabled { get; set; } public Button() { ButtonEnabled=true; } }
    public sealed class Obj { public bool activeInHierarchy = true; }
    public Button resumeButton = new Button();
}
public sealed class UIManager
{
    public static UIManager instance = new UIManager();
    public object Screen = new StartScreen_UI2();
    public object GetCurrentScreen() { return Screen; }
}
public static class PopupManager
{
    public static bool Showing;
    public static bool get_PopupShowing() { return Showing; }
}
namespace UnityEngine { public static class Application { public static int Quits; public static void Quit() { Quits++; } } }
namespace Il2Cpp.UnityEngine { public static class Application { public static int Quits; public static void Quit() { Quits++; } } }
public static class GameLifecycleTests
{
    public sealed class Client { public object GameState; }
    public sealed class Manager
    {
        public static Manager instance = new Manager();
        public static object GameState { get; set; }
        public Client client = new Client();
        public bool isLoadingGame;
        public int Loads;
        public bool Throw;
        public void ResumeSingleplayerGame() { Loads++; if (Throw) throw new InvalidOperationException(); }
    }
    private static readonly BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static int passed;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static void Set(object h, string f, object v) { h.GetType().GetField(f,F).SetValue(h,v); }
    private static object Call(BridgeMod h, string name, params object[] args) { return typeof(BridgeMod).GetMethod(name,F).Invoke(h,args); }
    private static string Act(BridgeMod h, string op, string id, string player = "1")
    { return (string)Call(h,"GameLifecycleAction",op,id,"","",player); }
    private static BridgeMod Host()
    {
        Manager.GameState = null; Manager.instance = new Manager(); UIManager.instance = new UIManager(); PopupManager.Showing = false;
        var h = new BridgeMod(); Set(h,"_fixedControlledSeat",(byte?)1); Set(h,"_gameManagerType",typeof(Manager));
        Set(h,"_gameStateProp",typeof(Manager).GetProperty("GameState")); return h;
    }
    private static object SaveReceipt(BridgeMod h, string path, string stamp)
    {
        var type = typeof(BridgeMod).GetNestedType("GameOperation",BindingFlags.NonPublic);
        var row = Activator.CreateInstance(type,true);
        Set(row,"Id",Guid.NewGuid().ToString("N")); Set(row,"Operation","save"); Set(row,"Stage","saving");
        Set(row,"Path",path); Set(row,"BeforeStamp",stamp); Set(row,"Submitted",true);
        return row;
    }
    public static int Main()
    {
        try { Run(); Console.WriteLine("ALL PASS " + passed); return 0; }
        catch(Exception e) { Console.WriteLine(e); return 1; }
    }
    private static void Run()
    {
        Check(BridgeMod.IsSetupAdminCommand("game_lifecycle") && BridgeMod.IsSetupAdminCommand("game_lifecycle_status"),"formal administration commands");
        var h=Host(); string id=Guid.NewGuid().ToString("N");
        Check(Act(h,"load_singleplayer",id,"2").Contains("CONTROLLED_SEAT_MISMATCH"),"wrong player refused");
        Check(Act(h,"load_singleplayer","invalid").Contains("INVALID_OPERATION_ID"),"invalid operation id refused");
        PopupManager.Showing=true;
        Check(Act(h,"load_singleplayer",id).Contains("NATIVE_POPUP_REQUIRES_ATTENTION"),"popup preserved");
        PopupManager.Showing=false;
        ((StartScreen_UI2)UIManager.instance.Screen).resumeButton.ButtonEnabled=false;
        Check(Act(h,"load_singleplayer",id).Contains("SINGLEPLAYER_RESUME_UNAVAILABLE"),"disabled resume refused");
        ((StartScreen_UI2)UIManager.instance.Screen).resumeButton.ButtonEnabled=true;
        Check(Act(h,"load_singleplayer",id).Contains("submitted") && Manager.instance.Loads==1,"native load submitted once");
        Check(Act(h,"load_singleplayer",id).Contains("submitted") && Manager.instance.Loads==1,"duplicate id only polls");
        Check(Act(h,"quit",id).Contains("OPERATION_ID_CONFLICT"),"id cannot change operation");
        Check(Act(h,"quit",Guid.NewGuid().ToString("N")).Contains("GAME_OPERATION_PENDING"),"pending load blocks exit");
        Check(((string)Call(h,"GameLifecycleStatus",id,"1")).Contains("submitted"),"status is not load completion");
        Check(Manager.instance.Loads==1,"status polling never repeats callback");
        h=Host(); Manager.instance.Throw=true; id=Guid.NewGuid().ToString("N");
        Check(Act(h,"load_singleplayer",id).Contains("unknown"),"post-invocation failure uncertain");
        Act(h,"load_singleplayer",id); Check(Manager.instance.Loads==1,"uncertain load never replayed");
        h=Host(); Manager.GameState=new object();
        Check(Act(h,"load_singleplayer",Guid.NewGuid().ToString("N")).Contains("ACTIVE_GAME_PRESERVED"),"active save not overwritten by load");
        Manager.GameState=null;
        Check(Act(h,"save",Guid.NewGuid().ToString("N")).Contains("CONTROLLED_GAME_NOT_READY"),"cannot save absent match");
        string path=Path.Combine(Path.GetTempPath(),"bridge-lifecycle-"+Guid.NewGuid().ToString("N")+".state");
        try
        {
            object row=SaveReceipt(h,path,"missing"); Call(h,"PollGameOperation",row);
            Check(((string)Call(h,"GameOperationJson",row)).Contains("submitted"),"missing file is not saved");
            File.WriteAllText(path,""); Call(h,"PollGameOperation",row);
            Check(((string)Call(h,"GameOperationJson",row)).Contains("submitted"),"empty file is not a confirmed save");
            File.WriteAllText(path,"native state"); Call(h,"PollGameOperation",row);
            Check(((string)Call(h,"GameOperationJson",row)).Contains("confirmed"),"new native save file confirms save");
            row=SaveReceipt(h,path,BridgeMod.SaveFileStamp(path)); Call(h,"PollGameOperation",row);
            Check(((string)Call(h,"GameOperationJson",row)).Contains("submitted"),"old unchanged save cannot confirm");
            File.WriteAllText(path,"new native state"); PopupManager.Showing=true; Call(h,"PollGameOperation",row);
            Check(((string)Call(h,"GameOperationJson",row)).Contains("POPUP_AFTER_SAVE"),"save error popup blocks success");
        }
        finally { if(File.Exists(path)) File.Delete(path); }
        h=Host(); UnityEngine.Application.Quits=0;
        string quit=Act(h,"quit",Guid.NewGuid().ToString("N"));
        Check(quit.Contains("quit_scheduled") && UnityEngine.Application.Quits==0,"quit reply precedes deferred native shutdown");
        Call(h,"TickGameOperation"); Check(UnityEngine.Application.Quits==0,"quit delay preserves response flush");
        object pending=typeof(BridgeMod).GetField("_gameOperation",F).GetValue(h);
        Set(pending,"QuitAfterTime",DateTime.UtcNow.AddSeconds(-1)); Set(h,"_tick",100L);
        Set(h,"_nextGameOperationPoll",DateTime.MinValue); Manager.GameState=new object();
        Call(h,"TickGameOperation");
        Check(UnityEngine.Application.Quits==0 && ((string)Call(h,"GameOperationJson",pending)).Contains("GAME_CHANGED_BEFORE_QUIT"),
            "new game between reply and quit is preserved");
        h=Host(); Act(h,"quit",Guid.NewGuid().ToString("N"));
        pending=typeof(BridgeMod).GetField("_gameOperation",F).GetValue(h);
        Set(pending,"QuitAfterTime",DateTime.UtcNow.AddSeconds(-1)); Set(h,"_tick",100L);
        Set(h,"_nextGameOperationPoll",DateTime.MinValue); Call(h,"TickGameOperation");
        Check(Il2Cpp.UnityEngine.Application.Quits==1 && UnityEngine.Application.Quits==0,
            "exact IL2CPP Quit binding works with both Unity types loaded");
        Set(h,"_nextGameOperationPoll",DateTime.MinValue); Call(h,"TickGameOperation");
        Check(Il2Cpp.UnityEngine.Application.Quits==1,"native Quit invoked at most once");
        string directory=Path.Combine(Path.GetTempPath(),"bridge-durable-"+Guid.NewGuid().ToString("N"));
        try
        {
            string operationId=Guid.NewGuid().ToString("N");
            BridgeMod.PersistGameOperationIn(directory,operationId,"{\"phase\":\"first\"}");
            string receiptPath=Path.Combine(directory,operationId+".json");
            Check(File.ReadAllText(receiptPath).Contains("first"),"quit progress is durable before exit");
            BridgeMod.PersistGameOperationIn(directory,operationId,"{\"phase\":\"quit_requested\"}");
            Check(File.ReadAllText(receiptPath).Contains("quit_requested") && Directory.GetFiles(directory).Length==1,
                "durable receipt atomically replaces earlier progress without leftover temporary files");
            bool invalid=false;
            try { BridgeMod.PersistGameOperationIn(directory,"../escape","{}"); } catch(ArgumentException) { invalid=true; }
            Check(invalid,"receipt filename cannot escape its directory");
        }
        finally
        {
            if(Directory.Exists(directory)) { foreach(string file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        }
    }
}
