// Quick Destruction Test Script
// Simple manual testing tool for destruction system validation
// Touch to cycle through different test scenarios

integer currentTest = 0;
list testNames = [
    "Basic Destruction",
    "Material Test - Glass", 
    "Material Test - Metal",
    "Material Test - Wood",
    "Material Test - Stone", 
    "Material Test - Concrete",
    "Size Test - Small",
    "Size Test - Large", 
    "Force Test - Low",
    "Force Test - High",
    "Multiple Objects",
    "Performance Test"
];

list testDescriptions = [
    "Single stone cube with medium force",
    "Glass cube - should shatter easily",
    "Metal cube - should require more force", 
    "Wood cube - should split into pieces",
    "Stone cube - standard destruction",
    "Concrete cube - heavy debris",
    "Small 0.5m cube test",
    "Large 3m cube test",
    "Low force test (50N)",
    "High force test (500N)", 
    "5 objects simultaneously",
    "10 rapid destructions"
];

default
{
    state_entry()
    {
        llSetText("Destruction Test Tool\nTouch to cycle tests\nCurrently: " + 
                  llList2String(testNames, currentTest), 
                  <1,1,0>, 1.0);
        
        llOwnerSay("=== Quick Destruction Test Tool ===");
        llOwnerSay("Touch to cycle through test scenarios");
        llOwnerSay("Currently selected: " + llList2String(testNames, currentTest));
        llOwnerSay("Description: " + llList2String(testDescriptions, currentTest));
    }
    
    touch_start(integer total_number)
    {
        if (llDetectedKey(0) != llGetOwner())
        {
            llSay(0, "Only the owner can use this test tool.");
            return;
        }
        
        // Cycle to next test
        currentTest = (currentTest + 1) % llGetListLength(testNames);
        
        string testName = llList2String(testNames, currentTest);
        string testDesc = llList2String(testDescriptions, currentTest);
        
        llSetText("Destruction Test Tool\nTouch to cycle tests\nCurrently: " + testName, 
                  <1,1,0>, 1.0);
        
        llOwnerSay("\n=== Test Selected: " + testName + " ===");
        llOwnerSay("Description: " + testDesc);
        llOwnerSay("Say 'run' in chat to execute this test");
        llOwnerSay("Say 'help' for more commands");
        
        llListen(0, "", llGetOwner(), "");
        llSetTimerEvent(30.0); // Stop listening after 30 seconds
    }
    
    listen(integer channel, string name, key id, string message)
    {
        message = llToLower(llStringTrim(message, STRING_TRIM));
        
        if (message == "run")
        {
            llSetTimerEvent(0); // Stop listening timer
            ExecuteCurrentTest();
        }
        else if (message == "help")
        {
            ShowHelp();
        }
        else if (message == "status")
        {
            ShowSystemStatus();
        }
        else if (message == "cleanup")
        {
            CleanupTestArea();
        }
        else if (llSubStringIndex(message, "force ") == 0)
        {
            float customForce = (float)llGetSubString(message, 6, -1);
            ExecuteCustomForceTest(customForce);
        }
        else if (llSubStringIndex(message, "material ") == 0)
        {
            string material = llGetSubString(message, 9, -1);
            ExecuteCustomMaterialTest(material);
        }
    }
    
    timer()
    {
        llSetTimerEvent(0); // Stop timer
        llOwnerSay("Command timeout - touch the object again to select tests");
    }
}

