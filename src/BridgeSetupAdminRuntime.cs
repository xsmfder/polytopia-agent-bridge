// Normal single-player menu administration, separate from planner commands.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private string _setupSession = Guid.NewGuid().ToString("N");
        private long _setupRevision;
        private string _setupFingerprint, _setupLastReply, _setupLastAction;
        private long _setupLastRevision = -1;
        private bool _setupCreationSubmitted;
        private string _setupCreatedEpisode;
        private string _setupChosenTribe, _setupChosenSkin;
        private Dictionary<string,string> _setupExpectedSettings;
        private object _setupScreen;

        internal sealed class SetupOption
        {
            internal string Id, Label, Method, Field, Setting, Name;
            internal int Index, Value;
            internal bool? Enabled;
            internal object[] Args;
            internal string Json()
            {
                return new JsonObj().Str("actionId", Id).NullOrStr("label", Label).NullOrStr("field", Field)
                    .Raw("value", Field == null ? "null" : Value.ToString(CultureInfo.InvariantCulture))
                    .NullOrStr("nativeName", Name).NullableBool("enabled", Enabled)
                    .Bool("executable", Enabled == true && Method != null).Close();
            }
        }
        private sealed class SetupView
        {
            internal string Phase, Error, Episode, SettingsJson, SelectedTribe, SelectedSkin;
            internal object Screen, Settings;
            internal bool Ready, Complete = true;
            internal readonly List<SetupOption> Actions = new List<SetupOption>();
        }
        internal static bool IsSetupAdminCommand(string cmd)
        { return cmd == "setup_status" || cmd == "setup_action" || cmd == "game_lifecycle" || cmd == "game_lifecycle_status"; }

        private static string SetupError(string code)
        {
            return new JsonObj().Bool("ok", false).Str("schema", "singleplayer-setup-1")
                .Str("status", "rejected").Str("errorCode", code).Bool("callbackSubmitted", false)
                .Bool("retryAllowed", false).Close();
        }
        private static Dictionary<string,string> SetupSettings(object settings)
        {
            var result = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var name in new[] {"GameType", "BaseGameMode", "OpponentCount", "MapSize", "Difficulty", "mapPreset"})
                result[name] = SafeScalar(MemberWithAccessor(settings, name));
            return result;
        }
        private static string SetupSettingsJson(object settings)
        {
            var o = new JsonObj(); foreach (var row in SetupSettings(settings)) o.NullOrStr(row.Key,row.Value);
            return o.Close();
        }
        private static bool SetupSettingsEqual(Dictionary<string,string> expected, object actual)
        {
            if (expected == null || actual == null) return false;
            var current = SetupSettings(actual);
            foreach (var row in expected) if (row.Value == null || current[row.Key] != row.Value) return false;
            return true;
        }
        private static string SetupSelectionName(object settings, string member, int id)
        {
            var value = MemberWithAccessor(settings,member);
            return value != null && value.GetType().IsEnum ? Enum.GetName(value.GetType(),id) : null;
        }
        private static int? SetupNativeId(object value)
        {
            try { return value != null && value.GetType().IsEnum ? (int?)Convert.ToInt32(value,CultureInfo.InvariantCulture) : CRInt(value); }
            catch { return null; }
        }
        // These are adapter bindings to audited native callbacks, not a candidate table.
        // Actual values and labels always come from this screen's populated UI data.
        internal static List<SetupOption> ReadSetupOptions(object screen, object settings, string field, out bool complete)
        {
            complete = false; var result = new List<SetupOption>();
            string dataName, member, callback;
            switch(field)
            {
                case "opponents": dataName="opponentsData"; member="OpponentCount"; callback="OnNumberOfOpponentsChanged"; break;
                case "difficulty": dataName="difficultyData"; member="Difficulty"; callback="OnDifficultyChanged"; break;
                case "map_size": dataName="mapSizeData"; member="MapSize"; callback="OnMapSizeChanged"; break;
                case "map_preset": dataName="mapTypeData"; member="mapPreset"; callback="OnMapTypeChanged"; break;
                default: return result;
            }
            try
            {
                var data = MemberWithAccessor(screen,dataName); if (data == null) return result;
                var labelsMethod = data.GetType().GetMethod("GetLabels",Type.EmptyTypes);
                var idMethod = data.GetType().GetMethod("GetId",new[] {typeof(int)});
                var labels = labelsMethod == null ? null : AsEnumerable(labelsMethod.Invoke(data,null));
                if (labels == null || idMethod == null) return result;
                complete = true; int index = 0; var ids = new HashSet<int>();
                foreach (var raw in labels)
                {
                    if (index >= 128) { complete = false; break; }
                    var id = CRInt(idMethod.Invoke(data,new object[] {index}));
                    if (!id.HasValue || !ids.Add(id.Value)) { complete = false; index++; continue; }
                    result.Add(new SetupOption {Id="select:"+field+":"+id.Value.ToString(CultureInfo.InvariantCulture),
                        Label=SafeScalar(raw), Field=field, Setting=member, Method=callback, Index=index, Value=id.Value,
                        Name=SetupSelectionName(settings,member,id.Value), Enabled=true, Args=new object[] {index}});
                    index++;
                }
            }
            catch { complete = false; }
            if (result.Count==0) complete=false; // Layout data not populated yet is not a ready setup.
            if (!complete) foreach(var row in result) row.Enabled = null;
            return result;
        }
        private void SetupButton(SetupView v, string field, string id, string method)
        { SetupButtonFrom(v,v.Screen,field,id,method); }
        private void SetupButtonFrom(SetupView v, object owner, string field, string id, string method)
        {
            var button = MemberWithAccessor(owner,field);
            if (ReadActive(button) != "true") return;
            v.Actions.Add(new SetupOption {Id=id,Label=SafeScalar(MemberWithAccessor(button,"Text")),Method=method,
                Enabled=ReadBoolPropertyOrGetter(button,"ButtonEnabled")});
        }
        private SetupView ReadSetupView()
        {
            var v = new SetupView {Phase="unavailable"};
            // No world or player data is read unless this administration submitted creation
            // and a verified single-player seat actually owns the new displayed state.
            var gs = MainClientState();
            if (gs != null || !NoActiveDisplayedState())
            {
                v.Phase="active_game";
                if (!_setupCreationSubmitted || gs == null || _fixedControlledSeat != 1 || !PrivateSamplingAllowed(gs)) return v;
                var identity = ResolveControlledIdentity(gs);
                if (!identity.Verified || identity.Id != 1) return v;
                var settings = MemberWithAccessor(gs,"Settings");
                if (SafeScalar(MemberWithAccessor(settings,"GameType")) != "SinglePlayer") return v;
                var own=PlayerStateOf(gs,1);
                v.SelectedTribe=SafeScalar(MemberWithAccessor(own,"tribe"));
                v.SelectedSkin=SafeScalar(MemberWithAccessor(own,"skinType"));
                v.SettingsJson=SetupSettingsJson(settings); v.Episode=EpisodeId(gs);
                if (!SetupSettingsEqual(_setupExpectedSettings,settings)
                    || (_setupChosenTribe!=null && v.SelectedTribe!=_setupChosenTribe)
                    || (_setupChosenSkin!=null && v.SelectedSkin!=_setupChosenSkin))
                {v.Phase="created_settings_mismatch"; v.Error="CREATED_SETTINGS_MISMATCH"; return v;}
                _setupCreatedEpisode=v.Episode;
                bool? creating=ReadBoolMember(SafeInstance(),"isLoadingGame");
                object fading=InvokeStatic(FindGameType("UIBlackFader"),"IsShowing");
                if (creating==null || !(fading is bool)) {v.Phase="unavailable";v.Error="NATIVE_LOADING_STATE_UNKNOWN";return v;}
                if (creating==true || (bool)fading) {v.Phase="loading";return v;}
                var unknown = new JsonArr(); string reason;
                string state = CombatStateJson(gs);
                bool? busy = ActionPipelineBusy(unknown);
                if (Json.GetString(state,"settlementStatus") != "complete") busy=true;
                string canAct = CanActOf(CurrentPlayerId(gs)==1,SafeScalar(MemberWithAccessor(gs,"CurrentState")),
                    PendingTriggerCountFor(gs,1,unknown),PopupIsOpen(),PopupNeedsChoice(),busy,out reason);
                v.Ready=canAct=="true";
                v.Phase=v.Ready ? "game_ready" : PopupIsOpen()==true ? "needs_resolution" : "loading";
                return v;
            }
            var manager=SafeInstance(); bool? loading=ReadBoolMember(manager,"isLoadingGame");
            if (loading == null) {v.Error="NATIVE_LOADING_STATE_UNKNOWN"; return v;}
            object fader = InvokeStatic(FindGameType("UIBlackFader"),"IsShowing");
            if (!(fader is bool)) {v.Error="NATIVE_FADER_STATE_UNKNOWN"; return v;}
            if (loading==true || (bool)fader) {v.Phase="loading"; return v;}
            var uiType=FindGameType("UIManager"); var ui=uiType==null ? null : InstanceOf(uiType);
            var getter=uiType==null ? null : uiType.GetMethod("GetCurrentScreen",Type.EmptyTypes);
            object current=ui==null || getter==null ? null : getter.Invoke(ui,null);
            if (current==null) {v.Error="NATIVE_MENU_NOT_READY"; return v;}
            foreach (var name in new[] {"StartScreen_UI2","GameModeScreen_UI2","TribePickerScreen_UI2","GameSetupScreen_UI2"})
            {
                var cast=CRTryNativeCast(current,FindGameType(name));
                if (cast!=null) {v.Screen=cast; v.Phase=name=="StartScreen_UI2" ? "start_menu" : name=="GameModeScreen_UI2" ? "mode_menu" : name=="TribePickerScreen_UI2" ? "tribe_menu" : "settings_menu"; break;}
            }
            if (v.Screen==null) {v.Error="UNSUPPORTED_CURRENT_MENU"; return v;}
            var property=_gameManagerType==null ? null : _gameManagerType.GetProperty("PreliminaryGameSettings",BindingFlags.Static|BindingFlags.Public);
            v.Settings=property==null ? null : property.GetValue(null,null);
            v.SettingsJson=v.Settings==null ? null : SetupSettingsJson(v.Settings);
            if (v.Settings==null && (v.Phase=="tribe_menu" || v.Phase=="settings_menu"))
            {v.Error="SETUP_SETTINGS_UNAVAILABLE"; return v;}
            if (v.Phase=="tribe_menu")
            {
                v.SelectedTribe=SafeScalar(MemberWithAccessor(MemberWithAccessor(v.Screen,"selectedTribe"),"type"));
                v.SelectedSkin=SafeScalar(MemberWithAccessor(v.Screen,"selectedSkin"));
            }
            else if (v.Phase=="settings_menu") {v.SelectedTribe=_setupChosenTribe; v.SelectedSkin=_setupChosenSkin;}
            if ((v.Phase=="tribe_menu" || v.Phase=="settings_menu")
                && SafeScalar(MemberWithAccessor(v.Settings,"GameType"))!="SinglePlayer")
            {v.Error="SINGLEPLAYER_MENU_REQUIRED"; return v;}
            bool? popup=PopupIsOpen();
            if (popup!=false)
            {
                var currentPopup=InvokeStatic(FindGameType("PopupManager"),"GetCurrentPopup");
                var selection=CRTryNativeCast(currentPopup,FindGameType("SelectTribePopup"));
                if (popup==true && v.Phase=="tribe_menu" && selection!=null
                    && CRSameNative(selection,MemberWithAccessor(v.Screen,"popup")) && ReadActive(selection)=="true"
                    && SafeScalar(MemberWithAccessor(selection,"Mode"))=="Select")
                { v.Phase="tribe_selected"; SetupButtonForSelection(v,selection); }
                else {v.Phase="needs_resolution"; v.Error=popup==null ? "NATIVE_POPUP_STATE_UNKNOWN" : "NATIVE_POPUP_REQUIRES_ATTENTION";}
                return v;
            }
            if (v.Phase=="start_menu") SetupButton(v,"startButton","start","ClickNewGame");
            if (v.Phase=="mode_menu")
            {
                SetupButton(v,"dominationButton","mode:Domination","OnDomination");
                SetupButton(v,"perfectionButton","mode:Perfection","OnPerfection");
                SetupButton(v,"creativeButton","mode:Creative",null);
                SetupButton(v,"tutorialButton","mode:Tutorial",null);
            }
            if (v.Phase=="tribe_menu") ReadSetupTribes(v);
            if (v.Phase=="settings_menu")
            {
                var view=MemberWithAccessor(v.Screen,"view");
                var flags=MemberWithAccessor(view,"whatToShow");
                foreach (var field in new[] {"opponents","difficulty","map_preset","map_size"})
                {
                    string flag=field=="opponents" ? "OpponentsList" : field=="difficulty" ? "DifficultyList" : field=="map_preset" ? "MapTypeList" : "MapSizeList";
                    bool? visible=SetupFlag(flags,flag);
                    if(visible==false)continue; // Collapsed native advanced lists are not missing data.
                    if(visible==null){v.Complete=false;continue;}
                    bool complete; v.Actions.AddRange(ReadSetupOptions(v.Screen,v.Settings,field,out complete)); v.Complete &= complete;
                }
                if(SetupFlag(flags,"AdvancedSettingsToggle")==true)
                    SetupButtonFrom(v,view,"advancedSettingsToggle","toggle_advanced","OnAdvancedSettingsToggleClicked");
                if(SetupFlag(flags,"ContinueButton")==true)
                {
                    SetupButtonFrom(v,view,"continueButton","continue","OnContinueClicked_StartSingleplayerGame");
                    foreach(var row in v.Actions)if(row.Id=="continue" && !v.Complete)row.Enabled=false;
                }
            }
            return v;
        }
        private static bool? SetupFlag(object flags,string name)
        {
            try
            {
                if(flags==null || !flags.GetType().IsEnum)return null;
                int value=Convert.ToInt32(flags,CultureInfo.InvariantCulture);
                int bit=Convert.ToInt32(Enum.Parse(flags.GetType(),name,false),CultureInfo.InvariantCulture);
                return (value & bit)==bit;
            }
            catch{return null;}
        }
        private void SetupButtonForSelection(SetupView v, object selection)
        {
            // TribePicker creates its main PopupButtonData with id=selected tribe,
            // then BasicPopupLegacy materializes it in buttonContainer. The
            // SelectTribePopup.uiTextButton is a separate opinion control.
            var id=SetupNativeId(MemberWithAccessor(MemberWithAccessor(v.Screen,"selectedTribe"),"type"));
            var getter=selection.GetType().GetMethod("GetButtonById",new[] {typeof(int)});
            object button=null;
            try {if(id.HasValue && getter!=null) button=getter.Invoke(selection,new object[] {id.Value});}
            catch {v.Complete=false;}
            v.Actions.Add(new SetupOption {Id="confirm_tribe",Method="OnSelectTribeMainButtonPressed",
                Enabled=button==null ? (bool?)null : ReadActive(button)=="true" ? ReadBoolPropertyOrGetter(button,"ButtonEnabled") : (bool?)false,
                Label=SafeScalar(MemberWithAccessor(button,"Text"))});
        }
        private void ReadSetupTribes(SetupView v)
        {
            var dictionary=MemberWithAccessor(v.Screen,"tribeButtons");
            // IL2CPP nongeneric Dictionary enumeration boxes KeyValuePair/enum
            // into Object. Its Item is keyed by TribeType, never a list index.
            var keys=URTypedKeys(MemberWithAccessor(dictionary,"Keys"));
            var count=CRInt(MemberWithAccessor(dictionary,"Count"));
            if (keys==null || !count.HasValue || keys.Count!=count.Value) {v.Complete=false; return;}
            string callbackSeen;
            var callback=FindInstanceMethodUnique(v.Screen.GetType(),"OnTribeButtonClicked",
                new[] {"TribeType","TribeType"},"Void",out callbackSeen);
            var pars=callback==null ? null : callback.GetParameters();
            foreach (var key in keys)
            {
                if (key==null || !key.GetType().IsEnum) {v.Complete=false; continue;}
                object button=null;
                try
                {
                    var getter=dictionary.GetType().GetMethod("get_Item",new[] {key.GetType()});
                    if (getter!=null) button=getter.Invoke(dictionary,new[] {key});
                }
                catch {v.Complete=false;}
                if (button==null) {v.Complete=false; continue;}
                if (ReadActive(button)!="true") continue;
                bool valid=callback!=null && !callback.ContainsGenericParameters && callback.ReturnType==typeof(void)
                    && pars!=null && pars.Length==2 && pars[0].ParameterType==key.GetType() && pars[1].ParameterType==key.GetType()
                    && Enum.IsDefined(key.GetType(),0);
                v.Actions.Add(new SetupOption {Id="tribe:"+SafeScalar(key),Name=SafeScalar(key),
                    Label=SafeScalar(MemberWithAccessor(button,"Text")),Method=valid ? "OnTribeButtonClicked" : null,
                    Enabled=ReadBoolPropertyOrGetter(button,"ButtonEnabled"),Args=valid ? new[] {key,Enum.ToObject(key.GetType(),0)} : null});
            }
        }
        private SetupView SetupRefresh()
        {
            var v=ReadSetupView();
            // A completed creation can be followed by another game only after the
            // previous displayed/main state is gone and the normal start menu returned.
            if (v.Phase=="start_menu" && _setupCreatedEpisode!=null)
            {
                _setupSession=Guid.NewGuid().ToString("N"); _setupCreatedEpisode=null;
                _setupCreationSubmitted=false; _setupExpectedSettings=null;
                _setupChosenTribe=null; _setupChosenSkin=null;
                _setupLastReply=null; _setupLastAction=null; _setupLastRevision=-1;
            }
            var a=new List<string>(); foreach(var row in v.Actions) a.Add(row.Json()); a.Sort(StringComparer.Ordinal);
            string fp=v.Phase+"|"+v.Error+"|"+v.SettingsJson+"|"+v.SelectedTribe+"|"+v.SelectedSkin+"|"+v.Episode+"|"+v.Complete+"|"+_setupLastRevision+"|"+_setupLastAction+"|"+string.Join(";",a.ToArray());
            if (fp!=_setupFingerprint || !CRSameNative(_setupScreen,v.Screen))
            {_setupRevision++; _setupFingerprint=fp; _setupScreen=v.Screen;}
            return v;
        }
        private string SetupStatus()
        {
            var v=SetupRefresh(); var actions=new JsonArr(); foreach(var row in v.Actions) actions.Raw(row.Json());
            return new JsonObj().Bool("ok",v.Error==null).Str("schema","singleplayer-setup-1")
                .Str("setupSessionId",_setupSession).Raw("menuRevision",_setupRevision.ToString(CultureInfo.InvariantCulture))
                .Raw("configuredPlayerId",_fixedControlledSeat.HasValue ? _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .Str("phase",v.Phase).Bool("ready",v.Ready).Bool("optionsComplete",v.Complete).NullOrStr("errorCode",v.Error)
                .NullOrStr("episodeId",v.Episode).Raw("settings",v.SettingsJson??"null").Raw("actions",actions.Close())
                .NullOrStr("selectedTribe",v.SelectedTribe).NullOrStr("selectedSkin",v.SelectedSkin)
                .Bool("creationSubmitted",_setupCreationSubmitted).Raw("lastAction",_setupLastReply??"null").Close();
        }
        private string SetupAction(string actionId,string session,string revision)
        {
            long expected;
            if (session!=_setupSession || !long.TryParse(revision,NumberStyles.None,CultureInfo.InvariantCulture,out expected) || expected<=0)
                return SetupError("INVALID_SETUP_CONTEXT");
            if (_fixedControlledSeat!=1) return SetupError("SINGLEPLAYER_SEAT_ONE_REQUIRED");
            if (_setupLastRevision==expected)
                return _setupLastAction==actionId ? _setupLastReply : SetupError("SETUP_ACTION_ALREADY_SUBMITTED");
            var v=SetupRefresh();
            if (expected!=_setupRevision) return SetupError("STALE_SETUP_CONTEXT");
            if (v.Error!=null) return SetupError(v.Error);
            if (_setupCreationSubmitted || v.Screen==null || MainClientState()!=null || !NoActiveDisplayedState())
                return SetupError("ACTIVE_OR_PENDING_GAME_PRESERVED");
            SetupOption selected=null;
            foreach (var row in v.Actions) if (row.Id==actionId) {if(selected!=null)return SetupError("AMBIGUOUS_SETUP_ACTION"); selected=row;}
            if (selected==null || selected.Enabled!=true || selected.Method==null) return SetupError("SETUP_ACTION_UNAVAILABLE");
            var method=v.Screen.GetType().GetMethod(selected.Method,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
                null, selected.Args==null ? Type.EmptyTypes : Array.ConvertAll(selected.Args,x=>x.GetType()), null);
            if (method==null || method.ReturnType!=typeof(void)) return SetupError("NATIVE_CALLBACK_UNAVAILABLE");
            _setupLastRevision=expected; _setupLastAction=actionId;
            if (actionId=="confirm_tribe") {_setupChosenTribe=v.SelectedTribe; _setupChosenSkin=v.SelectedSkin;}
            if (actionId=="continue") {_setupCreationSubmitted=true; _setupExpectedSettings=SetupSettings(v.Settings);}
            try
            {
                method.Invoke(v.Screen,selected.Args);
                bool verified=selected.Setting!=null && SetupNativeId(MemberWithAccessor(v.Settings,selected.Setting))==selected.Value;
                _setupLastReply=new JsonObj().Bool("ok",selected.Setting==null || verified).Str("status",selected.Setting==null ? "submitted" : verified ? "configured" : "not_confirmed")
                    .Str("setupSessionId",_setupSession).Raw("menuRevision",expected.ToString(CultureInfo.InvariantCulture)).Str("actionId",actionId)
                    .Bool("callbackSubmitted",true).Bool("ready",false).Bool("retryAllowed",false).Bool("settingVerified",verified).Close();
            }
            catch(Exception failure)
            {
                _setupLastReply=new JsonObj().Bool("ok",false).Str("status","unknown").Str("errorCode","SETUP_CALLBACK_UNCERTAIN")
                    .Str("error",(failure.InnerException??failure).GetType().Name).Str("setupSessionId",_setupSession)
                    .Raw("menuRevision",expected.ToString(CultureInfo.InvariantCulture)).Str("actionId",actionId)
                    .Bool("callbackSubmitted",true).Bool("retryAllowed",false).Close();
            }
            return _setupLastReply;
        }
    }
}
