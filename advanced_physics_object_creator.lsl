// Advanced Physics Object Creator Script
// This script creates all the test objects needed for advanced physics testing
// Place this script in a prim and touch it to create test objects

integer creationMode = 0;
integer maxModes = 10;

default
{
    state_entry()
    {
        llSay(0, "=== ADVANCED PHYSICS OBJECT CREATOR ===");
        llSay(0, "This script creates test objects for advanced physics");
        llSay(0, "Touch repeatedly to create different objects");
        llSay(0, "Creation modes: " + (string)maxModes);
        
        llSetText("OBJECT CREATOR\n\nTouch to create objects\nMode " + (string)(creationMode + 1) + "/" + (string)maxModes, <1,0.5,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "\n=== CREATING OBJECTS - MODE " + (string)(creationMode + 1) + " ===");
        
        if (creationMode == 0)
        {
            createTestController();
        }
        else if (creationMode == 1)
        {
            createDestructibleTargets();
        }
        else if (creationMode == 2)
        {
            createCCDTestObjects();
        }
        else if (creationMode == 3)
        {
            createCollisionForceObjects();
        }
        else if (creationMode == 4)
        {
            createMaterialTestObjects();
        }
        else if (creationMode == 5)
        {
            createProjectiles();
        }
        else if (creationMode == 6)
        {
            createTestFloor();
        }
        else if (creationMode == 7)
        {
            createTargetWall();
        }
        else if (creationMode == 8)
        {
            createFallingObjects();
        }
        else if (creationMode == 9)
        {
            showCreatedObjects();
        }
        
        creationMode++;
        if (creationMode >= maxModes) creationMode = 0;
        
        llSetText("OBJECT CREATOR\n\nObjects created!\nMode " + (string)(creationMode + 1) + "/" + (string)maxModes, <0,1,0>, 1.0);
    }
    
    createTestController()
    {
        llSay(0, "Creating main test controller...");
        vector pos = llGetPos() + <5, 0, 0>;
        llRezObject("TestController", pos, ZERO_VECTOR, ZERO_ROTATION, 100);
    }
    
    createDestructibleTargets()
    {
        llSay(0, "Creating destructible target objects...");
        
        // Glass target
        vector glassPos = llGetPos() + <8, -3, 0>;
        llRezObject("GlassTarget", glassPos, ZERO_VECTOR, ZERO_ROTATION, 201);
        
        // Metal target  
        vector metalPos = llGetPos() + <8, 0, 0>;
        llRezObject("MetalTarget", metalPos, ZERO_VECTOR, ZERO_ROTATION, 202);
        
        // Wood target
        vector woodPos = llGetPos() + <8, 3, 0>;
        llRezObject("WoodTarget", woodPos, ZERO_VECTOR, ZERO_ROTATION, 203);
    }
    
    createCCDTestObjects()
    {
        llSay(0, "Creating CCD test objects...");
        
        // Fast projectile launcher
        vector launcherPos = llGetPos() + <-8, 0, 2>;
        llRezObject("FastLauncher", launcherPos, ZERO_VECTOR, ZERO_ROTATION, 301);
        
        // CCD target
        vector targetPos = llGetPos() + <12, 0, 0>;
        llRezObject("CCDTarget", targetPos, ZERO_VECTOR, ZERO_ROTATION, 302);
    }
    
    createCollisionForceObjects()
    {
        llSay(0, "Creating collision force test objects...");
        
        // Heavy object
        vector heavyPos = llGetPos() + <3, -5, 3>;
        llRezObject("HeavyObject", heavyPos, ZERO_VECTOR, ZERO_ROTATION, 401);
        
        // Light object
        vector lightPos = llGetPos() + <3, 5, 3>;
        llRezObject("LightObject", lightPos, ZERO_VECTOR, ZERO_ROTATION, 402);
    }
    
    createMaterialTestObjects()
    {
        llSay(0, "Creating material test objects...");
        
        // Stone object
        vector stonePos = llGetPos() + <0, -6, 0>;
        llRezObject("StoneObject", stonePos, ZERO_VECTOR, ZERO_ROTATION, 501);
        
        // Concrete object
        vector concretePos = llGetPos() + <0, 6, 0>;
        llRezObject("ConcreteObject", concretePos, ZERO_VECTOR, ZERO_ROTATION, 502);
    }
    
    createProjectiles()
    {
        llSay(0, "Creating test projectiles...");
        
        // Small fast projectile
        vector projPos1 = llGetPos() + <-5, -2, 1>;
        llRezObject("SmallProjectile", projPos1, <10, 0, 0>, ZERO_ROTATION, 601);
        
        // Medium projectile
        vector projPos2 = llGetPos() + <-5, 0, 1>;
        llRezObject("MediumProjectile", projPos2, <15, 0, 0>, ZERO_ROTATION, 602);
        
        // Large projectile
        vector projPos3 = llGetPos() + <-5, 2, 1>;
        llRezObject("LargeProjectile", projPos3, <8, 0, 0>, ZERO_ROTATION, 603);
    }
    
    createTestFloor()
    {
        llSay(0, "Creating test floor...");
        vector floorPos = llGetPos() + <0, 0, -3>;
        llRezObject("TestFloor", floorPos, ZERO_VECTOR, ZERO_ROTATION, 701);
    }
    
    createTargetWall()
    {
        llSay(0, "Creating target wall...");
        vector wallPos = llGetPos() + <15, 0, 0>;
        llRezObject("TargetWall", wallPos, ZERO_VECTOR, ZERO_ROTATION, 801);
    }
    
    createFallingObjects()
    {
        llSay(0, "Creating falling test objects...");
        
        // Ball 1
        vector ball1Pos = llGetPos() + <0, -3, 8>;
        llRezObject("FallBall1", ball1Pos, ZERO_VECTOR, ZERO_ROTATION, 901);
        
        // Ball 2
        vector ball2Pos = llGetPos() + <0, 3, 8>;
        llRezObject("FallBall2", ball2Pos, ZERO_VECTOR, ZERO_ROTATION, 902);
    }
    
    showCreatedObjects()
    {
        llSay(0, "=== OBJECT CREATION COMPLETE ===");
        llSay(0, "All test objects should now be created around this location");
        llSay(0, "Objects created:");
        llSay(0, "- Main test controller (add comprehensive_physics_test.lsl to it)");
        llSay(0, "- Destructible targets (Glass, Metal, Wood)");
        llSay(0, "- CCD test objects (Fast launcher, CCD target)");
        llSay(0, "- Collision force objects (Heavy, Light)");
        llSay(0, "- Material test objects (Stone, Concrete)");
        llSay(0, "- Test projectiles (Small, Medium, Large)");
        llSay(0, "- Test floor");
        llSay(0, "- Target wall");
        llSay(0, "- Falling objects");
        llSay(0, "");
        llSay(0, "NEXT STEPS:");
        llSay(0, "1. Add 'comprehensive_physics_test.lsl' script to TestController");
        llSay(0, "2. Touch TestController to run advanced physics tests");
        llSay(0, "3. Test collisions between different objects");
    }
    
    object_rez(key id)
    {
        integer param = llGetStartParameter();
        
        if (param == 100) // Test Controller
        {
            llSay(0, "✓ Main test controller created");
            llSetText("TEST CONTROLLER\nAdd comprehensive_physics_test.lsl\nThen touch to test", <0,1,0>, 1.0);
            llSetStatus(STATUS_PHYSICS, FALSE);
            llSetColor(<0.2, 0.8, 0.2>, ALL_SIDES);
            llSetScale(<1.5, 1.5, 1.5>);
        }
        else if (param >= 201 && param <= 203) // Destructible targets
        {
            string[] materials = ["Glass", "Metal", "Wood"];
            vector[] colors = [<0.8, 0.9, 1.0>, <0.7, 0.7, 0.8>, <0.6, 0.4, 0.2>];
            
            integer index = param - 201;
            string material = materials[index];
            vector color = colors[index];
            
            llSay(0, "✓ " + material + " target created");
            llSetText(material + "\nDESTRUCTIBLE\nTARGET", color, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(color, ALL_SIDES);
            llSetScale(<2, 2, 2>);
            llVolumeDetect(FALSE);
        }
        else if (param == 301) // Fast Launcher
        {
            llSay(0, "✓ Fast projectile launcher created");
            llSetText("FAST\nLAUNCHER\nTouch for CCD test", <1, 0.5, 0>, 1.0);
            llSetStatus(STATUS_PHYSICS, FALSE);
            llSetColor(<1, 0.5, 0>, ALL_SIDES);
            llSetScale(<1, 1, 1>);
        }
        else if (param == 302) // CCD Target
        {
            llSay(0, "✓ CCD target created");
            llSetText("CCD\nTARGET", <1, 0, 0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<1, 0, 0>, ALL_SIDES);
            llSetScale(<1, 3, 3>);
            llVolumeDetect(FALSE);
        }
        else if (param == 401) // Heavy Object
        {
            llSay(0, "✓ Heavy object created");
            llSetText("HEAVY\nOBJECT", <0.3, 0.3, 0.3>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.3, 0.3, 0.3>, ALL_SIDES);
            llSetScale(<1.5, 1.5, 1.5>);
            llVolumeDetect(FALSE);
        }
        else if (param == 402) // Light Object
        {
            llSay(0, "✓ Light object created");
            llSetText("LIGHT\nOBJECT", <0.9, 0.9, 0.9>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.9, 0.9, 0.9>, ALL_SIDES);
            llSetScale(<0.8, 0.8, 0.8>);
            llVolumeDetect(FALSE);
        }
        else if (param == 501) // Stone Object
        {
            llSay(0, "✓ Stone object created");
            llSetText("STONE\nMATERIAL", <0.5, 0.5, 0.4>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.5, 0.5, 0.4>, ALL_SIDES);
            llSetScale(<2, 2, 2>);
            llVolumeDetect(FALSE);
        }
        else if (param == 502) // Concrete Object
        {
            llSay(0, "✓ Concrete object created");
            llSetText("CONCRETE\nMATERIAL", <0.6, 0.6, 0.6>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.6, 0.6, 0.6>, ALL_SIDES);
            llSetScale(<2, 2, 2>);
            llVolumeDetect(FALSE);
        }
        else if (param >= 601 && param <= 603) // Projectiles
        {
            string[] sizes = ["Small", "Medium", "Large"];
            vector[] scales = [<0.2, 0.2, 0.2>, <0.5, 0.5, 0.5>, <0.8, 0.8, 0.8>];
            
            integer index = param - 601;
            string size = sizes[index];
            vector scale = scales[index];
            
            llSay(0, "✓ " + size + " projectile created");
            llSetText(size + "\nPROJECTILE", <0, 1, 0>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0, 1, 0>, ALL_SIDES);
            llSetScale(scale);
            llVolumeDetect(FALSE);
            
            // Auto cleanup after 10 seconds
            llSetTimerEvent(10.0);
        }
        else if (param == 701) // Test Floor
        {
            llSay(0, "✓ Test floor created");
            llSetText("TEST FLOOR", <0.5, 0.5, 0.5>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.5, 0.5, 0.5>, ALL_SIDES);
            llSetScale(<20, 20, 0.5>);
            llVolumeDetect(FALSE);
        }
        else if (param == 801) // Target Wall
        {
            llSay(0, "✓ Target wall created");
            llSetText("TARGET\nWALL", <0.8, 0.2, 0.2>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0.8, 0.2, 0.2>, ALL_SIDES);
            llSetScale(<1, 6, 4>);
            llVolumeDetect(FALSE);
        }
        else if (param >= 901 && param <= 902) // Falling Balls
        {
            integer ballNum = param - 900;
            llSay(0, "✓ Falling ball " + (string)ballNum + " created");
            llSetText("FALL BALL\n" + (string)ballNum, <0, 0, 1>, 1.0);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSetColor(<0, 0, 1>, ALL_SIDES);
            llSetScale(<1, 1, 1>);
            llVolumeDetect(FALSE);
        }
    }
    
    timer()
    {
        // Auto cleanup for projectiles
        llDie();
    }
}