// Production menu endpoints with native-shaped fixtures; no new live match claimed.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

public class SetupGo {public bool activeInHierarchy=true;}
public class SetupButton {public SetupGo gameObject=new SetupGo(); public string Text="native label"; public bool ButtonEnabled {get;set;} public SetupButton(){ButtonEnabled=true;}}
public enum SetupDifficulty {Normal=2, FutureDifficulty=41}
public enum SetupPreset {Continents=3, Dryland=7, Lakes=8, FutureMap=77}
public enum SetupRules {Might=4,Sandbox=6,FutureRule=88}
public enum TribeType {None=0, FutureTribe=25}
public class SetupTribeData {public TribeType type;}
public class TypedTribeKeys
{
    public class Iterator
    {
        private int index=-1;
        public bool MoveNext(){return ++index==0;}
        public TribeType Current {get{return TribeType.FutureTribe;}}
    }
    public Iterator GetEnumerator(){return new Iterator();}
}
public class BoxedTribeDictionary : System.Collections.IEnumerable
{
    public TypedTribeKeys Keys {get{return new TypedTribeKeys();}}
    public int ActualCount=1,BoxedReads,TypedReads;
    public int Count {get{return ActualCount;}}
    public SetupButton this[TribeType key] {get{TypedReads++;return new SetupButton();}}
    public System.Collections.IEnumerator GetEnumerator(){BoxedReads++;return new object[] {new object()}.GetEnumerator();}
}
public class SetupSettings
{
    public string GameType="SinglePlayer",BaseGameMode="Domination";
    public SetupRules RulesGameMode=SetupRules.Might;
    public bool IgnoreSize;
    public int OpponentCount {get;set;}
    private int size=14;
    public int MapSize {get{return size;}set{if(!IgnoreSize)size=value;}}
    public SetupDifficulty Difficulty {get;set;}
    public SetupPreset mapPreset=SetupPreset.Continents;
    public SetupSettings(){Difficulty=SetupDifficulty.Normal;}
}
public class SetupClient {public object GameState; public bool IsReady {get;set;} public bool IsReplay {get;set;}}
public class SetupManager
{
    public static SetupManager instance=new SetupManager();
    public static object GameState {get;set;}
    public static SetupSettings PreliminaryGameSettings {get;set;}
    public bool isLoadingGame;
    public SetupClient client=new SetupClient();
}
public class UIManager
{
    public static UIManager Instance {get;set;}
    public object Current;
    public object GetCurrentScreen(){return Current;}
}
public static class PopupManager
{
    public static bool Showing; public static object Current;
    public static bool get_PopupShowing(){return Showing;}
    public static object GetCurrentPopup(){return Current;}
}
public static class UIBlackFader {public static bool Showing;public static bool IsShowing(){return Showing;}}
public class StartScreen_UI2
{
    public SetupButton startButton=new SetupButton(); public int Calls; public bool Fail;
    private void ClickNewGame(){Calls++;if(Fail)throw new Exception("callback uncertain");UIManager.Instance.Current=new GameModeScreen_UI2();}
}
public class GameModeScreen_UI2
{
    public SetupButton dominationButton=new SetupButton(),perfectionButton=new SetupButton(),creativeButton=new SetupButton(),tutorialButton=new SetupButton();
    public int Calls;
    public void OnDomination(){Calls++;SetupManager.PreliminaryGameSettings.BaseGameMode="Domination";UIManager.Instance.Current=new TribePickerScreen_UI2();}
    public void OnPerfection(){Calls++;SetupManager.PreliminaryGameSettings.BaseGameMode="Perfection";UIManager.Instance.Current=new TribePickerScreen_UI2();}
    public void OnCustom(){Calls++;SetupManager.PreliminaryGameSettings.BaseGameMode="Custom";UIManager.Instance.Current=new TribePickerScreen_UI2();}
}
public class SelectTribePopup
{
    public SetupGo gameObject=new SetupGo();public string Mode="Select"; public SetupButton uiTextButton=new SetupButton();
    public SetupButton SelectButton=new SetupButton();public int ButtonId=(int)TribeType.FutureTribe;
    public SetupButton disableButton=new SetupButton();public bool tribeEnabled=true;public int Toggles;
    public void OnDisableTribe(){Toggles++;tribeEnabled=!tribeEnabled;SelectButton.ButtonEnabled=tribeEnabled;}
    public SetupButton GetButtonById(int id){return id==ButtonId ? SelectButton : null;}
}
public class TribePickerScreen_UI2
{
    public object tribeButtons=new Dictionary<TribeType,SetupButton>();
    public SelectTribePopup popup;
    public SetupTribeData selectedTribe;
    public string selectedSkin="Default";
    public int Calls,Confirms;
    public TribePickerScreen_UI2(){((Dictionary<TribeType,SetupButton>)tribeButtons).Add(TribeType.FutureTribe,new SetupButton());}
    private void OnTribeButtonClicked(TribeType type,TribeType mix)
    {Calls++;selectedTribe=new SetupTribeData {type=type};popup=new SelectTribePopup();PopupManager.Current=popup;PopupManager.Showing=true;}
    private void OnTribeButtonClicked(TribeType type,bool unrelated)
    {throw new Exception("unrelated same-name callback must never be selected");}
    private void OnSelectTribeMainButtonPressed(){Confirms++;PopupManager.Showing=false;UIManager.Instance.Current=new GameSetupScreen_UI2();}
}
public class SetupList
{
    public List<string> Labels=new List<string>(); public List<int> Ids=new List<int>(); public bool Fail;
    public SetupList(params int[] ids){foreach(int n in ids){Ids.Add(n);Labels.Add("label "+n);}}
    public List<string> GetLabels(){return Labels;}
    public int GetId(int i){if(Fail)throw new Exception("unreadable ID");return Ids[i];}
}
public class GameSetupScreen_UI2
{
    public SetupViewFixture view=new SetupViewFixture();
    public SetupList opponentsData=new SetupList(0,5),difficultyData=new SetupList(2,41),mapTypeData=new SetupList(3,77),mapSizeData=new SetupList(14,22);
    public SetupList gameModeData=new SetupList(4,6,88);
    public int Calls,Creates;public bool IgnorePreset;
    private void OnNumberOfOpponentsChanged(int index){Calls++;SetupManager.PreliminaryGameSettings.OpponentCount=opponentsData.GetId(index);SetupManager.PreliminaryGameSettings.MapSize=22;}
    private void OnDifficultyChanged(int index){Calls++;SetupManager.PreliminaryGameSettings.Difficulty=(SetupDifficulty)difficultyData.GetId(index);}
    private void OnGameModeChanged(int index){Calls++;SetupManager.PreliminaryGameSettings.RulesGameMode=(SetupRules)gameModeData.GetId(index);if(SetupManager.PreliminaryGameSettings.RulesGameMode==SetupRules.Sandbox){opponentsData=new SetupList(0);SetupManager.PreliminaryGameSettings.OpponentCount=0;}}
    private void OnMapTypeChanged(int index){Calls++;if(!IgnorePreset)SetupManager.PreliminaryGameSettings.mapPreset=(SetupPreset)mapTypeData.GetId(index);}
    private void OnMapSizeChanged(int index){Calls++;SetupManager.PreliminaryGameSettings.MapSize=mapSizeData.GetId(index);}
    private void OnContinueClicked_StartSingleplayerGame(){Creates++;SetupManager.instance.isLoadingGame=true;}
    private void OnAdvancedSettingsToggleClicked(){Calls++;view.whatToShow|=SetupVisibility.MapTypeList|SetupVisibility.MapSizeList;}
}
[Flags] public enum SetupVisibility {GameModeList=4,OpponentsList=16,DifficultyList=32,MapTypeList=64,MapSizeList=128,AdvancedSettingsToggle=8192,ContinueButton=16384}
public class SetupViewFixture
{
    public SetupVisibility whatToShow=SetupVisibility.OpponentsList|SetupVisibility.DifficultyList|SetupVisibility.MapTypeList|SetupVisibility.MapSizeList|SetupVisibility.AdvancedSettingsToggle|SetupVisibility.ContinueButton;
    public SetupButton continueButton=new SetupButton(),advancedSettingsToggle=new SetupButton();
}
public class ConcealedState
{
    public int Reads;
    public object Settings {get{Reads++;throw new Exception("existing private state must not be read");}}
    public object Map {get{Reads++;throw new Exception("existing private map must not be read");}}
}
internal static class SetupAdminTests
{
    private const BindingFlags F=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static int passed;
    private static JavaScriptSerializer J=new JavaScriptSerializer();
    private static void Check(bool b,string label){if(!b)throw new Exception(label);passed++;Console.WriteLine("PASS "+label);}
    private static object Call(BridgeMod h,string name,params object[] a){return typeof(BridgeMod).GetMethod(name,F).Invoke(h,a);}
    private static void Set(BridgeMod h,string n,object v){typeof(BridgeMod).GetField(n,F).SetValue(h,v);}
    private static Dictionary<string,object> Parse(string s){return J.Deserialize<Dictionary<string,object>>(s);}
    private static Dictionary<string,object> Status(BridgeMod h){return Parse((string)Call(h,"SetupStatus"));}
    private static Dictionary<string,object> Act(BridgeMod h,Dictionary<string,object> v,string id)
    {return Parse((string)Call(h,"SetupAction",id,(string)v["setupSessionId"],v["menuRevision"].ToString()));}
    private static bool Has(Dictionary<string,object> v,string id,bool executable=true)
    {
        foreach(var x in (System.Collections.IList)v["actions"]){var row=(Dictionary<string,object>)x;if((string)row["actionId"]==id && (bool)row["executable"]==executable)return true;}
        return false;
    }
    private static BridgeMod Host(object screen)
    {
        SetupManager.instance=new SetupManager();SetupManager.GameState=null;SetupManager.PreliminaryGameSettings=new SetupSettings();
        UIManager.Instance=new UIManager {Current=screen};PopupManager.Showing=false;PopupManager.Current=null;UIBlackFader.Showing=false;
        var h=new BridgeMod();Set(h,"_gameManagerType",typeof(SetupManager));Set(h,"_gameStateProp",typeof(SetupManager).GetProperty("GameState"));Set(h,"_fixedControlledSeat",(byte?)1);return h;
    }
    public static int Main(){try{Run();Console.WriteLine("ALL PASS "+passed);return 0;}catch(Exception e){Console.WriteLine(e);return 1;}}
    private static void Run()
    {
        var start=new StartScreen_UI2();var h=Host(start);var v=Status(h);
        Check((string)v["phase"]=="start_menu" && Has(v,"start") && start.Calls==0,"status projects physical start button without invoking it");
        Check((int)Status(h)["menuRevision"]==(int)v["menuRevision"],"unchanged polling preserves menu revision");
        Set(h,"_fixedControlledSeat",(byte?)2);Check((string)Act(h,v,"start")["errorCode"]=="SINGLEPLAYER_SEAT_ONE_REQUIRED" && start.Calls==0,"player 2 cannot create solo game");Set(h,"_fixedControlledSeat",(byte?)1);
        Check((string)Parse((string)Call(h,"SetupAction","start","other",v["menuRevision"].ToString()))["errorCode"]=="INVALID_SETUP_CONTEXT","session mismatch rejected");
        var r=Act(h,v,"start");Check((string)r["status"]=="submitted" && !(bool)r["ready"] && start.Calls==1,"normal callback submission is not readiness");
        Check((string)Act(h,v,"start")["status"]=="submitted" && start.Calls==1,"duplicate exact action returns original receipt without replay");
        Check((string)Act(h,v,"mode:Domination")["errorCode"]=="SETUP_ACTION_ALREADY_SUBMITTED","same revision cannot submit different action");
        v=Status(h);Check((string)v["phase"]=="mode_menu" && Has(v,"mode:Tutorial",false),"unsupported visible mode retained without executable adapter");
        Check((string)Act(h,v,"mode:Tutorial")["errorCode"]=="SETUP_ACTION_UNAVAILABLE","unsupported mode cannot invoke callback");
        r=Act(h,v,"mode:Perfection");Check((string)r["status"]=="submitted" && SetupManager.PreliminaryGameSettings.BaseGameMode=="Perfection","mode callback uses normal native path");
        v=Status(h);var tribe=(TribePickerScreen_UI2)UIManager.Instance.Current;
        Check(Has(v,"tribe:FutureTribe"),"future tribe comes from visible native dictionary");
        Act(h,v,"tribe:FutureTribe");v=Status(h);Check((string)v["phase"]=="tribe_selected" && Has(v,"confirm_tribe"),"actual matching Select popup enables confirmation");
        Check((string)v["selectedTribe"]=="FutureTribe" && (string)v["selectedSkin"]=="Default","selection read exposes actual tribe and skin for confirmation");
        var originalSelection=v;tribe.selectedSkin="FutureSkin";
        Check((string)Act(h,originalSelection,"confirm_tribe")["errorCode"]=="STALE_SETUP_CONTEXT" && tribe.Confirms==0,"manual skin change invalidates stale confirmation");tribe.selectedSkin="Default";
        tribe.popup.Mode="Purchase";v=Status(h);Check((string)v["phase"]=="needs_resolution" && !Has(v,"confirm_tribe"),"purchase popup is never promoted to a selection callback");
        tribe.popup.Mode="Select";tribe.popup.SelectButton.ButtonEnabled=false;v=Status(h);Check(Has(v,"confirm_tribe",false),"disabled confirmation remains non-executable");
        var diagnostic=(Dictionary<string,object>)v["confirmationDiagnostics"];
        var matched=(Dictionary<string,object>)diagnostic["matchedButton"];
        Check((string)matched["activeInHierarchy"]=="true" && (bool)matched["buttonEnabled"]==false,
            "diagnostics distinguish visible disabled confirmation from hidden buttons");
        tribe.popup.SelectButton.ButtonEnabled=true;tribe.popup.SelectButton.gameObject.activeInHierarchy=false;v=Status(h);
        diagnostic=(Dictionary<string,object>)v["confirmationDiagnostics"];matched=(Dictionary<string,object>)diagnostic["matchedButton"];
        Check((string)matched["activeInHierarchy"]=="false" && (bool)matched["buttonEnabled"]==true && Has(v,"confirm_tribe",false),
            "hidden enabled button remains non-executable with distinct diagnostics");
        Check((string)Act(h,v,"confirm_tribe")["errorCode"]=="SETUP_ACTION_UNAVAILABLE" && tribe.Confirms==0,
            "read diagnostics do not grant authority to an inactive confirmation button");
        tribe.popup.SelectButton.gameObject.activeInHierarchy=true;
        tribe.popup.SelectButton.ButtonEnabled=true;tribe.popup.uiTextButton.gameObject.activeInHierarchy=false;v=Status(h);
        Check(Has(v,"confirm_tribe"),"inactive opinion control cannot hide actual tribe confirmation button");
        tribe.popup.ButtonId=-1;v=Status(h);Check(Has(v,"confirm_tribe",false),"missing matching native tribe button cannot acquire confirmation authority");
        tribe.popup.ButtonId=(int)TribeType.FutureTribe;v=Status(h);Act(h,v,"confirm_tribe");
        v=Status(h);var screen=(GameSetupScreen_UI2)UIManager.Instance.Current;
        Check((string)v["phase"]=="settings_menu" && tribe.Confirms==1,"confirm callback reaches actual settings screen");
        Check(Has(v,"select:opponents:5") && Has(v,"select:map_preset:77") && Has(v,"select:map_size:22"),"native candidates are not limited to old count/map/size constants");
        r=Act(h,v,"select:map_preset:77");Check((string)r["status"]=="configured" && (bool)r["settingVerified"] && SetupManager.PreliminaryGameSettings.mapPreset==SetupPreset.FutureMap,"native map callback updates enum field and verifies actual value");
        v=Status(h);r=Act(h,v,"select:difficulty:41");Check((bool)r["settingVerified"] && SetupManager.PreliminaryGameSettings.Difficulty==SetupDifficulty.FutureDifficulty,"native difficulty ID maps to index and enum readback");
        v=Status(h);var stale=v;Act(h,v,"select:opponents:5");v=Status(h);
        Check(((Dictionary<string,object>)v["settings"])["MapSize"].ToString()=="22","opponent callback side effect on map size appears in current settings");
        Check((string)Act(h,stale,"select:map_size:14")["errorCode"]=="SETUP_ACTION_ALREADY_SUBMITTED","old action context cannot be reused for another setting");
        screen.IgnorePreset=true;r=Act(h,v,"select:map_preset:3");Check((string)r["status"]=="not_confirmed" && !(bool)r["ok"],"successful invocation with failed setting readback does not report configured");
        screen.IgnorePreset=false;screen.mapTypeData.Ids.Add(77);screen.mapTypeData.Labels.Add("duplicate");v=Status(h);
        Check(!(bool)v["optionsComplete"] && Has(v,"select:map_preset:77",false) && Has(v,"continue",false),"duplicate native IDs keep list incomplete and creation disabled");
        screen.mapTypeData.Ids.RemoveAt(2);screen.mapTypeData.Labels.RemoveAt(2);screen.difficultyData.Fail=true;v=Status(h);
        Check(!(bool)v["optionsComplete"] && Has(v,"continue",false),"unreadable native IDs cannot imply empty complete choices");screen.difficultyData.Fail=false;
        screen.mapSizeData.Labels.Clear();v=Status(h);Check(!(bool)v["optionsComplete"],"unpopulated native layout data is not ready");screen.mapSizeData.Labels.AddRange(new[]{"14","22"});
        UIBlackFader.Showing=true;v=Status(h);Check((string)v["phase"]=="loading" && ((System.Collections.IList)v["actions"]).Count==0,"native fader blocks repeated navigation and creation");UIBlackFader.Showing=false;
        v=Status(h);Act(h,v,"continue");r=Act(h,v,"continue");Check(screen.Creates==1 && (string)r["status"]=="submitted","creation submitted once despite duplicate request");
        v=Status(h);Check((string)v["phase"]=="loading" && !(bool)v["ready"] && (bool)v["creationSubmitted"],"loading is distinct from actual new-game readiness");
        SetupManager.instance.isLoadingGame=false;v=Status(h);Check((string)Act(h,v,"continue")["errorCode"]=="ACTIVE_OR_PENDING_GAME_PRESERVED","pending creation never resubmits after a silent native return");
        var concealed=new ConcealedState();SetupManager.GameState=concealed;SetupManager.instance.client.GameState=concealed;v=Status(h);
        Check((string)v["phase"]=="active_game" && concealed.Reads==0,"unready active client is not sampled for private settings or map");
        SetupManager.GameState=null;SetupManager.instance.client.GameState=null;UIManager.Instance.Current=new StartScreen_UI2();
        string previousSession=(string)Status(h)["setupSessionId"];Set(h,"_setupCreatedEpisode","verified-old-episode");v=Status(h);
        Check((string)v["setupSessionId"]!=previousSession && !(bool)v["creationSubmitted"],"verified creation followed by normal no-game start menu opens a fresh session");
        start=new StartScreen_UI2 {Fail=true};h=Host(start);v=Status(h);r=Act(h,v,"start");
        Check((string)r["status"]=="unknown" && (bool)r["callbackSubmitted"],"attempted callback exception remains uncertain");Act(h,v,"start");Check(start.Calls==1,"uncertain callback is never replayed");
        start=new StartScreen_UI2();h=Host(start);v=Status(h);start.startButton.gameObject.activeInHierarchy=false;
        Check((string)Act(h,v,"start")["errorCode"]=="STALE_SETUP_CONTEXT" && start.Calls==0,"changed physical button invalidates observed menu");
        h=Host(new GameSetupScreen_UI2());SetupManager.PreliminaryGameSettings.GameType="PassAndPlay";v=Status(h);
        Check((string)v["errorCode"]=="SINGLEPLAYER_MENU_REQUIRED" && ((System.Collections.IList)v["actions"]).Count==0,"existing hotseat setup is not used for solo creation");
        h=Host(new StartScreen_UI2());concealed=new ConcealedState();SetupManager.GameState=concealed;SetupManager.instance.client.GameState=concealed;v=Status(h);
        Check((string)v["phase"]=="active_game" && concealed.Reads==0,"status preserves existing game without private observation");
        Check(!BridgeMod.IsDecisionCommand("setup_action") && BridgeMod.AdapterDecision("setup_action",null,null,null,null).StartsWith("deny:"),"menu administration stays outside planner command lists");
        h=Host(new GameSetupScreen_UI2());r=Parse((string)Call(h,"NativeSetupStep","configure","","1","Dryland","Normal"));
        Check((bool)r["ok"] && (bool)r["settingsVerified"] && SetupManager.PreliminaryGameSettings.mapPreset==SetupPreset.Dryland,"legacy configure writes actual enum field and verifies settings");
        SetupManager.PreliminaryGameSettings.MapSize=22;SetupManager.PreliminaryGameSettings.IgnoreSize=true;
        r=Parse((string)Call(h,"NativeSetupStep","configure","","1","Dryland","Normal"));
        Check(!(bool)r["ok"] && (string)r["errorCode"]=="SETUP_SETTINGS_NOT_APPLIED","legacy configure no longer reports success after a setter fails");
        string result=(string)Call(null,"SetEnumMember",SetupManager.PreliminaryGameSettings,"mapPreset","999");
        Check(result=="undefined enum value" && SetupManager.PreliminaryGameSettings.mapPreset==SetupPreset.Dryland,"invalid enum integers cannot be written to map field");
        var expected=(Dictionary<string,string>)Call(null,"SetupSettings",SetupManager.PreliminaryGameSettings);
        Check((bool)Call(null,"SetupSettingsEqual",expected,SetupManager.PreliminaryGameSettings),"actual six-field settings match validates creation metadata");
        SetupManager.PreliminaryGameSettings.GameType="PassAndPlay";
        Check(!(bool)Call(null,"SetupSettingsEqual",expected,SetupManager.PreliminaryGameSettings),"wrong actual game type cannot validate requested creation");
        expected["GameType"]=null;
        Check(!(bool)Call(null,"SetupSettingsEqual",expected,SetupManager.PreliminaryGameSettings),"unknown requested setting cannot be treated as matching a created game");
        var boxed=new BoxedTribeDictionary();tribe=new TribePickerScreen_UI2 {tribeButtons=boxed};h=Host(tribe);v=Status(h);
        Check(Has(v,"tribe:FutureTribe") && boxed.TypedReads==1 && boxed.BoxedReads==0,"IL2CPP-shaped dictionary uses typed Keys and enum-keyed getter without boxed pairs");
        Act(h,v,"tribe:FutureTribe");Check(tribe.Calls==1,"typed native tribe candidate reaches actual selection callback");
        h=Host(new TribePickerScreen_UI2 {tribeButtons=boxed});boxed.ActualCount=2;v=Status(h);
        Check(!(bool)v["optionsComplete"] && ((System.Collections.IList)v["actions"]).Count==0,"typed key Count mismatch stays incomplete instead of inventing missing tribes");
        screen=new GameSetupScreen_UI2();screen.view.whatToShow=SetupVisibility.OpponentsList|SetupVisibility.DifficultyList|SetupVisibility.AdvancedSettingsToggle|SetupVisibility.ContinueButton;h=Host(screen);v=Status(h);
        Check((bool)v["optionsComplete"] && Has(v,"continue") && !Has(v,"select:map_preset:77") && Has(v,"toggle_advanced"),"collapsed native advanced settings do not block normal continuation or expose hidden lists");
        Act(h,v,"toggle_advanced");v=Status(h);Check(Has(v,"select:map_preset:77") && Has(v,"select:map_size:22") && screen.Calls==1,"native advanced toggle reveals actual dynamic map candidates");
        screen.view.continueButton.ButtonEnabled=false;v=Status(h);Check(Has(v,"continue",false),"native disabled continue cannot submit creation even with complete options");
        screen=new GameSetupScreen_UI2();h=Host(screen);v=Status(h);r=Act(h,v,"select:map_preset:3");var refreshed=Status(h);
        Check((bool)r["settingVerified"] && (int)refreshed["menuRevision"]>(int)v["menuRevision"] && (string)Act(h,refreshed,"select:difficulty:41")["status"]=="configured","selecting an unchanged value advances action context once and permits next distinct setting");
        var creative=new GameModeScreen_UI2();h=Host(creative);v=Status(h);
        Check(Has(v,"mode:Creative"),"enabled Creative button exposes the verified native OnCustom adapter");
        creative.creativeButton.ButtonEnabled=false;
        Check((string)Act(h,v,"mode:Creative")["errorCode"]=="STALE_SETUP_CONTEXT"&&creative.Calls==0,"disabled Creative button cannot dispatch from stale observed menu");
        v=Status(h);Check(Has(v,"mode:Creative",false),"native disabled Creative button stays non-executable");
        creative.creativeButton.ButtonEnabled=true;v=Status(h);r=Act(h,v,"mode:Creative");
        Check((string)r["status"]=="submitted"&&creative.Calls==1&&SetupManager.PreliminaryGameSettings.BaseGameMode=="Custom","Creative invokes normal native callback and selects Custom mode");
        Act(h,v,"mode:Creative");Check(creative.Calls==1,"duplicate Creative request does not replay the native callback");
        Check((string)Status(h)["phase"]=="tribe_menu","Creative continues through normal tribe selection before creation");
        tribe=new TribePickerScreen_UI2();h=Host(tribe);v=Status(h);Act(h,v,"tribe:FutureTribe");
        tribe.popup.tribeEnabled=false;tribe.popup.SelectButton.ButtonEnabled=false;v=Status(h);
        Check(Has(v,"toggle_tribe_enabled") && Has(v,"confirm_tribe",false),"disabled tribe exposes only its real availability toggle while PICK stays disabled");
        tribe.popup.disableButton.gameObject.activeInHierarchy=false;v=Status(h);
        Check(!Has(v,"toggle_tribe_enabled") && (string)Act(h,v,"toggle_tribe_enabled")["errorCode"]=="SETUP_ACTION_UNAVAILABLE" && tribe.popup.Toggles==0,
            "hidden native toggle cannot be invoked through administration");
        tribe.popup.disableButton.gameObject.activeInHierarchy=true;tribe.popup.disableButton.ButtonEnabled=false;v=Status(h);
        Check(Has(v,"toggle_tribe_enabled",false) && (string)Act(h,v,"toggle_tribe_enabled")["errorCode"]=="SETUP_ACTION_UNAVAILABLE",
            "native disabled toggle never grants execution authority");
        tribe.popup.disableButton.ButtonEnabled=true;v=Status(h);var oldPopup=tribe.popup;
        tribe.popup=new SelectTribePopup {tribeEnabled=false};tribe.popup.SelectButton.ButtonEnabled=false;PopupManager.Current=tribe.popup;
        Check((string)Act(h,v,"toggle_tribe_enabled")["errorCode"]=="STALE_SETUP_CONTEXT" && tribe.popup.Toggles==0 && oldPopup.Toggles==0,
            "replacement selection popup invalidates a stale action even when its displayed values match");
        v=Status(h);r=Act(h,v,"toggle_tribe_enabled");
        Check((string)r["status"]=="submitted" && tribe.popup.Toggles==1 && tribe.popup.tribeEnabled && tribe.Confirms==0,
            "availability action invokes the actual popup callback once without confirming the tribe");
        Act(h,v,"toggle_tribe_enabled");Check(tribe.popup.Toggles==1,"duplicate toggle receipt does not toggle the tribe back off");
        Check(Has(Status(h),"confirm_tribe"),"PICK becomes executable only after native callback enables its physical button");
        tribe.popup.Mode="Purchase";v=Status(h);
        Check(!Has(v,"toggle_tribe_enabled") && !Has(v,"confirm_tribe"),"purchase mode cannot acquire tribe selection or availability callbacks");
        screen=new GameSetupScreen_UI2();h=Host(screen);v=Status(h);
        Check(!Has(v,"select:game_mode:6"),"hidden rules list does not expose Sandbox selection");
        screen.view.whatToShow|=SetupVisibility.GameModeList;v=Status(h);
        Check(Has(v,"select:game_mode:6") && Has(v,"select:game_mode:88"),"visible native rules list supplies Sandbox and future native IDs");
        r=Act(h,v,"select:game_mode:6");v=Status(h);
        Check((bool)r["settingVerified"] && SetupManager.PreliminaryGameSettings.RulesGameMode==SetupRules.Sandbox
            && SetupManager.PreliminaryGameSettings.OpponentCount==0 && Has(v,"select:opponents:0") && screen.Creates==0,
            "native Sandbox callback changes actual rules and supplies zero opponent option without creating a game");
        expected=(Dictionary<string,string>)Call(null,"SetupSettings",SetupManager.PreliminaryGameSettings);
        SetupManager.PreliminaryGameSettings.RulesGameMode=SetupRules.Might;
        Check(!(bool)Call(null,"SetupSettingsEqual",expected,SetupManager.PreliminaryGameSettings),"created rules mismatch cannot pass settings verification");
        screen.gameModeData.Fail=true;v=Status(h);
        Check(!(bool)v["optionsComplete"] && Has(v,"continue",false),"unreadable visible rules prevent creation rather than inventing a zero bot setting");
    }
}
