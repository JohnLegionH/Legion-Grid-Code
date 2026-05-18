// Real Destruction Test Script
// This script sets up objects for actual destruction testing

default
{
    state_entry()
    {
        llSay(0, "=== REAL DESTRUCTION TEST ===");
        llSay(0, "Touch me to configure objects for destruction");
        llSetText("DESTRUCTION TEST\nTouch to configure", <1,0,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "Configuring objects for destruction...");
        
        // Make this object destructible with low threshold
        llSetDestructible(TRUE, 5.0, FRACTURE_RANDOM);
        llSetPhysicsMaterial(MATERIAL_GLASS, 2.5, 0.1, 0.9);
        llSay(0, "✓ Controller set as fragile glass (threshold: 5.0)");
        
        llSleep(1.0);
        
        llSay(0, "=== DESTRUCTION TEST READY ===");
        llSay(0, "Objects configured:");
        llSay(0, "- Controller: Glass, threshold 5.0");
        llSay(0, "- GlassTarget: Should break at low force");
        llSay(0, "- TestHammer: Use this to break things");
        llSay(0, "");
        llSay(0, "TEST INSTRUCTIONS:");
        llSay(0, "1. Push/throw the TestHammer at objects");
        llSay(0, "2. Watch for destruction when force > threshold");
        llSay(0, "3. Check chat for destruction messages");
        llSay(0, "4. Look for visual fragments");
        
        llSetText("READY TO BREAK!\nHit objects with\nTestHammer", <1,0,0>, 1.0);
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "*** CONTROLLER HIT! ***");
        float impulse = llGetCollisionImpulse();
        llSay(0, "Impact force: " + (string)impulse);
        llSay(0, "Threshold: 5.0 - Should break if force > 5.0!");
        
        if (impulse > 5.0)
        {
            llSay(0, "BREAKING! Force exceeded threshold!");
            llSetText("BREAKING!\nForce: " + (string)impulse, <1,1,0>, 1.0);
        }
        else
        {
            llSay(0, "Not enough force to break (need > 5.0)");
            llSetText("HIT BUT OK\nForce: " + (string)impulse + "\nNeed > 5.0", <0,1,0>, 1.0);
        }
    }
}