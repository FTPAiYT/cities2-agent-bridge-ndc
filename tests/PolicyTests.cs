using System;
using CitiesIIAgentBridge;

internal static class PolicyTests
{
    internal static int Run()
    {
        int count=0;
        void Check(bool ok,string label){if(!ok)throw new Exception("FAIL: "+label);++count;Console.WriteLine("PASS: "+label);}
        var s=new SimulationWindow(100,50,30,5);
        Check(s.Check(100,0,true,true,false,null)==null,"step starts without blocking caller");
        Check(s.Check(110,2,true,true,false,null)==null,"advancing simulation continues");
        Check(s.Check(110,7,true,true,false,null)=="simulation_stalled","stalled simulation returns at finite deadline");
        Check(new SimulationWindow(100,50,30,5).Check(150,2,true,true,false,null)=="frame_limit","frame bound stops step");
        Check(new SimulationWindow(100,50,30,5).Check(110,30,true,true,false,null)=="wall_deadline","slow advancing simulation obeys wall deadline");
        Check(new SimulationWindow(100,50,30,5).Check(100,1,true,true,true,null)=="game_paused","manual pause never waits for unreachable event");
        Check(new SimulationWindow(100,50,30,5).Check(100,1,false,true,false,null)=="control_stopped","revoked control interrupts immediately");
        Check(new SimulationWindow(100,50,30,5).Check(1,1,true,true,false,null)=="city_changed","frame reset interrupts old-city step");
        Check(new SimulationWindow(100,50,30,5).Check(101,1,true,false,false,null)=="city_changed","city session change interrupts step");
        Check(new SimulationWindow(100,50,30,5).Check(101,1,true,true,false,"new_utility_shortage")=="new_utility_shortage","events return before deadlines");
        foreach(var invalid in new[]{new double[]{0,30,5},new double[]{262145,30,5},new double[]{10,61,5},new double[]{10,5,6},new double[]{10,0,0}})
        {bool rejected=false;try{new SimulationWindow(0,(ulong)invalid[0],invalid[1],invalid[2]);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid simulation bounds rejected");}
        var lot=new[]{new[]{0d,0d},new[]{10d,0d},new[]{10d,10d},new[]{0d,10d}};
        Check(PlanGeometry.Overlap(lot,lot),"identical occupied lots collide");
        Check(PlanGeometry.Overlap(lot,PlanGeometry.Corridor(-5,5,15,5,2)),"road crossing school footprint is rejected");
        Check(!PlanGeometry.Overlap(lot,PlanGeometry.Corridor(-5,13,15,13,2)),"road beside school remains available");
        Check(!PlanGeometry.Overlap(lot,new[]{new[]{10d,0d},new[]{20d,0d},new[]{20d,10d},new[]{10d,10d}}),"touching lot boundaries do not collide");
        var diamond=new[]{new[]{5d,-2d},new[]{12d,5d},new[]{5d,12d},new[]{-2d,5d}};
        Check(PlanGeometry.Overlap(lot,diamond),"rotated building overlap detected");
        Check(PlanGeometry.Overlap(diamond,lot)==PlanGeometry.Overlap(lot,diamond),"overlap detection is symmetric");
        bool zero=false;try{PlanGeometry.Corridor(1,1,1,1,8);}catch(ArgumentException){zero=true;}Check(zero,"degenerate road rejected");
        return count;
    }
}
