using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;
using PolytopiaPublicMarkers;
using Reader = PolytopiaPublicMarkers.PublicMarkerReader;

namespace MarkerIntegration041
{
    internal class MapData { public object[] Tiles { get; set; } public int Width { get { return 16; } } }
    internal class State : GameState
    {
        public uint CurrentTurn { get; set; }
        public MapData Map { get; set; }
        public int CurrentPlayerIndex { get { return 0; } }
        public PlayerState[] PlayerStates { get; set; }
        public string CurrentState { get { return "Started"; } }
        public uint CurrentUnitId { get { return 0; } }
    }
    internal class PlayerState
    {
        public byte Id { get; set; }
        public TribeType tribe { get; set; }
        public object[] availableTech { get { return new object[0]; } }
        public int Currency { get { return 3; } }
    }
    internal class Client
    {
        public State GameState { get; set; }
        internal bool replay, spectating, failReplay;
        public bool IsReplay { get { if (failReplay) throw new Exception("unknown replay"); return replay; } }
        public bool IsSpectating { get { return spectating; } }
    }
    internal static class GameManager
    {
        public static State GameState { get; set; }
        public static PlayerState LocalPlayer { get; set; }
        public static Client Client { get; set; }
    }
    internal enum Fog { Default = 0, NoFog = 1, SharedFog = 2 }
    internal enum Ability { RainbowVision = 1 }
    internal class TribeData { internal bool has = true, unknown; internal int reads; internal Action duringRead; }
    internal class GameLogicData
    {
        internal readonly TribeData data = new TribeData();
        internal int lookups;
        public TribeData GetTribeData(TribeType ownTribe) { lookups++; return data; }
    }
    internal static class TribeDataExtensions
    {
        public static bool HasAbility(TribeData data, Ability ability)
        { if (ability != Ability.RainbowVision) throw new Exception("wrong ability"); data.reads++; if(data.duringRead != null) data.duringRead(); if (data.unknown) throw new Exception("unknown ability"); return data.has; }
    }
    internal class Context
    {
        public byte localPlayerId;
        public Fog fogVisibilitySettings;
        public GameLogicData gld;
        public MapData mapData;
    }
    internal class Skin { }
    internal class SceneTile : Tile
    {
        public void Render(Context context) { }
        public void Render(Context context, Skin skin) { }
        public void RenderImprovement(Context context, Skin skin) { }
    }
    internal class Inner
    {
        internal string stableId = "real-scene-inner";
        public SceneTile[] renderedTiles;
        public bool RenderMap(Context context, MapData map) { return true; }
        public void ReRenderAllTiles(Context context) { }
    }
    internal class MapRenderer : Component
    {
        public static MapRenderer Current { get; set; }
        public Inner mapRenderer;
    }

