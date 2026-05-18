// Test Script for Advanced Physics LSL Functions
// This script tests the newly implemented advanced physics LSL functions

default
{
    state_entry()
    {
        llSay(0, "=== TESTING ADVANCED PHYSICS LSL FUNCTIONS ===");
        llSay(0, "This script tests the new LSL physics extensions");
        llSay(0, "Touch me to run the tests");
        
        llSetText("ADVANCED PHYSICS\nTEST SCRIPT\nTouch to test", <1,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "Starting advanced physics tests...");
        
        // Test 1: Set Prim Flags (CCD)
        llSay(0, "Test 1: Testing llSetPrimFlags for CCD");
        llSetPrimFlags(PF_USE_CCD, TRUE);
        llSay(0, "✓ CCD enabled using llSetPrimFlags()");
        
        llSleep(1.0);
        
        // Test 2: Set Destructible
        llSay(0, "Test 2: Testing llSetDestructible");
        llSetDestructible(TRUE, 10.0, FRACTURE_RANDOM);
        llSay(0, "✓ Object marked as destructible with threshold 10.0");
        
        llSleep(1.0);
        
        // Test 3: Set Physics Material
        llSay(0, "Test 3: Testing llSetPhysicsMaterial");
        llSetPhysicsMaterial(MATERIAL_GLASS, 2.5, 0.3, 0.8);
        llSay(0, "✓ Glass material properties applied");
        
        llSleep(1.0);
        
        // Test 4: Get Collision Impulse
        llSay(0, "Test 4: Testing llGetCollisionImpulse");
        float impulse = llGetCollisionImpulse();
        llSay(0, "✓ Collision impulse: " + (string)impulse);
        
        llSleep(1.0);
        
        // Test 5: Enable Physics Logging
        llSay(0, "Test 5: Testing llEnablePhysicsLogging");
        llEnablePhysicsLogging(TRUE);
        llSay(0, "✓ Physics logging enabled");
        
        llSleep(1.0);
        
        // Test 6: Test constants
        llSay(0, "Test 6: Testing physics constants");
        llSay(0, "PF_USE_CCD = " + (string)PF_USE_CCD);
        llSay(0, "PF_DESTRUCTIBLE = " + (string)PF_DESTRUCTIBLE);
        llSay(0, "PHYSICS_SHAPE_MESH = " + (string)PHYSICS_SHAPE_MESH);
        llSay(0, "MATERIAL_GLASS = " + (string)MATERIAL_GLASS);
        llSay(0, "FRACTURE_RANDOM = " + (string)FRACTURE_RANDOM);
        
        llSleep(2.0);
        
        llSay(0, "=== ALL TESTS COMPLETED ===");
        llSay(0, "Check object description for stored physics data");
        
        // Show the stored physics data
        string desc = llGetObjectDesc();
        llSay(0, "Object Description: " + desc);
        
        llSetText("TESTS COMPLETE\nCheck chat for results", <0,1,0>, 1.0);
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "COLLISION DETECTED!");
        llSay(0, "Testing collision impulse function...");
        float impulse = llGetCollisionImpulse();
        llSay(0, "Collision impulse: " + (string)impulse);
        
        llSetText("COLLISION!\nImpulse: " + (string)impulse, <1,0,0>, 1.0);
        llSetTimerEvent(3.0); // Reset text after 3 seconds
    }
    
    timer()
    {
        llSetTimerEvent(0.0);
        llSetText("ADVANCED PHYSICS\nTEST SCRIPT\nTouch to test", <1,1,0>, 1.0);
    }
}