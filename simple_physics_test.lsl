// Simple Advanced Physics Test Script
// Fixed version without syntax errors

integer testMode = 0;

default
{
    state_entry()
    {
        llSay(0, "=== SIMPLE ADVANCED PHYSICS TEST ===");
        llSay(0, "Touch me to test the new LSL physics functions");
        llSetText("PHYSICS TEST\nTouch to test", <1,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        testMode++;
        
        llSay(0, "\n=== RUNNING TEST " + (string)testMode + " ===");
        
        if (testMode == 1)
        {
            llSay(0, "TEST 1: Testing llSetPrimFlags()");
            llSetPrimFlags(PF_USE_CCD, TRUE);
            llSay(0, "✓ CCD enabled with PF_USE_CCD");
            llSetPrimFlags(PF_DESTRUCTIBLE, TRUE);
            llSay(0, "✓ Destructible flag set");
        }
        else if (testMode == 2)
        {
            llSay(0, "TEST 2: Testing llSetDestructible()");
            llSetDestructible(TRUE, 15.0, FRACTURE_RANDOM);
            llSay(0, "✓ Object set as destructible, threshold 15.0");
        }
        else if (testMode == 3)
        {
            llSay(0, "TEST 3: Testing llSetPhysicsMaterial()");
            llSetPhysicsMaterial(MATERIAL_GLASS, 2.5, 0.1, 0.9);
            llSay(0, "✓ Glass material applied");
        }
        else if (testMode == 4)
        {
            llSay(0, "TEST 4: Testing llGetCollisionImpulse()");
            float impulse = llGetCollisionImpulse();
            llSay(0, "✓ Collision impulse: " + (string)impulse);
        }
        else if (testMode == 5)
        {
            llSay(0, "TEST 5: Testing llEnablePhysicsLogging()");
            llEnablePhysicsLogging(TRUE);
            llSay(0, "✓ Physics logging enabled");
        }
        else if (testMode == 6)
        {
            llSay(0, "TEST 6: Testing Physics Constants");
            llSay(0, "PF_USE_CCD = " + (string)PF_USE_CCD);
            llSay(0, "MATERIAL_GLASS = " + (string)MATERIAL_GLASS);
            llSay(0, "FRACTURE_RANDOM = " + (string)FRACTURE_RANDOM);
        }
        else if (testMode == 7)
        {
            llSay(0, "TEST 7: Showing object description");
            string desc = llGetObjectDesc();
            llSay(0, "Object Description: " + desc);
        }
        else
        {
            llSay(0, "=== ALL TESTS COMPLETE ===");
            llSay(0, "All new LSL physics functions tested!");
            testMode = 0;
        }
        
        llSetText("PHYSICS TEST\nTest " + (string)testMode + " complete", <0,1,0>, 1.0);
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "*** COLLISION DETECTED ***");
        float impulse = llGetCollisionImpulse();
        llSay(0, "Collision impulse: " + (string)impulse);
    }
}