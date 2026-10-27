using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;
using PolytopiaAgent;

internal static class CymantiProofTests
{
    const BindingFlags F=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static;
    static int passed;
    static readonly BridgeMod Host=new BridgeMod();
    static readonly Type Pass=typeof(BridgeMod).GetNestedType("CombatHookPass",F);
    static void Check(bool yes,string name){if(!yes)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
    static object Call(string name,params object[] args){return typeof(BridgeMod).GetMethod(name,F).Invoke(Host,args);}
    static void Set(object obj,string name,object value){obj.GetType().GetField(name,F).SetValue(obj,value);}
    static BridgeMod.CombatFrame Frame(){return new BridgeMod.CombatFrame{Episode="cymanti",Me="1",Turn="3",Complete=true};}
    static BridgeMod.CombatUnitView Unit(string id,string owner,string coord)
    {return new BridgeMod.CombatUnitView{Id=id,Owner=owner,Coord=coord,Health=100,Effects="[]",Poisoned=false};}
    static BridgeMod.CombatTicket Ticket(string name,BridgeMod.CombatFrame frame,BridgeMod.CombatUnitView actor)
    {frame.Units[actor.Id]=actor;return new BridgeMod.CombatTicket{Before=frame,Action=name,UnitId=actor.Id,Coord=actor.Coord,Accepted=true,Dispatched=true};}
    static bool Bind(BridgeMod.CombatTicket t,BridgeMod.CombatFrame frame,object action,string kind)
    {return (bool)Call("CymantiBind",t,frame,action,kind);}
    static object Hook(string kind,object action,BridgeMod.CombatFrame frame,BridgeMod.CombatTicket t)
    {
        var p=Activator.CreateInstance(Pass,true);Set(p,"CandidateKind",kind);Set(p,"Action",action);Set(p,"Before",frame);
        Set(p,"CandidateCoord",(action as CymantiActionFixture).Coordinates??(action as CymantiActionFixture).Target);
        ((IList)Pass.GetField("ProofTickets",F).GetValue(p)).Add(t);return p;
    }
    static void Record(string kind,object action,BridgeMod.CombatFrame before,BridgeMod.CombatFrame after,BridgeMod.CombatTicket t)
    {Call("CymantiRecord",Hook(kind,action,before,t),after,t);}
    static void Child(BridgeMod.CombatTicket t,string kind,object action)
    {t.Cymanti.Children[kind]=new List<object>{action};}
    static string Confirmation(BridgeMod.CombatTicket t,BridgeMod.CombatFrame frame)
    {var args=new object[]{t,frame,null};return (string)typeof(BridgeMod).GetMethod("CymantiConfirmation",F).Invoke(null,args);}
    static void Main()
    {
        var frame=Frame();frame.PublicTerrains["(2,2)"]="Field";
        var actor=Unit("10","1","(2,2)");actor.Swarm=true;
        var swarm=Ticket("swarm",frame,actor);
        var root=new CymantiActionFixture{PlayerId=1,Coordinates="(2,2)"};
        Check(Bind(swarm,frame,root,"SwarmCommand"),"named own actual Swarm root binds");
        root.PlayerId=2;Check(!Bind(swarm,frame,root,"SwarmCommand"),"foreign Swarm root rejected");root.PlayerId=1;
        actor.Swarm=null;Check(!Bind(swarm,frame,root,"SwarmCommand"),"unknown actual Swarm capability rejected");actor.Swarm=true;
        var target=Unit("11","1","(3,2)");frame.Units[target.Id]=target;
        var boost=new CymantiActionFixture{PlayerId=1,Coordinates=target.Coord};
        Check(!Bind(swarm,frame,boost,"BoostAction"),"unrelated visible BoostAction cannot inherit Swarm");
        Child(swarm,"BoostAction",boost);
        Check(Bind(swarm,frame,boost,"BoostAction"),"precise Swarm appended Boost child accepted");
        Check(!Bind(swarm,frame,new CymantiActionFixture{PlayerId=1,Coordinates=target.Coord},"BoostAction"),"same-coordinate other Boost object rejected");
        var boosted=Frame();boosted.Units[actor.Id]=actor;var fast=Unit("11","1",target.Coord);fast.Effects="[\"Swift\"]";boosted.Units[fast.Id]=fast;
        swarm.NativeVersion=123;Record("BoostAction",boost,frame,boosted,swarm);
        Check(swarm.NativeBoostTargets.Contains("11"),"executed exact child records own Swift addition");
        Check(!swarm.Cymanti.SwarmExecuted,"child alone does not fabricate executed root");
        Record("SwarmCommand",root,frame,boosted,swarm);
        Check(swarm.Cymanti.SwarmExecuted,"completed native Swarm root recorded separately");

        var middleTicket=Ticket("swarm",frame,actor);middleTicket.NativeVersion=123;
        var middle=new CymantiActionFixture{PlayerId=1,Coordinates=actor.Coord};
        var activeSwarm=(IList)typeof(BridgeMod).GetField("_cymantiActivePasses",F).GetValue(Host);
        Check(!Bind(middleTicket,frame,middle,"BoostOthersAction"),"unassociated BoostOthers cannot inherit Swarm");
        activeSwarm.Add(Hook("SwarmCommand",root,frame,middleTicket));
        Check(Bind(middleTicket,frame,middle,"BoostOthersAction"),"actual nested own Swarm BoostOthers middle binds");
        middle.PlayerId=2;Check(!Bind(middleTicket,frame,middle,"BoostOthersAction"),"foreign middle rejected even inside own root");middle.PlayerId=1;
        middle.Coordinates=target.Coord;Check(Bind(middleTicket,frame,middle,"BoostOthersAction"),"precise middle association does not guess its coordinate semantics");middle.Coordinates=actor.Coord;
        activeSwarm.Clear();activeSwarm.Add(Hook("BoostOthersAction",middle,frame,middleTicket));
        Check(Bind(middleTicket,frame,boost,"BoostAction"),"own Boost child binds inside associated middle");
        Check(!Bind(Ticket("swarm",frame,actor),frame,boost,"BoostAction"),"different ticket cannot inherit active middle");
        Record("BoostAction",boost,frame,boosted,middleTicket);
        Check(middleTicket.NativeBoostTargets.Contains(target.Id),"middle chain records native Swift addition");
        activeSwarm.Clear();
        Check(!Bind(middleTicket,frame,new CymantiActionFixture{PlayerId=1,Coordinates=target.Coord},"BoostAction"),"unrelated later boost remains unassociated");
        Check((bool)Call("CymantiIsRoot","BoostOthersAction"),"associated middle captures its own exact appended Boost children");

        var explodeFrame=Frame();explodeFrame.PublicTerrains[actor.Coord]="Field";actor.Explode=true;
        var explode=Ticket("explode",explodeFrame,actor);
        Check(!BridgeMod.CRAreaActorMatches(explode,"1",actor.Id,actor.Coord),"explosion area requires known native root binding");
        Check(Bind(explode,explodeFrame,root,"ExplodeUnitAction"),"actual Explode capability and named root bind");
        Check(BridgeMod.CRAreaActorMatches(explode,"1",actor.Id,actor.Coord),"known own explosion permits its native AreaDamage helper");
        var removed=Frame();
        Check(Confirmation(explode,removed)=="unknown","disappearance without executed root and kill stays unknown");
        Record("ExplodeUnitAction",root,explodeFrame,explodeFrame,explode);
        Check(Confirmation(explode,removed)=="unknown","executed root alone does not fabricate self consumption");
        var kill=new CymantiActionFixture{PlayerId=1,Coordinates=actor.Coord};
        Check(!Bind(explode,explodeFrame,kill,"KillUnitAction"),"unrelated own kill rejected");Child(explode,"KillUnitAction",kill);
        Check(Bind(explode,explodeFrame,kill,"KillUnitAction"),"exact own self kill accepted");
        Record("KillUnitAction",kill,explodeFrame,removed,explode);
        Check(Confirmation(explode,removed)=="confirmed","native root plus exact self kill and final absence confirms explosion");
        Check(Confirmation(explode,explodeFrame)=="unknown","unit remaining present cannot confirm consumption");
        var direct=Ticket("explode",explodeFrame,actor);
        Check(Bind(direct,explodeFrame,root,"ExplodeUnitAction"),"direct-consumption native root binds");
        Record("ExplodeUnitAction",root,explodeFrame,removed,direct);
        Check(Confirmation(direct,removed)=="confirmed" && direct.Cymanti.ExploderConsumptionSource=="executed-ExplodeUnitAction-before-after",
            "direct native explosion consumption does not invent a KillUnitAction");

        var enemy=Unit("20","2","(3,3)");explodeFrame.Units[enemy.Id]=enemy;
        var poison=new CymantiActionFixture{PlayerId=1,Origin=actor.Coord,Target=enemy.Coord};
        Check(!Bind(explode,explodeFrame,poison,"PoisonUnitAction"),"visible enemy poison still requires precise parent proof");
        Child(explode,"PoisonUnitAction",poison);
        Check(Bind(explode,explodeFrame,poison,"PoisonUnitAction"),"exact explosion poison child uses disclosed enemy only");
        poison.PlayerId=2;Check(!Bind(explode,explodeFrame,poison,"PoisonUnitAction"),"foreign poison action rejected");poison.PlayerId=1;
        var poisoned=Frame();var affected=Unit(enemy.Id,enemy.Owner,enemy.Coord);affected.Health=90;affected.Poisoned=true;poisoned.Units[affected.Id]=affected;
        Record("PoisonUnitAction",poison,explodeFrame,poisoned,explode);
        var outcome=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)Call("CymantiOutcome",explode,poisoned));
        Check((bool)outcome["completeAffectedSet"]==false && ((IList)outcome["knownEffects"]).Count==1,"actual visible poison is reported without claiming all hidden effects");
        poison.Target="(9,9)";Check(!Bind(explode,explodeFrame,poison,"PoisonUnitAction"),"hidden or unavailable target excluded even with known child reference");poison.Target=enemy.Coord;
        var changed=Frame();changed.Episode="other";Check(!Bind(explode,changed,poison,"PoisonUnitAction"),"child proof cannot cross episode");

