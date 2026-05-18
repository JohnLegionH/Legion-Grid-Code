// Complete Collision Test System
// Put this script in a single object and touch it to run collision tests

integer testMode = 0;
float testTimer = 0.0;

default
{
    state_entry()
    {
        llSay(0, "=== COLLISION TEST SYSTEM ===");
        llSay(0, "Touch me to run different tests:");
        llSay(0, "1st touch: Slow collision test");
        llSay(0, "2nd touch: Medium speed test");
        llSay(0, "3rd touch: High speed test");
        llSay(0, "4th touch: Rapid fire test");
        testMode = 0;
        llSetText("COLLISION TEST\nTouch to start", <1,1,1>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        testMode++;
        if (testMode > 4) testMode = 1;
        
        llSay(0, "=== TEST " + (string)testMode + " STARTING ===");
        
        if (testMode == 1)
        {
            llSay(0, "Slow collision test (5 m/s)");
            runCollisionTest(5.0, "SLOW");
        }
        else if (testMode == 2)
        {
            llSay(0, "Medium speed test (15 m/s)");
            runCollisionTest(15.0, "MEDIUM");
        }
        else if (testMode == 3)
        {
            llSay(0, "High speed test (30 m/s)");
            runCollisionTest(30.0, "HIGH");
        }
        else if (testMode == 4)
        {
            llSay(0, "Rapid fire test");
            runRapidFireTest();
        }
    }
    
    runCollisionTest(float speed, string speedName)
    {
        // Create target
        vector targetPos = llGetPos() + <10, 0, 0>;
        llRezObject("TestTarget", targetPos, ZERO_VECTOR, ZERO_ROTATION, 100 + testMode);
        
        // Create projectile after short delay
        llSetTimerEvent(1.0);
        testTimer = speed; // Store speed in timer for later use
    }
    
    runRapidFireTest()
    {
        // Create wall target
        vector wallPos = llGetPos() + <12, 0, 0>;
        llRezObject("TestWall", wallPos, ZERO_VECTOR, ZERO_ROTATION, 200);
        
        // Start rapid fire sequence
        testTimer = 0.0; // Use as shot counter
        llSetTimerEvent(0.5); // Fire every 0.5 seconds
    }
    
    timer()
    {
        if (testMode <= 3) // Single shot tests
        {
            // Create projectile with stored speed
            vector projPos = llGetPos() + <0, 0, 1>;
            vector velocity = <testTimer, 0, 0>; // testTimer contains speed
            llRezObject("TestProjectile", projPos, velocity, ZERO_ROTATION, testMode);
            llSetTimerEvent(0.0); // Stop timer
            testTimer = 0.0;
        }
        else if (testMode == 4) // Rapid fire test
        {
            testTimer += 1.0; // Shot counter
            if (testTimer > 5) // Fire 5 shots
            {
                llSetTimerEvent(0.0);
                llSay(0, "Rapid fire complete - 5 shots fired");
                return;
            }
            
            // Fire projectile
            vector projPos = llGetPos() + <0, 0, 1>;
            vector velocity = <25, llFrand(4)-2, llFrand(4)-2>; // 25 m/s with spread
            llRezObject("TestBullet", projPos, velocity, ZERO_ROTATION, (integer)(200 + testTimer));
            llSay(0, "Shot " + (string)((integer)testTimer) + " fired!");
        }
    }
    
    object_rez(key id)
    {
        integer param = llGetStartParameter();
        
        if (param >= 101 && param <= 104) // Targets for tests 1-4
        {
            integer test = param - 100;
            llSay(0, "Target created for test " + (string)test);
            llSetText("TARGET\n(Test " + (string)test + ")", <1,0,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<1,0,0>, ALL_SIDES);
            
            if (test <= 3)
                llSetScale(<2, 2, 2>); // Medium target for single shots
            else
                llSetScale(<1, 4, 3>); // Wall for rapid fire
                
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
        }
        else if (param == 200) // Wall target
        {
            llSay(0, "Wall target created for rapid fire test");
            llSetText("WALL TARGET", <1,0,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.8,0.2,0.2>, ALL_SIDES);
            llSetScale(<1, 6, 4>); // Large wall
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
        }
        else if (param >= 1 && param <= 3) // Single projectiles
        {
            llSay(0, "Projectile created for test " + (string)param);
            llSetText("PROJECTILE\n(Test " + (string)param + ")", <0,1,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0,1,0>, ALL_SIDES);
            llSetScale(<0.2, 0.2, 0.2>); // Small projectile
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
            
            // Auto cleanup after 8 seconds
            llSetTimerEvent(8.0);
        }
        else if (param >= 201 && param <= 205) // Rapid fire bullets
        {
            integer shotNum = param - 200;
            llSay(0, "Bullet " + (string)shotNum + " created");
            llSetText("BULLET " + (string)shotNum, <0,1,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0,1,0>, ALL_SIDES);
            llSetScale(<0.1, 0.1, 0.1>); // Very small bullets
            llVolumeDetect(FALSE);
            llCollisionSound("", 1.0);
            
            // Auto cleanup after 6 seconds
            llSetTimerEvent(6.0);
        }
    }
    
    collision_start(integer num_detected)
    {
        string myName = llGetObjectName();
        llSay(0, "*** COLLISION SUCCESS! ***");
        llSay(0, myName + " hit " + (string)num_detected + " objects!");
        
        integer i;
        for (i = 0; i < num_detected; i++)
        {
            string hitName = llDetectedName(i);
            vector hitPos = llDetectedPos(i);
            llSay(0, "HIT: " + hitName + " at " + (string)hitPos);
        }
        
        llSetText("COLLISION!", <1,1,0>, 1.0);
        
        // If this is a projectile, clean up after showing result
        if (llSubStringIndex(llGetObjectName(), "TestProjectile") >= 0 || 
            llSubStringIndex(llGetObjectName(), "TestBullet") >= 0)
        {
            llSleep(2.0);
            llDie();
        }
    }
}