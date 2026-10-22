// Load the production DLL; test transport predicates without live game/file mutation.
using System;
using System.Reflection;

internal static class TransportRuntimeTests
{
    private static int passed;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        passed++;
        Console.WriteLine("PASS " + name);
    }

    private static bool Call(MethodInfo method, string json, int pid)
    {
        return (bool)method.Invoke(null, new object[] { json, pid });
    }

    private static int Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var type = assembly.GetType("PolytopiaAgent.BridgeMod", true);
        var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var ready = type.GetMethod("TransportReadyPidMatches", flags);
        var request = type.GetMethod("TransportRequestPidMatches", flags);
        Check(Call(ready, "{\"pid\":6972}", 6972), "same ready PID owns transport");
        Check(!Call(ready, "{\"pid\":33184}", 6972), "other ready PID cannot own transport");
        Check(!Call(ready, "{}", 6972), "missing ready PID is not ownership");
        Check(!Call(ready, "{", 6972), "malformed ready JSON is not ownership");
        Check(!Call(ready, null, 6972), "unreadable ready is not ownership");
        Check(!Call(ready, "{\"pid\":true}", 1), "boolean PID is not integer ownership");
        Check(!Call(ready, "{\"pid\":6972.5}", 6972), "fractional PID is rejected");
        Check(!Call(ready, "{\"pid\":0}", 0), "zero process PID is rejected");
        Check(Call(request, "{\"id\":1,\"cmd\":\"ping\",\"bridgePid\":6972}", 6972), "request with bound PID targets its instance");
        Check(!Call(request, "{\"id\":1,\"cmd\":\"ping\",\"bridgePid\":6972}", 33184), "old client's request cannot target new process");
        Check(Call(request, "{\"id\":1,\"cmd\":\"ping\"}", 6972), "legacy envelope uses ready-owner guard without extra parameter");
        Check(!Call(request, "{\"bridgePid\":null}", 6972), "explicit null bridge PID does not use legacy bypass");
        Check(!Call(request, "{\"bridgePid\":true}", 1), "boolean bridge PID is rejected");
        Check(!Call(request, "{\"bridgePid\":6972.5}", 6972), "fractional bridge PID is rejected");
        Check(!Call(request, "{", 6972), "malformed request does not target any instance");
        Check(!Call(request, "{\"bridgePid\":33184,\"unit\":\"6972\",\"popupId\":\"6972\"}", 6972), "ordinary unit/popup fields cannot spoof request process binding");
        Console.WriteLine("passed=" + passed + " failed=0");
        return 0;
    }
}