    internal static class PublicMarkerIntegrationTests
    {
        private static int passed;
        private static void Check(bool condition, string label)
        { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
        private static object Identity(object value) { Inner inner = value as Inner; return inner == null ? value : (object)inner.stableId; }
        private static Dictionary<string, Type> Types()
        {
            return new Dictionary<string, Type> {
                {"GameManager",typeof(GameManager)}, {"GameState",typeof(State)}, {"PlayerState",typeof(PlayerState)},
                {"MapRenderer",typeof(MapRenderer)}, {"MapRendererInner",typeof(Inner)}, {"MapRenderContext",typeof(Context)},
                {"MapRenderContext+FogVisibilitySettings",typeof(Fog)}, {"MapData",typeof(MapData)}, {"ClientBase",typeof(Client)},
                {"Polytopia.Data.GameLogicData",typeof(GameLogicData)}, {"Polytopia.Data.TribeData",typeof(TribeData)},
                {"Polytopia.Data.TribeAbility+Type",typeof(Ability)}, {"TribeDataExtensions",typeof(TribeDataExtensions)},
                {"PolytopiaBackendBase.Common.TribeType",typeof(TribeType)}, {"Tile",typeof(SceneTile)},
                {"SkinVisualsTransientData",typeof(Skin)}, {"UnityEngine.Object",typeof(UnityObject)} };
        }
        private static Reader.Bindings Bindings()
        {
            return Reader.Bindings.CreateForTests(new Type[] {typeof(Tile),typeof(ParticleController.ObjectPoolWrapper),typeof(ParticleController),
                typeof(TileData),typeof(GameState),typeof(WorldCoordinates),typeof(ImprovementState),typeof(ImprovementType),typeof(TribeType),
                typeof(UnityObject),typeof(Component),typeof(GameObject),typeof(ParticleSystem)}, Identity);
        }
        private static void Field(BridgeMod bridge, string name, object value)
        { typeof(BridgeMod).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(bridge,value); }
        private static object Invoke(BridgeMod bridge, string name, params object[] args)
        { return typeof(BridgeMod).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(bridge,args); }
        private static long Revision(BridgeMod bridge)
        { return (long)typeof(BridgeMod).GetField("_observationRevision",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(bridge); }
        private sealed class Fixture
        {
            internal readonly BridgeMod bridge = new BridgeMod();
            internal readonly State gs = new State();
            internal readonly Context ctx = new Context();
            internal readonly Inner inner = new Inner();
            internal readonly SceneTile tile = new SceneTile();
            internal readonly ParticleController fx = new ParticleController();
            internal readonly PublicMarkerHooks hooks;
            internal int? controlled = 2;
            internal Fixture()
            {
                gs.CurrentTurn = 9; gs.Map = new MapData(); gs.Map.Tiles = new object[0];
                GameManager.GameState = gs; GameManager.LocalPlayer = new PlayerState(); GameManager.LocalPlayer.Id = 2; GameManager.LocalPlayer.tribe = TribeType.Elyrion;
                gs.PlayerStates = new PlayerState[] {GameManager.LocalPlayer};
                GameManager.Client = new Client(); GameManager.Client.GameState = gs;
                ctx.localPlayerId = 2; ctx.fogVisibilitySettings = Fog.Default; ctx.gld = new GameLogicData(); ctx.mapData = gs.Map;
                MapRenderer.Current = new MapRenderer(); MapRenderer.Current.mapRenderer = inner;
                tile.Coordinates = new WorldCoordinates(8,13); tile.Data = new TileData(); tile.Data.expected = gs; tile.Data.captured = true;
                tile.Data.improvement = new ImprovementState(); tile.Data.improvement.type = ImprovementType.Ruin;
                tile.Fire(fx); tile.Rainbow(fx);
                Field(bridge,"_gameManagerType",typeof(GameManager)); Field(bridge,"_gameStateType",typeof(State));
                Field(bridge,"_gameStateProp",typeof(GameManager).GetProperty("GameState"));
                Field(bridge,"_currentTurnProp",typeof(State).GetProperty("CurrentTurn")); Field(bridge,"_mapProp",typeof(State).GetProperty("Map"));
                Field(bridge,"_playersProp",typeof(State).GetProperty("PlayerStates")); Field(bridge,"_currentPlayerIndexProp",typeof(State).GetProperty("CurrentPlayerIndex"));
                Field(bridge,"_currentStateProp",typeof(State).GetProperty("CurrentState"));
                hooks = PublicMarkerHooks.CreateForTests(PublicMarkerHooks.NativeView.CreateForTests(Types(),Identity),Bindings(),
                    delegate(object state) { return (string)Invoke(bridge,"EpisodeId",state); }, delegate(object state) { return controlled; }, delegate { return Revision(bridge); });
                Field(bridge,"_publicMarkerHooks",hooks);
            }
            internal PublicMarkerHooks.Pass Start()
            { PublicMarkerHooks.Pass pass; PublicMarkerHooks.MapPrefix(inner,ctx,gs.Map,out pass); return pass; }
            internal void Complete()
            {
                PublicMarkerHooks.Pass pass = Start(); inner.renderedTiles = new SceneTile[] {tile};
                PublicMarkerHooks.MapPostfix(inner,ctx,gs.Map,true,pass); PublicMarkerHooks.FullFinalizer(null,pass);
            }
            internal string World() { return (string)Invoke(bridge,"WorldDump"); }
        }
        private static Dictionary<string,object> Parse(string json)
        { return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json); }
        private static Dictionary<string,object> Domain(string json, string key) { return (Dictionary<string,object>)Parse(json)[key]; }
        private static bool Status(string json,string key,string expected) { return (string)Domain(json,key)["status"] == expected; }
        private static bool Withheld(string json,string key) { return ((System.Collections.IList)Domain(json,key)["markers"]).Count == 0; }
        private const string Siege = "publicSiegeMarkers", Rainbow = "publicRuinRainbowMarkers";
        private static Context Fresh(Fixture f)
        { return new Context { localPlayerId = 2, fogVisibilitySettings = Fog.Default, gld = f.ctx.gld, mapData = f.ctx.mapData }; }
        public static int Main()
        {
            try { return Run(); }
            catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(ex.StackTrace); return 1; }
        }
        private static int Run()
        {
            Fixture f = new Fixture(); string world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable") && Parse(world).ContainsKey("tiles"), "initial no completed epoch leaves board and unavailable domains");
            f.Complete(); world = f.World(); Dictionary<string,object> parsed = Parse(world);
            Dictionary<string,object> siege = Domain(world,Siege), rainbow = Domain(world,Rainbow);
            Dictionary<string,object> marker = (Dictionary<string,object>)((System.Collections.IList)siege["markers"])[0];
            Check((string)siege["status"] == "ok" && (string)rainbow["status"] == "ok" && (string)marker["coords"] == "(8,13)"
                && Convert.ToInt64(marker["observedTurn"]) == Convert.ToInt64(parsed["turn"])
                && Convert.ToInt64(marker["observationRevision"]) == Convert.ToInt64(parsed["observationRevision"])
                && Convert.ToInt64(siege["observationRevision"]) == Convert.ToInt64(parsed["observationRevision"]), "actual prefix new roster postfix to WorldDump uses same reliable turn and revision");
            Check(((System.Collections.IList)parsed["tiles"]).Count == 0 && ((System.Collections.IList)parsed["resources"]).Count == 0 && ((System.Collections.IList)parsed["units"]).Count == 0
                && !world.Contains("PRIVATE_") && marker.Count == 4 && siege.Count == 5, "fog marker outside board emits only domain whitelist and never discloses tile owner or reward");
            Check(BridgeMod.IsDecisionWorldKey(Siege) && BridgeMod.IsDecisionWorldKey(Rainbow), "both real WorldDump keys registered in DecisionWorldKeys");

            f=new Fixture(); PublicMarkerHooks.Pass fullBatch=f.Start();
            f.inner.renderedTiles=new SceneTile[]{f.tile}; int fullBatchReads=f.ctx.gld.lookups;
            for(int i=0;i<324;i++) PublicMarkerHooks.TilePrefix(f.ctx);
            Check(f.ctx.gld.lookups==fullBatchReads,"324 same-context full-render tile callbacks do not repeat scene and tribe reads");
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,fullBatch); PublicMarkerHooks.FullFinalizer(null,fullBatch);
            world=f.World();
            Check(Status(world,Siege,"ok") && Status(world,Rainbow,"ok")
                && Convert.ToInt64(Parse(f.hooks.DiagnosticsJson())["fullPassTileChecksReused"])==324,
                "full-render reuse publishes only after the genuine completed pass");
            bool partialFresh=true;
            for(int i=0;i<16;i++)
            { int partialReads=f.ctx.gld.lookups; PublicMarkerHooks.TilePrefix(f.ctx); partialFresh &= f.ctx.gld.lookups>partialReads; }
            Check(partialFresh,"every same-context partial tile render still checks fresh scene state");