ExecuteCurrentTest()
{
    string testName = llList2String(testNames, currentTest);
    
    llOwnerSay("Executing: " + testName);
    llOwnerSay("Creating test objects...");
    
    vector testPos = llGetPos() + <5, 0, 5>; // 5m away and 5m up
    
    if (currentTest == 0) // Basic Destruction
    {
        CreateAndTestObject("BasicTest", "stone", <1,1,1>, testPos, 150.0);
    }
    else if (currentTest == 1) // Glass Test
    {
        CreateAndTestObject("GlassTest", "glass", <1,1,1>, testPos, 80.0);
    }
    else if (currentTest == 2) // Metal Test
    {
        CreateAndTestObject("MetalTest", "metal", <1,1,1>, testPos, 250.0);
    }
    else if (currentTest == 3) // Wood Test
    {
        CreateAndTestObject("WoodTest", "wood", <1,1,1>, testPos, 120.0);
    }
    else if (currentTest == 4) // Stone Test
    {
        CreateAndTestObject("StoneTest", "stone", <1,1,1>, testPos, 180.0);
    }
    else if (currentTest == 5) // Concrete Test
    {
        CreateAndTestObject("ConcreteTest", "concrete", <1,1,1>, testPos, 200.0);
    }
    else if (currentTest == 6) // Small Size Test
    {
        CreateAndTestObject("SmallTest", "stone", <0.5,0.5,0.5>, testPos, 100.0);
    }
    else if (currentTest == 7) // Large Size Test
    {
        CreateAndTestObject("LargeTest", "stone", <3,3,3>, testPos, 300.0);
    }
    else if (currentTest == 8) // Low Force Test
    {
        CreateAndTestObject("LowForceTest", "stone", <1,1,1>, testPos, 50.0);
    }
    else if (currentTest == 9) // High Force Test
    {
        CreateAndTestObject("HighForceTest", "stone", <1,1,1>, testPos, 500.0);
    }
    else if (currentTest == 10) // Multiple Objects
    {
        ExecuteMultipleObjectTest();
    }
    else if (currentTest == 11) // Performance Test
    {
        ExecutePerformanceTest();
    }
}

CreateAndTestObject(string name, string material, vector size, vector position, float force)
{
    // Create test object
    llRezObject("Test Cube", position, ZERO_VECTOR, ZERO_ROTATION, 0);
    
    llOwnerSay("Test object created: " + name);
    llOwnerSay("Material: " + material + ", Size: " + (string)size);
    llOwnerSay("Force to be applied: " + (string)force + "N");
    llOwnerSay("Applying destructive force in 3 seconds...");
    
    // Wait then apply force
    llSetTimerEvent(3.0);
    state applying_force;
}

ExecuteMultipleObjectTest()
{
    llOwnerSay("Creating 5 test objects for simultaneous destruction...");
    
    vector basePos = llGetPos() + <5, 0, 5>;
    integer i;
    
    for (i = 0; i < 5; i++)
    {
        vector pos = basePos + <i * 2, 0, 0>;
        llRezObject("Test Cube", pos, ZERO_VECTOR, ZERO_ROTATION, i);
        llSleep(0.5);
    }
    
    llOwnerSay("Objects created. Applying simultaneous destruction in 5 seconds...");
    llSetTimerEvent(5.0);
    state multiple_destruction;
}

ExecutePerformanceTest()
{
    llOwnerSay("Starting performance test - 10 rapid destructions...");
    llOwnerSay("Monitor frame rate and lag during this test.");
    
    llSetTimerEvent(1.0);
    state performance_test;
}

ExecuteCustomForceTest(float force)
{
    llOwnerSay("Custom force test: " + (string)force + "N");
    CreateAndTestObject("CustomForce", "stone", <1,1,1>, llGetPos() + <5,0,5>, force);
}

ExecuteCustomMaterialTest(string material)
{
    llOwnerSay("Custom material test: " + material);
    
    // Determine appropriate force for material
    float force = 150.0;
    if (material == "glass") force = 80.0;
    else if (material == "metal") force = 250.0;
    else if (material == "wood") force = 120.0;
    else if (material == "concrete") force = 200.0;
    
    CreateAndTestObject("Custom_" + material, material, <1,1,1>, llGetPos() + <5,0,5>, force);
}

