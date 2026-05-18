// Comprehensive Advanced Physics Test Script
// This script demonstrates all the new LSL advanced physics functions
// Place this script in the AdvancedPhysicsTest object

integer testMode = 0;
integer maxTests = 8;

default
{
    state_entry()
    {
        llSay(0, "=== COMPREHENSIVE ADVANCED PHYSICS TEST ===");
        llSay(0, "This script tests ALL new LSL physics functions");
        llSay(0, "Touch me repeatedly to cycle through different tests");
        llSay(0, "Tests available: " + (string)maxTests);
        
        llSetText("ADVANCED PHYSICS\nTEST SUITE\n\nTouch to cycle tests\nTest " + (string)(testMode + 1) + "/" + (string)maxTests, <1,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        testMode++;
        if (testMode >= maxTests) testMode = 0;
        
        llSay(0, "\n=== RUNNING TEST " + (string)(testMode + 1) + "/" + (string)maxTests + " ===");
        
        if (testMode == 0)
        {
            testPrimFlags();
        }
        else if (testMode == 1)
        {
            testDestructible();
        }
        else if (testMode == 2)
        {
            testPhysicsMaterials();
        }
        else if (testMode == 3)
        {
            testCollisionImpulse();
        }
        else if (testMode == 4)
        {
            testPhysicsLogging();
        }
        else if (testMode == 5)
        {
            testPhysicsConstants();
        }
        else if (testMode == 6)
        {
            testCombinedFeatures();
        }
        else if (testMode == 7)
        {
            resetAllTests();
        }
        
        llSetText("ADVANCED PHYSICS\nTEST SUITE\n\nRunning Test " + (string)(testMode + 1) + "/" + (string)maxTests, <0,1,1>, 1.0);
    }
    
    testPrimFlags()
    {
        llSay(0, "TEST 1: Testing llSetPrimFlags()");
        llSay(0, "Setting various physics flags...");
        
        // Test CCD flag
        llSetPrimFlags(PF_USE_CCD, TRUE);
        llSay(0, "✓ CCD enabled with PF_USE_CCD");
        
        // Test destructible flag
        llSetPrimFlags(PF_DESTRUCTIBLE, TRUE);
        llSay(0, "✓ Destructible flag set with PF_DESTRUCTIBLE");
        
        // Test physics shape type
        llSetPrimFlags(PF_PHYSICS_SHAPE_TYPE, PHYSICS_SHAPE_MESH);
        llSay(0, "✓ Physics shape set to MESH type");
        
        llSay(0, "Object description after flags: " + llGetObjectDesc());
    }
    
    testDestructible()
    {
        llSay(0, "TEST 2: Testing llSetDestructible()");
        llSay(0, "Configuring destruction parameters...");
        
        // Test different destruction settings
        llSetDestructible(TRUE, 15.0, FRACTURE_RANDOM);
        llSay(0, "✓ Random fracture pattern, threshold 15.0");
        
        llSleep(1.0);
        
        llSetDestructible(TRUE, 25.0, FRACTURE_RADIAL);
        llSay(0, "✓ Radial fracture pattern, threshold 25.0");
        
        llSleep(1.0);
        
        llSetDestructible(TRUE, 10.0, FRACTURE_GRID);
        llSay(0, "✓ Grid fracture pattern, threshold 10.0");
        
        llSay(0, "Object is now destructible! Impact with force > 10.0 should break it");
    }
    
    testPhysicsMaterials()
    {
        llSay(0, "TEST 3: Testing llSetPhysicsMaterial()");
        llSay(0, "Applying different material properties...");
        
        // Test glass material
        llSetPhysicsMaterial(MATERIAL_GLASS, 2.5, 0.1, 0.9);
        llSay(0, "✓ Glass: density=2.5, friction=0.1, restitution=0.9");
        
        llSleep(1.0);
        
        // Test metal material
        llSetPhysicsMaterial(MATERIAL_METAL, 7.8, 0.8, 0.3);
        llSay(0, "✓ Metal: density=7.8, friction=0.8, restitution=0.3");
        
        llSleep(1.0);
        
        // Test wood material
        llSetPhysicsMaterial(MATERIAL_WOOD, 0.6, 0.6, 0.5);
        llSay(0, "✓ Wood: density=0.6, friction=0.6, restitution=0.5");
        
        llSay(0, "Material properties applied - check object behavior!");
    }
    
    testCollisionImpulse()
    {
        llSay(0, "TEST 4: Testing llGetCollisionImpulse()");
        llSay(0, "Current collision impulse: " + (string)llGetCollisionImpulse());
        llSay(0, "Collide with this object to see impulse measurement!");
        llSay(0, "Note: Impulse will be 0 until a collision occurs");
        
        // Enable collision detection
        llVolumeDetect(FALSE);
        llSetStatus(STATUS_PHANTOM, FALSE);
    }
    
    testPhysicsLogging()
    {
        llSay(0, "TEST 5: Testing llEnablePhysicsLogging()");
        
        llEnablePhysicsLogging(TRUE);
        llSay(0, "✓ Physics logging ENABLED");
        llSay(0, "Physics debug information should now be logged");
        
        llSleep(2.0);
        
        llEnablePhysicsLogging(FALSE);
        llSay(0, "✓ Physics logging DISABLED");
        llSay(0, "Check object description for logging flags");
    }
    
    testPhysicsConstants()
    {
        llSay(0, "TEST 6: Testing Physics Constants");
        llSay(0, "Displaying all physics constant values:");
        
        llSay(0, "--- Prim Flags ---");
        llSay(0, "PF_USE_CCD = " + (string)PF_USE_CCD);
        llSay(0, "PF_DESTRUCTIBLE = " + (string)PF_DESTRUCTIBLE);
        llSay(0, "PF_PHYSICS_SHAPE_TYPE = " + (string)PF_PHYSICS_SHAPE_TYPE);
        
        llSay(0, "--- Physics Shapes ---");
        llSay(0, "PHYSICS_SHAPE_PRIM = " + (string)PHYSICS_SHAPE_PRIM);
        llSay(0, "PHYSICS_SHAPE_CONVEX = " + (string)PHYSICS_SHAPE_CONVEX);
        llSay(0, "PHYSICS_SHAPE_MESH = " + (string)PHYSICS_SHAPE_MESH);
        llSay(0, "PHYSICS_SHAPE_NONE = " + (string)PHYSICS_SHAPE_NONE);
        
        llSay(0, "--- Materials ---");
        llSay(0, "MATERIAL_GLASS = " + (string)MATERIAL_GLASS);
        llSay(0, "MATERIAL_METAL = " + (string)MATERIAL_METAL);
        llSay(0, "MATERIAL_WOOD = " + (string)MATERIAL_WOOD);
        llSay(0, "MATERIAL_STONE = " + (string)MATERIAL_STONE);
        llSay(0, "MATERIAL_CONCRETE = " + (string)MATERIAL_CONCRETE);
        
        llSay(0, "--- Fracture Patterns ---");
        llSay(0, "FRACTURE_RANDOM = " + (string)FRACTURE_RANDOM);
        llSay(0, "FRACTURE_RADIAL = " + (string)FRACTURE_RADIAL);
        llSay(0, "FRACTURE_GRID = " + (string)FRACTURE_GRID);
    }
    
    testCombinedFeatures()
    {
        llSay(0, "TEST 7: Testing Combined Advanced Features");
        llSay(0, "Applying multiple advanced physics features together...");
        
        // Apply CCD
        llSetPrimFlags(PF_USE_CCD, TRUE);
        llSay(0, "✓ CCD enabled for fast collisions");
        
        // Make destructible with glass properties
        llSetDestructible(TRUE, 8.0, FRACTURE_RADIAL);
        llSay(0, "✓ Destructible glass (threshold: 8.0, radial fracture)");
        
        // Apply glass material
        llSetPhysicsMaterial(MATERIAL_GLASS, 2.5, 0.1, 0.9);
        llSay(0, "✓ Glass material properties applied");
        
        // Enable physics logging
        llEnablePhysicsLogging(TRUE);
        llSay(0, "✓ Physics logging enabled");
        
        llSay(0, "=== COMBINED FEATURES ACTIVE ===");
        llSay(0, "This object now has: CCD, Destruction, Glass Material, Logging");
        llSay(0, "Try high-speed collisions to test everything together!");
    }
    
    resetAllTests()
    {
        llSay(0, "TEST 8: Resetting All Tests");
        llSay(0, "Clearing all advanced physics settings...");
        
        // Disable destructible
        llSetDestructible(FALSE, 0.0, 0);
        llSay(0, "✓ Destructible disabled");
        
        // Disable CCD
        llSetPrimFlags(PF_USE_CCD, FALSE);
        llSay(0, "✓ CCD disabled");
        
        // Disable physics logging
        llEnablePhysicsLogging(FALSE);
        llSay(0, "✓ Physics logging disabled");
        
        // Reset to basic material
        llSetPhysicsMaterial(MATERIAL_WOOD, 1.0, 0.5, 0.5);
        llSay(0, "✓ Reset to basic wood material");
        
        llSay(0, "=== ALL TESTS RESET ===");
        llSay(0, "Object returned to default physics state");
        llSay(0, "Touch again to restart test cycle");
        
        testMode = -1; // Will be 0 on next touch
    }
    
    collision_start(integer num_detected)
    {
        llSay(0, "\n*** COLLISION DETECTED ***");
        float impulse = llGetCollisionImpulse();
        llSay(0, "Collision impulse measured: " + (string)impulse);
        
        integer i;
        for (i = 0; i < num_detected; i++)
        {
            string name = llDetectedName(i);
            vector pos = llDetectedPos(i);
            llSay(0, "Collided with: " + name + " at " + (string)pos);
        }
        
        llSetText("COLLISION!\nImpulse: " + (string)impulse + "\n" + (string)num_detected + " objects", <1,0,0>, 1.0);
        llSetTimerEvent(5.0); // Reset text after 5 seconds
    }
    
    timer()
    {
        llSetTimerEvent(0.0);
        llSetText("ADVANCED PHYSICS\nTEST SUITE\n\nTouch to cycle tests\nTest " + (string)(testMode + 1) + "/" + (string)maxTests, <1,1,0>, 1.0);
    }
}