        var directDamage=Ticket("explode",explodeFrame,actor);
        Check(Bind(directDamage,explodeFrame,root,"ExplodeUnitAction"),"direct damage proof has named own root");
        Record("ExplodeUnitAction",root,explodeFrame,poisoned,directDamage);
        var damageOutcome=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>((string)Call("CymantiOutcome",directDamage,poisoned));
        var damageRows=(IList)damageOutcome["knownEffects"];
        Check(damageRows.Count==1 && (string)((Dictionary<string,object>)damageRows[0])["role"]=="explosion_damage_target",
            "root direct visible HP decrease is reported without requiring an invented damage child");
        var noSight=Frame();var visibilityOnly=Ticket("explode",explodeFrame,actor);
        Record("ExplodeUnitAction",root,explodeFrame,noSight,visibilityOnly);
        Check(visibilityOnly.Cymanti.ExplosionDamage.Count==0,"target disappearing from view is not invented explosion damage or death");
        var queuedExplosion=Ticket("explode",explodeFrame,actor);
        Check(Bind(queuedExplosion,explodeFrame,root,"ExplodeUnitAction"),"queued explosion root binds before actor consumption");
        var beforeQueuedDamage=Frame();beforeQueuedDamage.Units[enemy.Id]=enemy;
        Record("ExplodeUnitAction",root,explodeFrame,beforeQueuedDamage,queuedExplosion);
        var queuedAttack=new CymantiActionFixture{PlayerId=1,Target=enemy.Coord};
        Check(!Bind(queuedExplosion,beforeQueuedDamage,queuedAttack,"AttackAction"),"unassociated damage cannot inherit an explosion");
        Child(queuedExplosion,"AttackAction",queuedAttack);
        Check(Bind(queuedExplosion,beforeQueuedDamage,queuedAttack,"AttackAction"),"exact queued explosion attack binds after own actor disappears");
        Check(!Bind(queuedExplosion,beforeQueuedDamage,new CymantiActionFixture{PlayerId=1,Target=enemy.Coord},"AttackAction"),
            "same-target unrelated attack cannot inherit the explosion");
        queuedAttack.PlayerId=2;Check(!Bind(queuedExplosion,beforeQueuedDamage,queuedAttack,"AttackAction"),"explicit foreign explosion attack remains rejected");queuedAttack.PlayerId=1;
        Record("AttackAction",queuedAttack,beforeQueuedDamage,poisoned,queuedExplosion);
        Check(queuedExplosion.Cymanti.ExplosionDamage[enemy.Id].Before.Health==100
            && queuedExplosion.Cymanti.ExplosionDamage[enemy.Id].After.Health==90
            && queuedExplosion.Cymanti.ExplosionDamageSource[enemy.Id]=="exact-ExplodeUnitAction-AttackAction-child",
            "deferred root child records its exact native damage boundary without a fictitious AreaDamage helper");
        var poisonFrame=Frame();var exida=Unit("200","1","(2,2)");exida.PoisonAttack=true;
        var poisonTicket=Ticket("attack",poisonFrame,exida);
        var primary=Unit("201","2","(4,2)");var neighbour=Unit("202","2","(5,3)");
        poisonFrame.Units[primary.Id]=primary;poisonFrame.Units[neighbour.Id]=neighbour;
        var primaryPoison=new CymantiActionFixture{PlayerId=1,Origin=exida.Coord,Target=primary.Coord};
        var spreadPoison=new CymantiActionFixture{PlayerId=1,Origin=primary.Coord,Target=neighbour.Coord};
        Child(poisonTicket,"PoisonUnitAction",primaryPoison);
        Check(Bind(poisonTicket,poisonFrame,primaryPoison,"PoisonUnitAction"),"own attack's exact primary poison binds");
        Check(!Bind(poisonTicket,poisonFrame,spreadPoison,"PoisonUnitAction"),"neighbour poison is not inferred from proximity");
        var poisonActive=(IList)typeof(BridgeMod).GetField("_cymantiActivePasses",F).GetValue(Host);
        poisonActive.Add(Hook("PoisonUnitAction",primaryPoison,poisonFrame,poisonTicket));
        Check(Bind(poisonTicket,poisonFrame,spreadPoison,"PoisonUnitAction"),"bound primary PoisonUnitAction can execute an exact poison spread child");
        spreadPoison.PlayerId=2;Check(!Bind(poisonTicket,poisonFrame,spreadPoison,"PoisonUnitAction"),"foreign poison still rejected inside owned spread parent");spreadPoison.PlayerId=1;
        Check(!Bind(Ticket("attack",poisonFrame,exida),poisonFrame,spreadPoison,"PoisonUnitAction"),"another ticket cannot inherit active poison parent");
        poisonActive.Clear();
        Check(!Bind(poisonTicket,poisonFrame,new CymantiActionFixture{PlayerId=1,Origin=primary.Coord,Target=neighbour.Coord},"PoisonUnitAction"),
            "same target poison after parent ends remains unrelated");
        var spreadAfter=Frame();var poisonedNeighbour=Unit(neighbour.Id,neighbour.Owner,neighbour.Coord);poisonedNeighbour.Poisoned=true;
        spreadAfter.Units[poisonedNeighbour.Id]=poisonedNeighbour;
        Record("PoisonUnitAction",spreadPoison,poisonFrame,spreadAfter,poisonTicket);
        Check(poisonTicket.Cymanti.Poison[neighbour.Id].Before.Health==poisonTicket.Cymanti.Poison[neighbour.Id].After.Health
            && poisonTicket.Cymanti.Poison[neighbour.Id].After.Poisoned==true
            && poisonTicket.Cymanti.Poison[neighbour.Id].Association=="executed-native-poison-spread-child",
            "actual poison-only splash retains its spread-child association without invented HP damage");
        Check((bool)Call("CymantiIsRoot","PoisonUnitAction"),"bound poison root captures exact deferred poison references");