            f=new Fixture(); fullBatch=f.Start(); PublicMarkerHooks.TilePrefix(f.ctx); world=f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"),
                "in-progress full-render reuse cannot expose markers before completion");
            PublicMarkerHooks.FullFinalizer(new Exception("aborted render"),fullBatch);
            f=new Fixture(); fullBatch=f.Start(); f.inner.renderedTiles=new SceneTile[]{f.tile};
            PublicMarkerHooks.TilePrefix(f.ctx); f.ctx.fogVisibilitySettings=Fog.SharedFog;
            PublicMarkerHooks.TilePrefix(f.ctx); PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,fullBatch);
            f.ctx.fogVisibilitySettings=Fog.Default; world=f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"),
                "postfix rejects mutated fog on the same context before any publication");
            f=new Fixture(); fullBatch=f.Start(); f.inner.renderedTiles=new SceneTile[]{f.tile};
            PublicMarkerHooks.TilePrefix(Fresh(f)); PublicMarkerHooks.TilePrefix(f.ctx);
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,fullBatch); world=f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"),
                "different equivalent context inside full render invalidates the pass and cannot be revived by reuse");
            f=new Fixture(); fullBatch=f.Start(); f.inner.renderedTiles=new SceneTile[]{f.tile};
            PublicMarkerHooks.TilePrefix(f.ctx); GameManager.LocalPlayer.Id=3;
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,fullBatch); GameManager.LocalPlayer.Id=2; world=f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"),
                "same-context reuse retains full-postfix player isolation");
            PublicMarkerHooks.Pass inProgress = f.Start(); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege) && Status(world,Rainbow,"unavailable"), "actual prefix in-progress withholds both domains");
            Exception original = new Exception("native render failed"); Exception returned = PublicMarkerHooks.FullFinalizer(original,inProgress); world = f.World();
            Check(object.ReferenceEquals(original,returned) && Status(world,Siege,"unavailable") && Withheld(world,Siege), "actual finalizer preserves exception and invalidates incomplete render");
            // Native RenderMap's bool is set only on array allocation or Tile Instantiate;
            // its second loop still renders EVERY tile before a normal false return.
            f = new Fixture(); f.inner.renderedTiles = new SceneTile[]{f.tile};
            PublicMarkerHooks.Pass reused = f.Start(); PublicMarkerHooks.TilePrefix(f.ctx);
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,reused); PublicMarkerHooks.FullFinalizer(null,reused); world = f.World();
            Check(Status(world,Siege,"ok") && !Withheld(world,Siege) && Status(world,Rainbow,"ok"),
                "completed native RenderMap with reused roster certifies both domains despite false allocation flag");
            f.tile.Fire(null); reused=f.Start(); PublicMarkerHooks.TilePrefix(f.ctx);
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,reused); world=f.World();
            Check(Status(world,Siege,"ok") && Withheld(world,Siege),
                "another false allocation return observes removed visible fire rather than preserving stale markers");
            f.tile.Fire(f.fx); reused=f.Start(); PublicMarkerHooks.TilePrefix(f.ctx);
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,false,reused); world=f.World();
            Check(Status(world,Siege,"ok") && !Withheld(world,Siege)
                && ((System.Collections.IList)Parse(world)["tiles"]).Count==0 && !world.Contains("PRIVATE_"),
                "reused native render observes new fog fire without adding hidden tile information");
            f=new Fixture(); f.inner.renderedTiles=new SceneTile[]{f.tile};
            PublicMarkerHooks.Pass reusedFailed=f.Start();
            Exception renderFailure=new Exception("native render failed while reusing tiles");
            Exception sameFailure=PublicMarkerHooks.FullFinalizer(renderFailure,reusedFailed); world=f.World();
            Check(object.ReferenceEquals(renderFailure,sameFailure) && Status(world,Siege,"unavailable") && Withheld(world,Siege),
                "real native exception with reused roster still invalidates without treating false as an error code");
            f=new Fixture(); f.inner.renderedTiles=new SceneTile[]{f.tile};
            PublicMarkerHooks.Pass reusedWrongMap;
            PublicMarkerHooks.MapPrefix(f.inner,f.ctx,new MapData(),out reusedWrongMap);
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,new MapData(),false,reusedWrongMap);world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege),
                "false allocation flag never excuses incoming map identity mismatch");
            f = new Fixture(); PublicMarkerHooks.Pass missingRoster = f.Start(); PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,true,missingRoster); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "genuine postfix without actual rendered roster cannot report successful empty");
            f = new Fixture(); PublicMarkerHooks.Pass wrongMap; MapData unrelatedMap = new MapData();
            PublicMarkerHooks.MapPrefix(f.inner,f.ctx,unrelatedMap,out wrongMap); f.inner.renderedTiles = new SceneTile[]{f.tile};
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,unrelatedMap,true,wrongMap); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "actual incoming RenderMap MapData must equal real context and GameState map");
            f = new Fixture(); PublicMarkerHooks.Pass rerender; PublicMarkerHooks.FullPrefix(f.inner,f.ctx,out rerender); f.inner.renderedTiles = new SceneTile[]{f.tile};
            PublicMarkerHooks.AllPostfix(f.inner,f.ctx,rerender); PublicMarkerHooks.FullFinalizer(null,rerender); world = f.World();
            Check(Status(world,Siege,"ok") && Status(world,Rainbow,"ok"), "actual ReRenderAllTiles void postfix completes the same production epoch path");
            f = new Fixture(); f.Complete(); f.ctx.localPlayerId = 3; world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable") && Withheld(world,Siege), "actual context player differs from controlled local player");
            f = new Fixture(); f.Complete(); GameManager.LocalPlayer.Id = 3; world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"), "actual GameManager local-player change invalidates completed attribution");
            f = new Fixture(); GameManager.LocalPlayer.tribe = TribeType.Bardur; f.ctx.gld.data.has = true; f.Complete(); world = f.World();
            Check(Status(world,Rainbow,"unsupported") && Withheld(world,Rainbow) && Status(world,Siege,"ok"), "nonElyrion with rainbow-capable data still unsupported");
            f = new Fixture(); f.ctx.gld.data.unknown = true; f.Complete(); world = f.World();
            Check(Status(world,Rainbow,"unavailable") && Withheld(world,Rainbow) && Status(world,Siege,"ok"), "unknown own native ability withholds rainbow only");
            f = new Fixture(); f.ctx.fogVisibilitySettings = Fog.SharedFog; f.Complete(); world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable"), "SharedFog cannot certify normal own-player view");
            f = new Fixture(); GameManager.Client.replay = true; f.Complete(); world = f.World();
            f = new Fixture(); GameManager.Client.spectating = true; f.Complete(); string spectator = f.World();
            f = new Fixture(); GameManager.Client.failReplay = true; f.Complete(); string replayUnknown = f.World();
            Check(Status(world,Siege,"unavailable") && Status(spectator,Siege,"unavailable") && Status(replayUnknown,Siege,"unavailable"), "replay spectator and unreadable replay getter remain unavailable");
            f = new Fixture(); f.Complete(); f.tile.Data.duringCapture = delegate { MapRenderer.Current.mapRenderer = new Inner(); MapRenderer.Current.mapRenderer.stableId = "replacement-native-inner"; }; world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable") && Withheld(world,Siege), "native renderer identity changes during production collector sampling withhold both domains");
            Check((string)Domain(world,Siege)["reasonCode"] == "observation-context-unavailable",
                "failed current renderer context is not mislabeled as missing native bindings");
            f = new Fixture(); f.Complete(); f.ctx.gld.data.reads = 0; f.ctx.gld.data.duringRead = delegate
            { if (f.ctx.gld.data.reads == 4) { MapRenderer.Current.mapRenderer = new Inner(); MapRenderer.Current.mapRenderer.stableId = "changed-during-second-domain"; } };
            world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable") && Withheld(world,Siege), "drift during second domain also withholds earlier successful siege domain");
            f = new Fixture(); f.Complete(); f.tile.Data.duringCapture = delegate { Field(f.bridge,"_observationRevision",Revision(f.bridge)+1); }; world = f.World();
            parsed = Parse(world);
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable")
                && Convert.ToInt64(Domain(world,Siege)["observationRevision"]) == Convert.ToInt64(parsed["observationRevision"]), "revision drift withholds domains and preserves exact top-level frozen revision");
            f = new Fixture(); f.Complete(); f.inner.renderedTiles = new SceneTile[]{f.tile}; world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "actual rendered array replacement outside completed render invalidates epoch");
            f = new Fixture(); f.Complete(); Context different = new Context(); different.localPlayerId = 2; different.fogVisibilitySettings = Fog.Default; different.gld = f.ctx.gld; different.mapData = f.ctx.mapData;
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"ok") && Status(world,Rainbow,"ok"), "fresh equivalent native partial context retains genuine completed epoch");
            Dictionary<string,object> counts = Parse(f.hooks.DiagnosticsJson());
            Check(Convert.ToInt64(counts["fullCompleted"]) == 1 && Convert.ToInt64(counts["tileContextAdopted"]) == 1
                && Convert.ToInt64(counts["fullPassTileChecksReused"]) == 0 && counts.Count == 8,
                "bounded counters distinguish full completion, full-pass reuse and partial context adoption");
            long snapshots = Convert.ToInt64(counts["rosterSnapshots"]);
            for (int i=0; i<16; i++) PublicMarkerHooks.TilePrefix(Fresh(f));
            Check(Convert.ToInt64(Parse(f.hooks.DiagnosticsJson())["rosterSnapshots"]) == snapshots,
                "sixteen local tile checks never copy the full rendered roster");
            f = new Fixture(); f.Complete(); different = Fresh(f);
            GameManager.LocalPlayer = new PlayerState { Id=2, tribe=TribeType.Elyrion };
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "changed player object still requires an actual full render");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TileDefaultPrefix(null);
            PublicMarkerHooks.TilePrefix(Fresh(f)); world=f.World();
            Check(Status(world,Siege,"ok") && Convert.ToInt64(Parse(f.hooks.DiagnosticsJson())["defaultContextDeferred"]) == 1,
                "native optional null wrapper defers to real resolved context without destroying completed epoch");
            f = new Fixture(); f.inner.renderedTiles=new SceneTile[]{f.tile}; PublicMarkerHooks.TileDefaultPrefix(null);
            PublicMarkerHooks.TilePrefix(Fresh(f)); world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "optional null wrapper and partial render never establish a first epoch");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TileDefaultPrefix(null);
            different=Fresh(f); different.localPlayerId=3; PublicMarkerHooks.TilePrefix(different); world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "resolved other-seat context is rejected after optional null wrapper");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TileDefaultPrefix(null);
            Exception wrapperError=new Exception("native default render failed");
            Check(object.ReferenceEquals(PublicMarkerHooks.TileFinalizer(wrapperError),wrapperError), "native optional wrapper exception remains unchanged");
            PublicMarkerHooks.TilePrefix(Fresh(f)); world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "exception before resolved context cannot restore completed epoch");
            f = new Fixture(); f.Complete(); different = Fresh(f); GameManager.Client = new Client { GameState=f.gs };
            PublicMarkerHooks.TilePrefix(different); world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "changing client never transfers a full epoch even with identical visible state");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.mapData=new MapData();
            GameManager.Client.GameState=new State { Map=different.mapData, CurrentTurn=f.gs.CurrentTurn };
            PublicMarkerHooks.TilePrefix(different); world=f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "unrelated client state and context map cannot replace displayed state");
            f = new Fixture(); f.Complete(); different=Fresh(f); PublicMarkerHooks.TilePrefix(different); f.World();
            f.tile.Fire(null); PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"ok") && Withheld(world,Siege), "equivalent partial context reads current removed marker instead of retaining stale output");
            f = new Fixture(); f.inner.renderedTiles = new SceneTile[]{f.tile}; PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "fresh partial context never establishes first full epoch");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.fogVisibilitySettings = Fog.SharedFog;
            PublicMarkerHooks.TilePrefix(different); PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "shared fog followed by normal partial context cannot revive an invalidated epoch");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.fogVisibilitySettings = Fog.NoFog;
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new no-fog partial context cannot reuse player epoch");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.localPlayerId = 3;
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new other-seat partial context cannot reuse epoch");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.mapData = new MapData();
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new partial context must reference current map");
            f = new Fixture(); f.Complete(); different = Fresh(f); different.gld = new GameLogicData();
            PublicMarkerHooks.TilePrefix(different); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new partial context must retain full pass game-logic identity");
            f = new Fixture(); f.Complete(); f.gs.CurrentTurn++; PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "fresh partial context cannot transfer a completed view across turns");
            f = new Fixture(); f.Complete(); GameManager.Client.replay = true; PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new replay partial context cannot reuse epoch");
            f = new Fixture(); f.Complete(); GameManager.Client.spectating = true; PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new spectator partial context cannot reuse epoch");
            f = new Fixture(); f.Complete(); f.inner.renderedTiles = new SceneTile[]{f.tile}; PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "new partial context cannot certify replacement roster");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TilePrefix(Fresh(f));
            Exception partialError = new Exception("render fault");
            Check(object.ReferenceEquals(PublicMarkerHooks.TileFinalizer(partialError), partialError), "partial render exception is preserved unchanged");
            PublicMarkerHooks.TilePrefix(Fresh(f)); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "partial exception cannot be repaired by a later partial context");
            f.Complete(); world = f.World();
            Check(Status(world,Siege,"ok"), "actual full completion recovers after partial failure");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TilePrefix(null); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "unknown implicit single-tile context is unavailable without constructing one");
            f = new Fixture(); f.Complete(); PublicMarkerHooks.TilePrefix(f.ctx); world = f.World();
            Check(Status(world,Siege,"ok"), "same verified single-tile context retains completed renderer epoch");
            f = new Fixture(); PublicMarkerHooks.Pass changedTurn = f.Start(); f.inner.renderedTiles = new SceneTile[]{f.tile}; f.gs.CurrentTurn++;
            PublicMarkerHooks.MapPostfix(f.inner,f.ctx,f.gs.Map,true,changedTurn); world = f.World();
            Check(Status(world,Siege,"unavailable") && Withheld(world,Siege), "state turn changes inside full render cannot certify old prefix");
            f = new Fixture(); f.Complete(); MapRenderer.Current.mapRenderer = new Inner(); MapRenderer.Current.mapRenderer.stableId = f.inner.stableId; MapRenderer.Current.mapRenderer.renderedTiles = f.inner.renderedTiles;
            world = f.World();
            Check(Status(world,Siege,"ok"), "opaque native renderer identity tolerates a fresh managed wrapper");
            f = new Fixture(); f.Complete(); f.World(); f.fx.Stop(true); f.fx.particles[0].alive = true; f.tile.Data.captured = false; world = f.World();
            marker = (Dictionary<string,object>)((System.Collections.IList)Domain(world,Siege)["markers"])[0];
            Check(Status(world,Siege,"ok") && (string)marker["displayState"] == "fading", "actual world reuses classified same-lease tail without private recategorization");
            GameManager.LocalPlayer.Id = 3; f.controlled = 3; f.ctx.localPlayerId = 3; f.World(); f.Complete(); world = f.World();
            Check(Status(world,Siege,"partial") && Withheld(world,Siege), "actual player switch clears tail history and unknown stopped origin cannot become successful empty");
            f = new Fixture(); Field(f.bridge,"_publicMarkerHooks",null); world = f.World();
            Check(Status(world,Siege,"unavailable") && Status(world,Rainbow,"unavailable") && Parse(world).ContainsKey("tiles"), "binding or install failure does not break normal board observation");
            Check((string)Domain(world,Siege)["reasonCode"] == "native-bindings-unavailable",
                "actual absent marker hooks retain the binding unavailable diagnostic");
            PublicMarkerHooks.NativeView rejected = PublicMarkerHooks.NativeView.CreateNative(delegate(string name) { return typeof(GameManager); });
            Check(rejected == null, "production native view factory rejects fake assembly and guessed names");
            PublicMarkerHooks.NativeView exact = PublicMarkerHooks.NativeView.CreateForTests(Types(),Identity);
            Check(exact.ExactMethod("MapRendererInner","RenderMap",typeof(bool),"MapRenderContext","MapData") != null
                && exact.ExactMethod("MapRendererInner","RenderMap",typeof(void),"MapRenderContext","MapData") == null
                && exact.ExactMethod("Tile","Render",typeof(void),"MapRenderContext") != null
                && exact.ExactMethod("Tile","Render",typeof(void),"MapRenderContext","SkinVisualsTransientData") != null, "production hook lookup uses exact bool void and both Render signatures");
            Check(exact.ExactMethod("Tile","RenderImprovement",typeof(void),"MapRenderContext","SkinVisualsTransientData") != null,
                "resolved context observer targets exact native improvement render overload");
            Console.WriteLine("TOTAL "+passed+" passed"); return 0;
        }
    }
}
