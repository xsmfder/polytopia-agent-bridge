using System;
using PolytopiaAgent;

internal static class PeaceResponseDisplayTests
{
    private sealed class Display
    {
        public int Hides, Responses;
        public void Hide() { Hides++; }
        public void Hide<T>() { throw new Exception("unbound generic must not hide real callback"); }
        public void Accept() { Responses++; throw new Exception("must not replay choice"); }
    }
    private sealed class ThrowingDisplay
    { public int Hides; public void Hide() { Hides++; throw new Exception("uncertain native hide"); } }
    private sealed class BooleanDisplay
    { public int Calls; public bool Hide() { Calls++; return true; } }
    private sealed class GenericDisplay
    { public int Calls; public void Hide<T>() { Calls++; } }
    private sealed class StaticDisplay
    { public static int Calls; public static void Hide() { Calls++; } }
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static bool Ready(BridgeMod.PeaceResponseDisplay display, BridgeMod.PublicDiplomacyObservation proof,
        bool context = true, bool samePopup = true, int? pending = 0, bool? incoming = false)
    { return BridgeMod.PeaceResponseDisplayCanDismiss(display, proof, context, samePopup, pending, incoming); }
    public static int Main()
    {
        try
        {
            var native = new Display();
            var display = new BridgeMod.PeaceResponseDisplay { Popup = native, Episode = "episode", Me = 2, Other = 1 };
            var proof = new BridgeMod.PublicDiplomacyObservation { Episode = "episode", Me = 2, Other = 1,
                Kind = "PeaceRequestResponseAction", Complete = true, Accepted = true, ResponseAuthorized = true,
                TriggerConsumed = true, OwnStateAfter = "Peace" };
            Check(Ready(display,proof), "completed authorized acceptance permits only bound display dismissal");
            Check(!Ready(display,proof,false), "changed seat episode stream or turn refuses dismissal");
            Check(!Ready(display,proof,true,false), "a different current popup is never dismissed");
            Check(!Ready(display,proof,true,true,1), "another pending choice prevents dismissal");
            Check(!Ready(display,proof,true,true,null), "unknown pending count prevents dismissal");
            Check(!Ready(display,proof,true,true,0,true), "remaining incoming request prevents dismissal");
            Check(!Ready(display,proof,true,true,0,null), "unreadable incoming request prevents dismissal");
            proof.Me = 1; Check(!Ready(display,proof), "different actor proof refuses dismissal"); proof.Me = 2;
            proof.Other = 3; Check(!Ready(display,proof), "different opponent proof refuses dismissal"); proof.Other = 1;
            proof.Episode = "old"; Check(!Ready(display,proof), "different episode proof refuses dismissal"); proof.Episode = "episode";
            proof.ResponseAuthorized = false; Check(!Ready(display,proof), "uncorrelated response cannot dismiss"); proof.ResponseAuthorized = true;
            proof.Complete = false; Check(!Ready(display,proof), "accepted dispatch alone cannot dismiss"); proof.Complete = true;
            proof.TriggerConsumed = false; Check(!Ready(display,proof), "unconsumed trigger cannot dismiss"); proof.TriggerConsumed = true;
            proof.Accepted = null; Check(!Ready(display,proof), "unknown response choice cannot dismiss"); proof.Accepted = true;
            proof.OwnStateAfter = "Neutral"; Check(!Ready(display,proof), "failed acceptance cannot dismiss");
            proof.Accepted = false; Check(Ready(display,proof), "completed authorized rejection permits dismissal too");
            proof.OwnStateAfter = "Peace"; Check(!Ready(display,proof), "rejection inconsistent with Peace cannot dismiss");
            proof.Accepted = true;
            Check(BridgeMod.HidePeaceResponseDisplayOnce(display) && native.Hides == 1 && native.Responses == 0,
                "invoke only exact display Hide without response callback");
            Check(!Ready(display,proof) && !BridgeMod.HidePeaceResponseDisplayOnce(display) && native.Hides == 1,
                "same display is never dismissed twice");
            var throwing = new ThrowingDisplay();
            var uncertain = new BridgeMod.PeaceResponseDisplay { Popup = throwing };
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(uncertain) && uncertain.Attempted && throwing.Hides == 1,
                "exception retains attempted state instead of replaying");
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(uncertain) && throwing.Hides == 1,
                "uncertain native hide never retried");
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(new BridgeMod.PeaceResponseDisplay { Popup = new object() }),
                "no native Hide method leaves display unchanged");
            var boolean = new BooleanDisplay(); var wrongReturn = new BridgeMod.PeaceResponseDisplay {Popup = boolean};
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(wrongReturn) && boolean.Calls == 0 && !wrongReturn.Attempted,
                "wrong-return UI method rejected before callback and attempted marker");
            var generic = new GenericDisplay(); var openMethod = new BridgeMod.PeaceResponseDisplay {Popup = generic};
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(openMethod) && generic.Calls == 0 && !openMethod.Attempted,
                "generic-only UI method is unavailable without invoking it");
            Check(!BridgeMod.HidePeaceResponseDisplayOnce(new BridgeMod.PeaceResponseDisplay {Popup = new StaticDisplay()})
                && StaticDisplay.Calls == 0, "static UI method is not an instance display callback");
            Console.WriteLine("PASS total " + passed); return 0;
        }
        catch(Exception e) { Console.WriteLine(e); return 1; }
    }
}