        var nativeSourceTicket=Ticket("attack",poisonFrame,exida);nativeSourceTicket.Coord=primary.Coord;
        var nativePoison=new CymantiActionFixture{PlayerId=1,Origin=exida.Coord,Target=neighbour.Coord};
        Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"explicit own poison source without matched requested hit stays unrelated");
        var damagedPrimary=Unit(primary.Id,primary.Owner,primary.Coord);damagedPrimary.Health=60;
        nativeSourceTicket.NativePrimaryBefore=primary;nativeSourceTicket.NativePrimaryAfter=damagedPrimary;
        Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"primary snapshots without a matched native attack target do not bind");
        nativeSourceTicket.NativeAttackTargets.Add(primary.Id);
        Check(Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"native explicit own Origin binds separate collateral poison after the exact primary hit");
        var sourceTickets=(IDictionary)typeof(BridgeMod).GetField("_combatTickets",F).GetValue(Host);
        var competing=Ticket("attack",poisonFrame,exida);competing.Coord=primary.Coord;
        competing.NativePrimaryBefore=primary;competing.NativePrimaryAfter=damagedPrimary;
        sourceTickets.Add(699L,competing);
        Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"two pending hits from the same actor cannot share unreferenced poison");
        sourceTickets.Clear();
        Record("PoisonUnitAction",nativePoison,poisonFrame,spreadAfter,nativeSourceTicket);
        Check(nativeSourceTicket.Cymanti.Poison[neighbour.Id].Association=="executed-native-PoisonUnitAction-own-Origin-after-matched-primary",
            "separate native source is reported without claiming a child reference");
        nativePoison.PlayerId=2;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"foreign player cannot use own Origin association");nativePoison.PlayerId=1;
        nativePoison.Origin=primary.Coord;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"another native Origin cannot inherit collateral association");nativePoison.Origin=exida.Coord;
        poisonFrame.Units.Remove(neighbour.Id);Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"undisclosed collateral target is never read or bound");poisonFrame.Units[neighbour.Id]=neighbour;
        neighbour.Owner="1";Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"friendly collateral cannot inherit the hostile poison path");neighbour.Owner="2";
        neighbour.Owner=null;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"unknown target owner cannot inherit collateral poison");neighbour.Owner="2";
        nativePoison.PlayerId=0;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"neutral source does not establish explicit own Origin proof");nativePoison.PlayerId=1;
        nativeSourceTicket.Dispatched=false;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"undispatched ticket cannot inherit native poison");nativeSourceTicket.Dispatched=true;
        var later=Frame();later.Turn="4";later.Units[exida.Id]=exida;later.Units[neighbour.Id]=neighbour;
        Check(!Bind(nativeSourceTicket,later,nativePoison,"PoisonUnitAction"),"native poison cannot cross turns");
        later.Turn="3";later.Episode="another";Check(!Bind(nativeSourceTicket,later,nativePoison,"PoisonUnitAction"),"native poison cannot cross episodes");
        exida.PoisonAttack=null;Check(!Bind(nativeSourceTicket,poisonFrame,nativePoison,"PoisonUnitAction"),"unknown actual poison capability cannot bind");exida.PoisonAttack=true;

        var eatFrame=Frame();var eater=Unit("30","1","(4,4)");eater.Eat=true;
        eatFrame.PublicTerrains[eater.Coord]="Field";
        var eat=Ticket("attack",eatFrame,eater);eat.Coord="(3,3)";
        var meal=new CymantiActionFixture{PlayerId=1,Eater=30,Coordinates=eater.Coord};
        Check(!Bind(eat,eatFrame,meal,"EatAction"),"Eater identity alone cannot prove causal attack");
        Child(eat,"EatAction",meal);Check(Bind(eat,eatFrame,meal,"EatAction"),"exact attack child binds actual own Eater");
        meal.Eater=31;Check(!Bind(eat,eatFrame,meal,"EatAction"),"another Eater cannot inherit own attack");meal.Eater=30;
        Record("EatAction",meal,eatFrame,eatFrame,eat);Check(eat.Cymanti.EatExecutions==1,"executed Eat counted separately from attack damage");
        var train=new CymantiActionFixture{PlayerId=1,Coordinates="(4,4)",Type="Segment",trainActionAborted=false};
        Child(eat,"TrainAction",train);eat.Cymanti.EatTrains.Add(train);
        Check(Bind(eat,eatFrame,train,"TrainAction"),"exact Eat train child binds after parent execution");
        var born=Frame();var segment=Unit("31","1",train.Coordinates);segment.Type="Segment";born.Units[segment.Id]=segment;
        Record("TrainAction",train,eatFrame,born,eat);Check(eat.Cymanti.Generated.ContainsKey("31"),"actual new own native train type and identity recorded");
        eat.Cymanti.Generated.Clear();train.Type="Warrior";Record("TrainAction",train,eatFrame,born,eat);
        Check(eat.Cymanti.Generated.Count==0,"different actual train type cannot fabricate Segment");
        eater.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasFollower\":true,\"followerId\":\"31\"}";
        segment.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"30\"}";
        born.Units[eater.Id]=eater;Record("EatAction",meal,eatFrame,born,eat);
        Check(eat.Cymanti.Generated.ContainsKey(segment.Id)
            && eat.Cymanti.GeneratedAssociation[segment.Id]=="executed-EatAction-reciprocal-own-chain",
            "native Eat direct creation requires reciprocal actual own links and no invented TrainAction");
        eat.Cymanti.Generated.Clear();segment.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"99\"}";
        Record("EatAction",meal,eatFrame,born,eat);
        Check(eat.Cymanti.Generated.Count==0,"unrelated own new unit without reciprocal Eater link is rejected");
        var chained=Ticket("attack",eatFrame,eater);chained.Coord="(3,3)";
        var killedTarget=new CymantiActionFixture{PlayerId=1,Coordinates=chained.Coord};
        var parent=Hook("KillUnitAction",killedTarget,eatFrame,chained);
        var active=(IList)typeof(BridgeMod).GetField("_cymantiActivePasses",F).GetValue(Host);active.Add(parent);
        Check(Bind(chained,eatFrame,meal,"EatAction"),"real primary KillUnitAction can execute the causal Eat child");
        Check(!Bind(Ticket("attack",eatFrame,eater),eatFrame,meal,"EatAction"),"another ticket cannot inherit the active kill parent");
        active.Clear();
        Check(!Bind(chained,eatFrame,new CymantiActionFixture{PlayerId=1,Eater=30,Coordinates=eater.Coord},"EatAction"),
            "after parent ends an unrelated same-coordinate Eat remains unbound");

        var chainBefore=Frame();var chainHead=Unit("80","1","(4,4)");chainHead.Eat=true;
        chainHead.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasFollower\":true,\"followerId\":\"81\"}";
        var tail=Unit("81","1","(4,5)");
        tail.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"80\"}";
        chainBefore.Units[tail.Id]=tail;
        var growingChain=Ticket("attack",chainBefore,chainHead);
        var chainAfter=Frame();chainAfter.Units[chainHead.Id]=chainHead;
        var extendedTail=Unit("81","1",tail.Coord);
        extendedTail.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"80\",\"hasFollower\":true,\"followerId\":\"82\"}";
        var appended=Unit("82","1","(4,6)");
        appended.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"81\"}";
        chainAfter.Units[extendedTail.Id]=extendedTail;chainAfter.Units[appended.Id]=appended;
        var chainMeal=new CymantiActionFixture{PlayerId=1,Eater=80,Coordinates=chainHead.Coord};
        Record("EatAction",chainMeal,chainBefore,chainAfter,growingChain);
        Check(growingChain.Cymanti.Generated.ContainsKey(appended.Id),"Eat recognizes a new segment appended after an existing reciprocal own tail");
        growingChain.Cymanti.Generated.Clear();extendedTail.Owner="2";
        Record("EatAction",chainMeal,chainBefore,chainAfter,growingChain);
        Check(growingChain.Cymanti.Generated.Count==0,"foreign intermediate chain cannot establish own generation");
        extendedTail.Owner="1";extendedTail.Links=tail.Links;
        Record("EatAction",chainMeal,chainBefore,chainAfter,growingChain);
        Check(growingChain.Cymanti.Generated.Count==0,"missing forward tail link cannot establish generation");
        var tailMeal=new CymantiActionFixture{PlayerId=1,Eater=81,Coordinates="(9,9)"};
        Child(growingChain,"EatAction",tailMeal);
        Check(Bind(growingChain,chainBefore,tailMeal,"EatAction"),"precise own Eat may name a known reciprocal tail and spawn beyond previous terrain disclosure");
        tailMeal.Eater=99;Check(!Bind(growingChain,chainBefore,tailMeal,"EatAction"),"unknown Eater cannot inherit an own chain");tailMeal.Eater=81;
        var queuedKill=new CymantiActionFixture{PlayerId=1,Coordinates=growingChain.Coord};
        Child(growingChain,"KillUnitAction",queuedKill);
        Check(Bind(growingChain,chainBefore,queuedKill,"KillUnitAction"),"exact appended primary kill can establish the native Eat parent chain");
        Check(!Bind(growingChain,chainBefore,new CymantiActionFixture{PlayerId=1,Coordinates=growingChain.Coord},"KillUnitAction"),
            "unrelated same-coordinate primary kill is not a causal Eat parent");

        var commandBefore=Frame();var boundaryHead=Unit("90","1","(4,4)");boundaryHead.Eat=true;
        var boundaryTicket=Ticket("attack",commandBefore,boundaryHead);boundaryTicket.Coord="(5,4)";
        var victim=Unit("91","2",boundaryTicket.Coord);victim.Health=20;commandBefore.Units[victim.Id]=victim;
        var dead=Unit(victim.Id,victim.Owner,victim.Coord);dead.Health=0;
        boundaryTicket.NativePrimaryBefore=victim;boundaryTicket.NativePrimaryAfter=dead;
        boundaryTicket.NativeAttackTargets.Add(victim.Id);boundaryTicket.NativeAttackKilledIds.Add(victim.Id);
        var eatPrefix=Frame();eatPrefix.Units[boundaryHead.Id]=boundaryHead;
        var eatAfter=Frame();var grownHead=Unit("90","1","(5,4)");grownHead.Eat=true;
        grownHead.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasFollower\":true,\"followerId\":\"92\"}";
        var grownTail=Unit("92","1","(4,4)");grownTail.Type="Segment";
        grownTail.Links="{\"linkAuthority\":\"owned-native-HasLeader/HasFollower\",\"hasLeader\":true,\"leaderId\":\"90\"}";
        eatAfter.Units[grownHead.Id]=grownHead;eatAfter.Units[grownTail.Id]=grownTail;
        var deferredEat=new CymantiActionFixture{PlayerId=1,Eater=0,Coordinates="(4,4)"};
        Check(!Bind(boundaryTicket,eatPrefix,deferredEat,"EatAction"),"deferred unreferenced Eat is not pre-confirmed by time alone");
        Check(BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),
            "executed Eat boundary with own reciprocal generation and exact native primary kill is sufficient");
        boundaryTicket.NativeAttackKilledIds.Clear();
        Check(!BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),"visible target absence without native kill does not bind deferred Eat");
        boundaryTicket.NativeAttackKilledIds.Add(victim.Id);deferredEat.PlayerId=2;
        Check(!BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),"explicit foreign deferred Eat remains rejected");deferredEat.PlayerId=1;
        eatPrefix.Units[grownTail.Id]=grownTail;
        Check(!BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),"existing chain member is not newly generated");eatPrefix.Units.Remove(grownTail.Id);
        grownTail.Owner="2";Check(!BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),"foreign generated unit cannot establish own Eat");grownTail.Owner="1";
        eatAfter.Turn="4";Check(!BridgeMod.CymantiEatBoundaryMatches(boundaryTicket,eatPrefix,eatAfter,deferredEat),"deferred Eat cannot cross a turn");eatAfter.Turn="3";
        var deferredPass=Hook("EatAction",deferredEat,eatPrefix,boundaryTicket);
        ((IList)Pass.GetField("ProofTickets",F).GetValue(deferredPass)).Clear();
        Set(deferredPass,"Cause",Activator.CreateInstance(typeof(BridgeMod).GetNestedType("CombatCause",F),true));
        boundaryTicket.Stream="boundary-stream";Set(Host,"_combatStream",boundaryTicket.Stream);
        var tickets=(IDictionary)typeof(BridgeMod).GetField("_combatTickets",F).GetValue(Host);tickets.Add(700L,boundaryTicket);
        Call("CymantiReconcileEatBoundary",deferredPass,eatAfter);
        Check(((IList)Pass.GetField("ProofTickets",F).GetValue(deferredPass)).Contains(boundaryTicket),"postfix attaches deferred Eat only after boundary proof");
        Call("CymantiRecord",deferredPass,eatAfter,boundaryTicket);
        Check(boundaryTicket.Cymanti.EatExecutions==1 && boundaryTicket.Cymanti.GeneratedAssociation[grownTail.Id]=="executed-EatAction-own-chain-after-confirmed-primary-kill",
            "reconciled generation reports its actual boundary association without inventing a child reference");
        Check(boundaryTicket.Cymanti.EatObservations.Count==1 && !boundaryTicket.Cymanti.EatActorIds.Contains("0")
            && (bool)new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(boundaryTicket.Cymanti.EatObservations[0])["nativeEaterFieldIsZero"],
            "native zero Eater is diagnosed without inventing a valid owned unit ID");
        tickets.Clear();
        Console.WriteLine("CymantiProofTests: "+passed+" passed");
    }
}
public sealed class CymantiActionFixture
{
    public int PlayerId{get;set;}
    public uint Eater{get;set;}
    public string Coordinates{get;set;}
    public string Origin{get;set;}
    public string Target{get;set;}
    public string Type{get;set;}
    public bool trainActionAborted{get;set;}
}
