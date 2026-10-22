using System;
using PolytopiaAgent;

internal static class ForestActionTests
{
    private const string Target = "(10,5)";
    private static int passed;
    private static string Snapshot(string terrains, string resources, string stars, string player)
    {
        return "player=" + player + "|units=|imps=|stars=" + stars
            + (terrains == null ? "" : "|terrains=" + terrains)
            + (resources == null ? "" : "|res=" + resources);
    }
    private static string F(string terrain, string resource, string stars)
    { return Snapshot(Target + ":" + terrain, resource, stars, "1"); }
    private static void Check(string title, string kind, string before, string after, string expected)
    {
        string basis;
        string actual = BridgeMod.ConfirmAction("1", "build", null, Target, kind, before, after, out basis);
        if (actual != expected) throw new Exception(title + ": " + actual + "; " + basis);
        passed++;
        Console.WriteLine("PASS " + title);
    }
    public static int Main()
    {
        try
        {
            Check("plant named empty field", "GrowForest", F("Field", "", "5"), F("Forest", "", "0"), "confirmed");
            Check("plant preserves other resource evidence", "GrowForest", F("Field", "(12,5):Crop", "5"), F("Forest", "(12,5):Crop", "0"), "confirmed");
            Check("star debit without terrain change is insufficient", "GrowForest", F("Field", "", "5"), F("Field", "", "0"), "not_confirmed");
            Check("plant did not start on field", "GrowForest", F("Forest", "", "5"), F("Forest", "", "0"), "not_confirmed");
            Check("plant cannot turn mountain into forest", "GrowForest", F("Mountain", "", "5"), F("Forest", "", "0"), "not_confirmed");
            Check("plant target absent before", "GrowForest", Snapshot("(11,5):Field", "", "5", "1"), F("Forest", "", "0"), "unknown");
            Check("another field turning into forest is insufficient", "GrowForest", Snapshot(Target + ":Field;(11,5):Field", "", "5", "1"), Snapshot(Target + ":Field;(11,5):Forest", "", "0", "1"), "not_confirmed");
            Check("plant target absent after", "GrowForest", F("Field", "", "5"), Snapshot("(11,5):Forest", "", "0", "1"), "unknown");
            Check("plant terrain field missing", "GrowForest", Snapshot(null, "", "5", "1"), F("Forest", "", "0"), "unknown");
            Check("burn creates crop on named tile", "BurnForest", F("Forest", "", "3"), F("Field", Target + ":Crop", "0"), "confirmed");
            Check("burn replaces visible game", "BurnForest", F("Forest", Target + ":Game", "3"), F("Field", Target + ":Crop", "0"), "confirmed");
            Check("burn no terrain change despite crop is insufficient", "BurnForest", F("Forest", "", "3"), F("Forest", Target + ":Crop", "0"), "not_confirmed");
            Check("burn target already field", "BurnForest", F("Field", "", "3"), F("Field", Target + ":Crop", "0"), "not_confirmed");
            Check("burn other tile crop is insufficient", "BurnForest", F("Forest", "", "3"), F("Field", "(11,5):Crop", "0"), "unknown");
            Check("burn missing resource field", "BurnForest", F("Forest", "", "3"), F("Field", null, "0"), "unknown");
            Check("burn unreadable or gated resource stays unknown", "BurnForest", F("Forest", "", "3"), F("Field", "", "0"), "unknown");
            Check("burn wrong visible resource", "BurnForest", F("Forest", "", "3"), F("Field", Target + ":Fruit", "0"), "not_confirmed");
            Check("burn missing named terrain", "BurnForest", F("Forest", "", "3"), Snapshot("(11,5):Field", Target + ":Crop", "0", "1"), "unknown");
            Check("other seat cannot confirm plant", "GrowForest", F("Field", "", "5"), Snapshot(Target + ":Forest", "", "0", "2"), "unknown");
            Check("clear forest remains confirmed", "ClearForest", F("Forest", "", "5"), F("Field", "", "6"), "confirmed");
            Check("clear forest star change alone insufficient", "ClearForest", F("Forest", "", "5"), F("Forest", "", "6"), "not_confirmed");
            Check("fruit harvest remains confirmed", "HarvestFruit", F("Field", Target + ":Fruit", "5"), F("Field", "", "3"), "confirmed");
            Console.WriteLine("PASS total " + passed);
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 1; }
    }
}
