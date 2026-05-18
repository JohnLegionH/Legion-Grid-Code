// Collision Detection Diagnostic Script
// This will help us understand what's happening with collision detection

default
{
    state_entry()
    {
        llSay(0, "Collision Diagnostic Test - Touch to start");
        llSay(0, "This will test basic collision detection without CCD first");
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "=== COLLISION DIAGNOSTIC TEST ===");
        
        // Test 1: Create a slow-moving object (should definitely hit)
        llSay(0, "Test 1: Creating slow target and projectile...");
        
        vector targetPos = llGetPos() + <5, 0, 0>;
        llRezObject("DiagTarget", targetPos, ZERO_VECTOR, ZERO_ROTATION, 1);
        
        llSleep(2.0);
        
        vector projectilePos = llGetPos() + <0, 0, 1>;
        vector slowVelocity = <2, 0, 0>; // Very slow - 2 m/s
        llRezObject("DiagProjectile", projectilePos, slowVelocity, ZERO_ROTATION, 2);
    }
    
    object_rez(key id)
    {
        integer param = llGetStartParameter();
        
        if (param == 1) // Target
        {
            llSay(0, "Target created - ID: " + (string)id);
            llSetText("TARGET\n(Should be hit)", <1,0,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<1,0,0>, ALL_SIDES);
            llSetScale(<2, 2, 2>); // Large target
            
            // Enable collision detection
            llVolumeDetect(FALSE); // Make sure it's solid
            llCollisionSound("", 1.0); // Subscribe to collisions
        }
        else if (param == 2) // Projectile
        {
            llSay(0, "Projectile created - ID: " + (string)id);
            llSetText("PROJECTILE\n(Slow: 2m/s)", <0,1,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0,1,0>, ALL_SIDES);
            llSetScale(<0.5, 0.5, 0.5>); // Medium projectile
            
            // Enable collision detection
            llVolumeDetect(FALSE); // Make sure it's solid
            llCollisionSound("", 1.0); // Subscribe to collisions
            
            // Report physics properties
            llSay(0, "Projectile Physics Status: " + (string)llGetStatus(STATUS_PHYSICS));
            llSay(0, "Projectile Material: " + (string)llGetPrimitive(PRIM_MATERIAL));
            
            // Set timer to report position
            llSetTimerEvent(1.0);
        }
    }
    
    timer()
    {
        // Report projectile position every second
        vector pos = llGetPos();
        vector vel = llGetVel();
        llSay(0, "Projectile Pos: " + (string)pos + " Vel: " + (string)vel);
        
        // Stop reporting after 10 seconds
        if (llGetTime() > 10.0)
        {
            llSetTimerEvent(0.0);
            llSay(0, "Projectile tracking stopped - may have missed target");
        }
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "*** COLLISION DETECTED! ***");
        llSay(0, "Number of objects detected: " + (string)num_detected);
        
        integer i;
        for (i = 0; i < num_detected; i++)
        {
            key detectedKey = llDetectedKey(i);
            string detectedName = llDetectedName(i);
            vector detectedPos = llDetectedPos(i);
            
            llSay(0, "Collision with: " + detectedName + " at " + (string)detectedPos);
        }
        
        llSetText("COLLISION SUCCESS!", <1,1,0>, 1.0);
        llSetTimerEvent(0.0); // Stop position reporting
    }
    
    collision_end(integer num_detected)
    {
        llSay(0, "Collision ended with " + (string)num_detected + " objects");
    }
}