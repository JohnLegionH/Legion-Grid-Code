// Working Collision Test - Using existing LSL functions
// Tests collision detection with current BulletSim setup

default
{
    state_entry()
    {
        llSay(0, "Working Collision Test - Touch to start");
        llSay(0, "This uses existing LSL functions to test collision detection");
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "=== COLLISION TEST START ===");
        
        // Create a target
        vector targetPos = llGetPos() + <8, 0, 0>;
        llRezObject("Target", targetPos, ZERO_VECTOR, ZERO_ROTATION, 1);
        
        llSleep(2.0);
        
        // Create a projectile with moderate speed
        vector projectilePos = llGetPos() + <0, 0, 1>;
        vector velocity = <15, 0, 0>; // 15 m/s - fast but not extreme
        llRezObject("Projectile", projectilePos, velocity, ZERO_ROTATION, 2);
    }
    
    object_rez(key id)
    {
        integer param = llGetStartParameter();
        
        if (param == 1) // Target
        {
            llSay(0, "Target created");
            llSetText("TARGET", <1,0,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<1,0,0>, ALL_SIDES);
            llSetScale(<3, 3, 3>); // Large target - easier to hit
            
            // Make sure it can detect collisions
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
        }
        else if (param == 2) // Projectile
        {
            llSay(0, "Projectile created with velocity");
            llSetText("PROJECTILE", <0,1,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0,1,0>, ALL_SIDES);
            llSetScale(<0.3, 0.3, 0.3>); // Small projectile
            
            // Enable collision detection
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
            
            // Set material properties for better physics
            llSetPrimitive(PRIM_MATERIAL, MATERIAL_METAL);
            llSetStatus(STATUS_PHANTOM, FALSE);
            
            // Auto-cleanup after 10 seconds
            llSetTimerEvent(10.0);
        }
    }
    
    timer()
    {
        llSay(0, "Cleaning up projectile (timed out)");
        llDie();
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "*** COLLISION DETECTED! ***");
        llSay(0, "Collision with " + (string)num_detected + " objects");
        
        integer i;
        for (i = 0; i < num_detected; i++)
        {
            string name = llDetectedName(i);
            vector pos = llDetectedPos(i);
            llSay(0, "Hit: " + name + " at " + (string)pos);
        }
        
        llSetText("HIT!", <1,1,0>, 1.0);
        llSetTimerEvent(0.0); // Stop timer
        
        // Clean up after showing result
        llSleep(3.0);
        llDie();
    }
}