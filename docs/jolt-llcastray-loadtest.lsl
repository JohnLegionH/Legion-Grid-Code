// jolt-llcastray-loadtest.lsl
// =====================================================================================
// PURPOSE: hammer the concurrent-query-vs-Step path that _simLock now guards.
//
// llCastRay runs on a SCRIPT thread and (with SupportsRaycastWorldFiltered => true) issues a native
// Jolt NarrowPhaseQuery. The heartbeat thread runs _system.Update every frame. Before the _simLock fix,
// those two overlapping inside Jolt's non-thread-safe LIFO TempAllocator aborted the process
// ("TempAllocator: Freeing in the wrong order"). This script fires bursts of llCastRay continuously so
// that many script-thread queries land WHILE the step thread is inside Update. Rez several copies to get
// several script threads contending with the step at once.
//
// HOW TO RUN:
//   1. Rez a prim on Elm (or Ebony), drop this script in, take a copy, rez ~5-10 copies around the region.
//   2. While they run, ALSO make the physics engine busy so Update has real work overlapping the queries:
//        - walk one or two avatars around,
//        - drive a vehicle (boat/car), and/or
//        - drop a few physical prims so bodies are active.
//   3. Let it run several minutes. Watch OpenSim.log.
//   4. Touch any copy to stop it and print its final tally.
//
// TUNING: raise CASTS_PER_TICK / lower TIMER_INTERVAL for more pressure. More rezzed copies = more
// concurrent script threads = the real stress on _simLock.
// =====================================================================================

integer CASTS_PER_TICK = 25;    // llCastRay calls per timer tick
float   TIMER_INTERVAL = 0.05;  // ~20 ticks/sec  => ~500 casts/sec per copy

integer total;   // casts issued
integer hits;    // total hit detections returned
integer errs;    // negative status (RCERR_*) - see note below
integer thr;     // RCERR_SIM_PERF_LOW (-2) = the region's llCastRay throttle (EXPECTED under load)
integer ticks;

cast_once()
{
    vector p = llGetPos();
    // Random direction, 60 m reach, so casts hit terrain / prims / avatars in different bodies each time
    // (exercises the heightfield AND compound/prim broadphase, not just one shape).
    vector dir = <llFrand(2.0) - 1.0, llFrand(2.0) - 1.0, llFrand(2.0) - 1.0>;
    if (dir == ZERO_VECTOR) dir = <0.0, 0.0, -1.0>;
    vector end = p + llVecNorm(dir) * 60.0;

    list res = llCastRay(p, end, [RC_MAX_HITS, 4, RC_DETECT_PHANTOM, FALSE]);
    integer status = llList2Integer(res, -1);   // >=0 : number of hits ; <0 : RCERR_*
    ++total;
    if (status >= 0)       hits += status;
    else if (status == -2) ++thr;               // RCERR_SIM_PERF_LOW = throttle, not a fault
    else                   ++errs;              // any other negative = real error worth noting
}

report()
{
    llOwnerSay("[castray] casts=" + (string)total
             + " hits=" + (string)hits
             + " throttled=" + (string)thr
             + " errs=" + (string)errs);
}

default
{
    state_entry()
    {
        total = 0; hits = 0; errs = 0; thr = 0; ticks = 0;
        llOwnerSay("[castray] ARMED: " + (string)CASTS_PER_TICK + " casts/tick @ "
                 + (string)TIMER_INTERVAL + "s  (touch to stop)");
        llSetTimerEvent(TIMER_INTERVAL);
    }

    timer()
    {
        integer i;
        for (i = 0; i < CASTS_PER_TICK; ++i) cast_once();
        ++ticks;
        if (ticks % 40 == 0) report();   // ~every 2 s
    }

    touch_start(integer n)
    {
        llSetTimerEvent(0.0);
        llOwnerSay("[castray] STOPPED.");
        report();
    }
}
