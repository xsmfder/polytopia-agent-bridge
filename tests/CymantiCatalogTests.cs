using System;
using System.Collections;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using PolytopiaAgent;

namespace CymantiCatalogFixture
{
    public sealed class UnitData
    {
        public string type = "Warrior", displayName = "native";
        public int cost = 2;
        public bool hidden, BrokenPeriod;
        public List<string> unitAbilities = new List<string>();
        public int growthRate { get { if (BrokenPeriod) throw new Exception("period unavailable"); return 2; } }
    }
    public sealed class TechData
    {
        public string type = "NativeTech", displayName = "native";
        public int cost = 1;
        public bool BrokenMetadata;
        public List<UnitData> unitUnlocks = new List<UnitData> { new UnitData() };
        public List<string> abilityUnlocks { get { if (BrokenMetadata) throw new Exception("ability unavailable"); return new List<string> { "Microbes" }; } }
    }
    public sealed class ImprovementData
    {
        public string type = "Fungi", displayName = "native";
        public int cost = 5;
        public bool BrokenMetadata;
        public List<object> creates = new List<object>();
        public List<string> improvementAbilities { get { if (BrokenMetadata) throw new Exception("building ability unavailable"); return new List<string> { "Poison", "Heal" }; } }
    }
    public sealed class TribeData
    {
        public bool BrokenMetadata;
        public List<TechData> startingTech = new List<TechData>();
        public UnitData startingUnit = new UnitData();
        public Dictionary<string, UnitData> unitOverrides = new Dictionary<string, UnitData>();
        public List<string> tribeAbilities { get { if (BrokenMetadata) throw new Exception("tribe ability unavailable"); return new List<string> { "PoisonResist" }; } }
    }
    public sealed class Player { public string tribe = "Cymanti"; }
    public sealed class Logic
    {
        public bool BrokenMetadata, BrokenGrowth;
        public int ObservedOverrideCalls;
        public TribeData GetTribeData(string tribe) { return new TribeData { BrokenMetadata = BrokenMetadata }; }
        public List<TechData> GetAllTechForTribe(TribeData tribe) { return new List<TechData> { new TechData { BrokenMetadata = BrokenMetadata } }; }
        public List<ImprovementData> GetImprovementsFromTech(TechData tech, TribeData tribe) { return new List<ImprovementData> { new ImprovementData { BrokenMetadata = BrokenMetadata } }; }
        public TechData GetOverride(TechData data, TribeData tribe) { return data; }
        public ImprovementData GetOverride(ImprovementData data, TribeData tribe) { return data; }
        public UnitData GetOverride(UnitData data, TribeData tribe)
        {
            if (data.type != "Warrior") { ObservedOverrideCalls++; throw new Exception("observed data must not be overridden"); }
            return data;
        }
        public UnitData GetUnitWillGrowInto(UnitData data)
        {
            if (BrokenGrowth) throw new Exception("observed growth unavailable");
            return data.type == "ObservedEgg" ? new UnitData { type = "ObservedAdult", hidden = true } : null;
        }
    }
    public sealed class ThrowingObserved : IEnumerable<object>
    {
        public IEnumerator<object> GetEnumerator()
        {
            yield return new UnitData { type = "FirstObserved", hidden = true };
            throw new Exception("incomplete observed input");
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
}

internal static class CymantiCatalogTests
{
    static int assertions;
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); assertions++; }
    static Dictionary<string, object> Parse(string value) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(value); }
    static Dictionary<string, object> Row(Dictionary<string, object> catalog, string kind, string type)
    {
        foreach (Dictionary<string, object> row in (IList)catalog[kind]) if ((string)row["type"] == type) return row;
        return null;
    }
    static bool Contains(IList list, string value) { foreach (object item in list) if ((string)item == value) return true; return false; }
    public static int Main()
    {
        try { Run(); Console.WriteLine("PASS CymantiCatalogTests assertions=" + assertions); return 0; }
        catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    static void Run()
    {
        var player = new CymantiCatalogFixture.Player();
        var logic = new CymantiCatalogFixture.Logic();
        var baseline = Parse(BridgeMod.ProjectTribeCatalog(logic, player, null));
        Check((string)baseline["status"] == "ok" && (string)baseline["observedOwnedUnitDataStatus"] == "not_requested", "three-argument caller retains native roster");
        Check(Row(baseline, "units", "Segment") == null, "no hard-coded Eat product invented before observation");
        var native = Row(baseline, "units", "Warrior");
        Check((string)native["source"] == "tribe-native-rule" && (string)native["tribeRuleReachability"] == "native-rule", "native provenance explicit");
        Check((string)((Dictionary<string, object>)native["growthRule"])["status"] == "not_growing", "non-growing unit has explicit rule status");
        Check(Contains((IList)baseline["tribeAbilities"], "PoisonResist"), "exact tribeAbilities read");
        Check(Contains((IList)Row(baseline, "techs", "NativeTech")["playerAbilities"], "Microbes"), "player abilities use native abilityUnlocks field");
        Check(Contains((IList)Row(baseline, "improvements", "Fungi")["improvementAbilities"], "Poison"), "building abilities use exact native field");

        var segment = new CymantiCatalogFixture.UnitData { type = "Segment", hidden = true, cost = 77 };
        var converted = new CymantiCatalogFixture.UnitData { type = "ForeignConverted", hidden = false, cost = 19 };
        var egg = new CymantiCatalogFixture.UnitData { type = "ObservedEgg", hidden = true, unitAbilities = new List<string> { "Grow" } };
        var observed = Parse(BridgeMod.ProjectTribeCatalog(logic, player, null, new object[] { segment, segment, converted, egg }));
        Check((string)observed["status"] == "ok" && (string)observed["observedOwnedUnitDataStatus"] == "ok", "known own data supplement succeeds");
        Check(((IList)observed["units"]).Count == 5, "observed types deduplicated with native growth successor");
        var found = Row(observed, "units", "Segment");
        Check(found != null && (string)found["source"] == "observed-owned-unit" && (bool)found["hiddenFromTraining"], "observed hidden non-Grow unit retained with source");
        Check(Convert.ToInt32(found["baseCost"]) == 77 && logic.ObservedOverrideCalls == 0, "real observed data never coerced through tribe override");
        var foreign = Row(observed, "units", "ForeignConverted");
        Check((string)foreign["tribeRuleReachability"] == "unproven", "ownership never claims converted foreign unit is tribe reachable");
        var unlocks = (Dictionary<string, object>)Row(observed, "techs", "NativeTech")["unlocks"];
        Check(((IList)unlocks["units"]).Count == 1 && Contains((IList)unlocks["units"], "Warrior"), "observed units do not pollute technology unlocks");
        var growth = (Dictionary<string, object>)Row(observed, "units", "ObservedEgg")["growthRule"];
        Check((string)growth["status"] == "growing" && (string)growth["nextStage"] == "ObservedAdult" && Convert.ToInt32(growth["growthPeriodTurns"]) == 2, "readable actual Grow successor and period exposed");
        Check((string)Row(observed, "units", "ObservedAdult")["source"] == "observed-owned-unit-growth", "observed growth successor does not become tribe-native claim");

        var broken = Parse(BridgeMod.ProjectTribeCatalog(new CymantiCatalogFixture.Logic { BrokenMetadata = true }, player, null));
        Check((string)broken["status"] == "ok" && Row(broken, "units", "Warrior") != null, "optional metadata failure preserves established roster");
        Check(broken["tribeAbilities"] == null && Row(broken, "techs", "NativeTech")["playerAbilities"] == null && Row(broken, "improvements", "Fungi")["improvementAbilities"] == null, "unreadable optional lists remain null, never empty");
        var brokenGrow = Parse(BridgeMod.ProjectTribeCatalog(new CymantiCatalogFixture.Logic { BrokenGrowth = true }, player, null, new object[] { egg }));
        var unknownGrowth = (Dictionary<string, object>)Row(brokenGrow, "units", "ObservedEgg")["growthRule"];
        Check((string)brokenGrow["status"] == "ok" && (string)brokenGrow["observedOwnedUnitDataStatus"] == "partial" && unknownGrowth["nextStage"] == null, "unknown supplemental growth does not erase native directory");
        var badPeriod = new CymantiCatalogFixture.UnitData { type = "ObservedEgg", hidden = true, BrokenPeriod = true, unitAbilities = new List<string> { "Grow" } };
        var periodCatalog = Parse(BridgeMod.ProjectTribeCatalog(logic, player, null, new object[] { badPeriod }));
        var period = (Dictionary<string, object>)Row(periodCatalog, "units", "ObservedEgg")["growthRule"];
        Check((string)periodCatalog["status"] == "ok" && period["growthPeriodTurns"] == null && (string)period["status"] == "unavailable", "unknown period not invented or fatal");
        var partial = Parse(BridgeMod.ProjectTribeCatalog(logic, player, null, new CymantiCatalogFixture.ThrowingObserved()));
        Check((string)partial["status"] == "ok" && (string)partial["observedOwnedUnitDataStatus"] == "partial" && Row(partial, "units", "FirstObserved") != null, "partial optional enumeration preserves known positive units");
        var noRepeat = Parse(BridgeMod.ProjectTribeCatalog(logic, player, null, new object[] { new CymantiCatalogFixture.UnitData { type = "Warrior", cost = 99 } }));
        Check(Convert.ToInt32(Row(noRepeat, "units", "Warrior")["baseCost"]) == 2 && (string)Row(noRepeat, "units", "Warrior")["source"] == "tribe-native-rule", "observed duplicate cannot overwrite native rule row");
    }
}