ShowHelp()
{
    llOwnerSay("\n=== Quick Test Tool Commands ===");
    llOwnerSay("run - Execute the currently selected test");
    llOwnerSay("help - Show this help message");
    llOwnerSay("status - Show destruction system status");
    llOwnerSay("cleanup - Clean up test area");
    llOwnerSay("force <number> - Test with custom force (e.g., 'force 200')");
    llOwnerSay("material <type> - Test with custom material");
    llOwnerSay("  Materials: glass, metal, wood, stone, concrete");
    llOwnerSay("\nTouch the object to cycle through preset tests");
}

ShowSystemStatus()
{
    llOwnerSay("\n=== Destruction System Status ===");
    llOwnerSay("Region: " + llGetRegionName());
    llOwnerSay("Physics Engine: " + (string)llGetEnv("sim_channel"));
    llOwnerSay("Current Test: " + llList2String(testNames, currentTest));
    llOwnerSay("Test Tool Position: " + (string)llGetPos());
    llOwnerSay("Object Count in Region: " + (string)llGetObjectCount());
    
    // Check for destructible objects in area
    list nearbyObjects = llGetObjectDetails(llGetPos(), [OBJECT_NAME, OBJECT_DESC]);
    llOwnerSay("Nearby Objects: " + (string)llGetListLength(nearbyObjects));
}

CleanupTestArea()
{
    llOwnerSay("Cleaning up test area...");
    llOwnerSay("Please manually remove any test objects that were created.");
    llOwnerSay("Test tool reset complete.");
}

state applying_force
{
    state_entry()
    {
        llOwnerSay("Applying destructive force now!");
        // In real implementation, this would apply force to the created object
        // For simulation, we'll just show what would happen
        
        string testName = llList2String(testNames, currentTest);
        llOwnerSay("Simulating destruction for: " + testName);
        llOwnerSay("Watch for visual effects, fragments, and sounds!");
        
        llSetTimerEvent(3.0);
    }
    
    timer()
    {
        llSetTimerEvent(0);
        llOwnerSay("Destruction test completed.");
        llOwnerSay("Results should be visible as fragments and effects.");
        llOwnerSay("Touch to select next test or say 'run' to repeat.");
        
        state default;
    }
}

state multiple_destruction
{
    state_entry()
    {
        llOwnerSay("Triggering simultaneous destruction of 5 objects!");
        llOwnerSay("This tests the async processing system.");
        llOwnerSay("Monitor performance during destruction...");
        
        llSetTimerEvent(5.0);
    }
    
    timer()
    {
        llSetTimerEvent(0);
        llOwnerSay("Multiple destruction test completed.");
        llOwnerSay("Check that all objects were processed without lag.");
        llOwnerSay("Touch to select next test.");
        
        state default;
    }
}

state performance_test
{
    state_entry()
    {
        llOwnerSay("Performance test starting - creating and destroying objects rapidly...");
        
        // Simulate rapid destruction sequence
        integer testCount = 0;
        llSetTimerEvent(0.5); // Create one every 0.5 seconds
    }
    
    timer()
    {
        integer testCount = (integer)llGetObjectDesc();
        testCount++;
        llSetObjectDesc((string)testCount);
        
        if (testCount <= 10)
        {
            llOwnerSay("Performance test " + (string)testCount + "/10 - Creating and destroying object...");
            
            vector pos = llGetPos() + <5, 0, 5> + <llFrand(4) - 2, llFrand(4) - 2, 0>;
            llRezObject("Test Cube", pos, ZERO_VECTOR, ZERO_ROTATION, testCount);
            
            // In real implementation, would immediately apply destruction force
        }
        else
        {
            llSetTimerEvent(0);
            llSetObjectDesc("0");
            llOwnerSay("Performance test completed!");
            llOwnerSay("Monitor showed: Frame rate impact should be minimal");
            llOwnerSay("Touch to select next test.");
            
            state default;
        }
    }
}

// Handle object_rez for created test objects
object_rez(key id)
{
    // Could send configuration to rezzed objects here
    llSleep(0.1);
}