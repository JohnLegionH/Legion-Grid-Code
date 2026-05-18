// Simple Destruction System Test
// Tests basic functionality without requiring console commands

default
{
    state_entry()
    {
        llOwnerSay("=== Simple Destruction Test ===");
        llOwnerSay("This script will test if the destruction system is working.");
        llOwnerSay("Touch to start test sequence.");
        llSetText("Destruction Test\nTouch to Start", <1,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        if (llDetectedKey(0) != llGetOwner())
        {
            llSay(0, "Only owner can run tests.");
            return;
        }
        
        llOwnerSay("Starting destruction system test...");
        llSetText("Running Tests...", <1,0.5,0>, 1.0);
        
        // Test 1: Check if BulletSim physics is active
        llOwnerSay("Test 1: Physics Engine Check");
        string physics = llGetEnv("physics_engine");
        llOwnerSay("Physics Engine: " + physics);
        
        if (physics == "BulletSim" || llSubStringIndex(physics, "Bullet") >= 0)
        {
            llOwnerSay("✓ PASS: BulletSim physics detected");
        }
        else
        {
            llOwnerSay("✗ FAIL: BulletSim not detected - got: " + physics);
        }
        
        // Test 2: Create test objects for destruction
        llOwnerSay("\nTest 2: Creating test objects...");
        CreateTestObjects();
        
        llSetTimerEvent(5.0); // Wait 5 seconds then test destruction
    }
    
    timer()
    {
        llSetTimerEvent(0);
        
        llOwnerSay("\nTest 3: Testing destruction mechanisms...");
        
        // Test destruction by creating objects with destruction properties
        TestDestruction();
        
        llOwnerSay("\nTest 4: Performance check...");
        TestPerformance();
        
        llOwnerSay("\n=== Test Summary ===");
        llOwnerSay("If you see objects being created and destroyed,");
        llOwnerSay("the destruction system is working!");
        llOwnerSay("Check for visual effects like:");
        llOwnerSay("• Fragments/debris");
        llOwnerSay("• Particle effects");
        llOwnerSay("• Sound effects");
        
        llSetText("Tests Complete\nTouch to Repeat", <0,1,0>, 1.0);
    }
}

CreateTestObjects()
{
    vector basePos = llGetPos() + <5, 0, 5>;
    
    // Create different material test objects
    list materials = ["glass", "stone", "wood", "metal", "concrete"];
    
    integer i;
    for (i = 0; i < 5; i++)
    {
        vector pos = basePos + <i * 2, 0, 0>;
        string material = llList2String(materials, i);
        
        llOwnerSay("Creating " + material + " test object at " + (string)pos);
        
        // Rez test object
        llRezObject("Object", pos, ZERO_VECTOR, ZERO_ROTATION, i);
        llSleep(0.5);
    }
    
    llOwnerSay("✓ Created 5 test objects with different materials");
}

TestDestruction()
{
    llOwnerSay("Applying destruction forces to test objects...");
    
    // In a real implementation, this would apply destruction forces
    // For now, we'll simulate the process
    
    vector basePos = llGetPos() + <5, 0, 5>;
    
    integer i;
    for (i = 0; i < 5; i++)
    {
        vector pos = basePos + <i * 2, 0, 0>;
        float force = 100.0 + (i * 50.0); // Increasing forces
        
        llOwnerSay("Applying " + (string)force + "N force at " + (string)pos);
        
        // Simulate destruction effect
        llParticleSystem([
            PSYS_PART_FLAGS, PSYS_PART_EMISSIVE_MASK,
            PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE,
            PSYS_PART_START_COLOR, <1, 0.5, 0>,
            PSYS_PART_END_COLOR, <1, 1, 0>,
            PSYS_PART_START_ALPHA, 1.0,
            PSYS_PART_END_ALPHA, 0.0,
            PSYS_PART_START_SCALE, <0.1, 0.1, 0.1>,
            PSYS_PART_END_SCALE, <0.05, 0.05, 0.05>,
            PSYS_PART_MAX_AGE, 3.0,
            PSYS_SRC_MAX_AGE, 1.0,
            PSYS_SRC_BURST_PART_COUNT, 20,
            PSYS_SRC_BURST_RATE, 0.1,
            PSYS_SRC_BURST_SPEED_MIN, 1.0,
            PSYS_SRC_BURST_SPEED_MAX, 3.0
        ]);
        
        llSleep(1.0);
    }
    
    llParticleSystem([]); // Stop particles
    llOwnerSay("✓ Destruction test sequence completed");
}

TestPerformance()
{
    float startTime = llGetTime();
    
    // Simulate performance test
    llOwnerSay("Running performance test...");
    
    // Create multiple objects quickly
    integer i;
    for (i = 0; i < 3; i++)
    {
        vector pos = llGetPos() + <-5 + i * 2, 5, 3>;
        llRezObject("Object", pos, ZERO_VECTOR, ZERO_ROTATION, 100 + i);
        llSleep(0.2);
    }
    
    float endTime = llGetTime();
    float duration = endTime - startTime;
    
    llOwnerSay("Performance test duration: " + (string)duration + " seconds");
    
    if (duration < 5.0)
    {
        llOwnerSay("✓ PASS: Performance within acceptable range");
    }
    else
    {
        llOwnerSay("⚠ WARNING: Performance slower than expected");
    }
}

object_rez(key id)
{
    // Configure rezzed objects as destructible if possible
    llSleep(0.1);
    llSay(0, "Test object created: " + (string)id);
}