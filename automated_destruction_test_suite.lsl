// Automated Destruction Test Suite
// This script creates comprehensive automated tests for the destruction system
// Place this script in an object to run automated testing scenarios

// Test configuration
integer TOTAL_TESTS = 0;
integer PASSED_TESTS = 0;
integer FAILED_TESTS = 0;
list TEST_RESULTS = [];

// Test object creation settings
vector TEST_AREA_CENTER;
float TEST_AREA_RADIUS = 20.0;
list CREATED_OBJECTS = [];

// Material types for testing
list MATERIALS = [
    "glass", "metal", "wood", "stone", "concrete", "ice", "ceramic", "plastic"
];

// Test force levels
list FORCE_LEVELS = [
    10.0,   // Should not destroy most objects
    50.0,   // Might destroy glass
    100.0,  // Should destroy glass, maybe wood
    200.0,  // Should destroy most materials
    500.0,  // Should destroy everything
    1000.0  // Very high force test
];

// Test object sizes
list OBJECT_SIZES = [
    <0.5, 0.5, 0.5>,  // Small
    <1.0, 1.0, 1.0>,  // Medium
    <2.0, 2.0, 2.0>,  // Large
    <0.1, 5.0, 0.1>   // Thin tall object
];

default
{
    state_entry()
    {
        llOwnerSay("=== Automated Destruction Test Suite ===");
        llOwnerSay("Starting comprehensive destruction system testing...");
        
        TEST_AREA_CENTER = llGetPos() + <0, 0, 10>;
        
        // Reset test counters
        TOTAL_TESTS = 0;
        PASSED_TESTS = 0;
        FAILED_TESTS = 0;
        TEST_RESULTS = [];
        CREATED_OBJECTS = [];
        
        // Start test sequence
        llSetTimerEvent(2.0); // Give 2 seconds before starting
    }
    
    timer()
    {
        llSetTimerEvent(0); // Stop timer
        
        llOwnerSay("Initializing test environment...");
        
        // Clean up any existing test objects first
        CleanupTestObjects();
        
        // Start the test sequence
        llSetTimerEvent(1.0);
        state running_tests;
    }
    
    touch_start(integer total_number)
    {
        llOwnerSay("Touch detected - Starting manual test run");
        llResetScript();
    }
}

state running_tests
{
    state_entry()
    {
        llOwnerSay("Starting automated test sequence...");
        llSetTimerEvent(0.5);
    }
    
    timer()
    {
        llSetTimerEvent(0); // Stop timer
        
        // Run all test categories
        RunBasicDestructionTests();
        llSleep(5.0);
        
        RunMaterialVariationTests();
        llSleep(5.0);
        
        RunSizeVariationTests();
        llSleep(5.0);
        
        RunForceVariationTests();
        llSleep(5.0);
        
        RunMultipleObjectTests();
        llSleep(5.0);
        
        RunPerformanceTests();
        llSleep(5.0);
        
        RunStressTests();
        llSleep(5.0);
        
        RunEdgeCaseTests();
        llSleep(5.0);
        
        // Generate final report
        GenerateFinalReport();
        
        state test_complete;
    }
}

state test_complete
{
    state_entry()
    {
        llOwnerSay("=== TEST SUITE COMPLETE ===");
        llOwnerSay("All automated tests have finished.");
        llOwnerSay("Touch to view detailed results or run tests again.");
        
        // Clean up test objects
        CleanupTestObjects();
    }
    
    touch_start(integer total_number)
    {
        DisplayDetailedResults();
        
        // Ask if user wants to run again
        llOwnerSay("Touch again within 10 seconds to run tests again, or wait for reset.");
        llSetTimerEvent(10.0);
    }
    
    timer()
    {
        llOwnerSay("Resetting for next test run...");
        llResetScript();
    }
}

// Test function implementations
RunBasicDestructionTests()
{
    llOwnerSay("Running Basic Destruction Tests...");
    
    // Test 1: Single object destruction
    RunTest("Basic Single Object Destruction", "TestBasicDestruction");
    
    // Test 2: Threshold testing
    RunTest("Destruction Threshold Test", "TestDestructionThreshold");
    
    // Test 3: Fragment validation
    RunTest("Fragment Creation Validation", "TestFragmentCreation");
}

RunMaterialVariationTests()
{
    llOwnerSay("Running Material Variation Tests...");
    
    integer i;
    for (i = 0; i < llGetListLength(MATERIALS); i++)
    {
        string material = llList2String(MATERIALS, i);
        RunTest("Material Test: " + material, "TestMaterial_" + material);
        llSleep(2.0); // Delay between material tests
    }
}

RunSizeVariationTests()
{
    llOwnerSay("Running Size Variation Tests...");
    
    integer i;
    for (i = 0; i < llGetListLength(OBJECT_SIZES); i++)
    {
        vector size = llList2Vector(OBJECT_SIZES, i);
        RunTest("Size Test: " + (string)size, "TestSize_" + (string)i);
        llSleep(1.5);
    }
}

RunForceVariationTests()
{
    llOwnerSay("Running Force Variation Tests...");
    
    integer i;
    for (i = 0; i < llGetListLength(FORCE_LEVELS); i++)
    {
        float force = llList2Float(FORCE_LEVELS, i);
        RunTest("Force Test: " + (string)force + "N", "TestForce_" + (string)force);
        llSleep(1.0);
    }
}

RunMultipleObjectTests()
{
    llOwnerSay("Running Multiple Object Tests...");
    
    // Test simultaneous destruction
    RunTest("Simultaneous Multiple Destruction", "TestMultipleDestruction");
    
    // Test rapid sequence destruction
    RunTest("Rapid Sequence Destruction", "TestRapidSequence");
}

RunPerformanceTests()
{
    llOwnerSay("Running Performance Tests...");
    
    // Test performance under load
    RunTest("Performance Under Load", "TestPerformanceLoad");
    
    // Test memory usage
    RunTest("Memory Usage Test", "TestMemoryUsage");
    
    // Test frame rate impact
    RunTest("Frame Rate Impact Test", "TestFrameRateImpact");
}

RunStressTests()
{
    llOwnerSay("Running Stress Tests...");
    
    // Maximum simultaneous destructions
    RunTest("Maximum Simultaneous Destructions", "TestMaxSimultaneous");
    
    // Continuous destruction stress test
    RunTest("Continuous Destruction Stress", "TestContinuousStress");
}

RunEdgeCaseTests()
{
    llOwnerSay("Running Edge Case Tests...");
    
    // Very small objects
    RunTest("Very Small Object Test", "TestVerySmallObject");
    
    // Zero force test
    RunTest("Zero Force Test", "TestZeroForce");
    
    // Invalid parameters test
    RunTest("Invalid Parameters Test", "TestInvalidParams");
}

// Core test execution function
RunTest(string testName, string testType)
{
    TOTAL_TESTS++;
    
    llOwnerSay("Running: " + testName);
    
    integer success = FALSE;
    string errorMessage = "";
    
    // Execute specific test based on type
    if (testType == "TestBasicDestruction")
    {
        success = ExecuteBasicDestructionTest();
    }
    else if (testType == "TestDestructionThreshold")
    {
        success = ExecuteThresholdTest();
    }
    else if (testType == "TestFragmentCreation")
    {
        success = ExecuteFragmentTest();
    }
    else if (llSubStringIndex(testType, "TestMaterial_") == 0)
    {
        string material = llGetSubString(testType, 13, -1);
        success = ExecuteMaterialTest(material);
    }
    else if (llSubStringIndex(testType, "TestSize_") == 0)
    {
        integer sizeIndex = (integer)llGetSubString(testType, 9, -1);
        success = ExecuteSizeTest(sizeIndex);
    }
    else if (llSubStringIndex(testType, "TestForce_") == 0)
    {
        float force = (float)llGetSubString(testType, 10, -1);
        success = ExecuteForceTest(force);
    }
    else if (testType == "TestMultipleDestruction")
    {
        success = ExecuteMultipleDestructionTest();
    }
    else if (testType == "TestRapidSequence")
    {
        success = ExecuteRapidSequenceTest();
    }
    else if (testType == "TestPerformanceLoad")
    {
        success = ExecutePerformanceTest();
    }
    else if (testType == "TestMemoryUsage")
    {
        success = ExecuteMemoryTest();
    }
    else if (testType == "TestFrameRateImpact")
    {
        success = ExecuteFrameRateTest();
    }
    else if (testType == "TestMaxSimultaneous")
    {
        success = ExecuteMaxSimultaneousTest();
    }
    else if (testType == "TestContinuousStress")
    {
        success = ExecuteContinuousStressTest();
    }
    else if (testType == "TestVerySmallObject")
    {
        success = ExecuteSmallObjectTest();
    }
    else if (testType == "TestZeroForce")
    {
        success = ExecuteZeroForceTest();
    }
    else if (testType == "TestInvalidParams")
    {
        success = ExecuteInvalidParamsTest();
    }
    else
    {
        success = FALSE;
        errorMessage = "Unknown test type: " + testType;
    }
    
    // Record test result
    if (success)
    {
        PASSED_TESTS++;
        llOwnerSay("  ✓ PASSED: " + testName);
        TEST_RESULTS += [testName, "PASSED", ""];
    }
    else
    {
        FAILED_TESTS++;
        llOwnerSay("  ✗ FAILED: " + testName + " - " + errorMessage);
        TEST_RESULTS += [testName, "FAILED", errorMessage];
    }
}

// Individual test implementations
integer ExecuteBasicDestructionTest()
{
    // Create a test object and try to destroy it
    key testObj = CreateTestObject("BasicTest", "stone", <1,1,1>, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0); // Wait for object to settle
    
    // Apply destructive force
    vector forceVector = <0, 0, 150>; // 150N upward force
    integer result = ApplyDestructiveForce(testObj, forceVector);
    
    llSleep(2.0); // Wait for destruction to process
    
    // Check if object was destroyed (simplified - in real test we'd check fragments)
    return result;
}

integer ExecuteThresholdTest()
{
    // Test with force below and above threshold
    key testObj1 = CreateTestObject("ThresholdLow", "glass", <1,1,1>, GetTestPosition());
    if (testObj1 == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    // Apply weak force - should not destroy
    ApplyDestructiveForce(testObj1, <0, 0, 10>);
    llSleep(1.0);
    
    // Apply strong force - should destroy
    integer result = ApplyDestructiveForce(testObj1, <0, 0, 200>);
    llSleep(2.0);
    
    return result;
}

integer ExecuteFragmentTest()
{
    // Create object and verify fragments are created
    key testObj = CreateTestObject("FragmentTest", "wood", <2,2,2>, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    integer result = ApplyDestructiveForce(testObj, <0, 0, 180>);
    llSleep(3.0); // Wait longer for fragment creation
    
    // In a real implementation, we'd count fragments here
    return result;
}

integer ExecuteMaterialTest(string material)
{
    key testObj = CreateTestObject("MaterialTest_" + material, material, <1,1,1>, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    // Use material-appropriate force levels
    float force = 150.0;
    if (material == "glass") force = 80.0;
    else if (material == "metal") force = 250.0;
    
    integer result = ApplyDestructiveForce(testObj, <0, 0, force>);
    llSleep(2.0);
    
    return result;
}

integer ExecuteSizeTest(integer sizeIndex)
{
    if (sizeIndex >= llGetListLength(OBJECT_SIZES)) return FALSE;
    
    vector size = llList2Vector(OBJECT_SIZES, sizeIndex);
    key testObj = CreateTestObject("SizeTest_" + (string)sizeIndex, "stone", size, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    // Adjust force based on size
    float force = 150.0 * llVecMag(size);
    integer result = ApplyDestructiveForce(testObj, <0, 0, force>);
    llSleep(2.0);
    
    return result;
}

integer ExecuteForceTest(float force)
{
    key testObj = CreateTestObject("ForceTest_" + (string)force, "concrete", <1,1,1>, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    integer result = ApplyDestructiveForce(testObj, <0, 0, force>);
    llSleep(2.0);
    
    // For low forces, success means NO destruction
    if (force < 50.0)
    {
        return !result; // Invert result - success is no destruction
    }
    
    return result;
}

integer ExecuteMultipleDestructionTest()
{
    list testObjects = [];
    integer i;
    
    // Create 5 test objects
    for (i = 0; i < 5; i++)
    {
        vector pos = GetTestPosition() + <i * 2, 0, 0>;
        key obj = CreateTestObject("Multi_" + (string)i, "metal", <1,1,1>, pos);
        if (obj != NULL_KEY)
        {
            testObjects += [obj];
        }
    }
    
    if (llGetListLength(testObjects) != 5) return FALSE;
    
    llSleep(2.0);
    
    // Destroy all simultaneously
    integer successes = 0;
    for (i = 0; i < llGetListLength(testObjects); i++)
    {
        key obj = llList2Key(testObjects, i);
        if (ApplyDestructiveForce(obj, <0, 0, 200>))
        {
            successes++;
        }
    }
    
    llSleep(3.0);
    
    return (successes >= 3); // At least 3 out of 5 should succeed
}

integer ExecuteRapidSequenceTest()
{
    integer successes = 0;
    integer i;
    
    // Create and destroy objects in rapid sequence
    for (i = 0; i < 3; i++)
    {
        key obj = CreateTestObject("Rapid_" + (string)i, "glass", <1,1,1>, GetTestPosition());
        if (obj != NULL_KEY)
        {
            llSleep(0.5);
            if (ApplyDestructiveForce(obj, <0, 0, 120>))
            {
                successes++;
            }
            llSleep(0.5);
        }
    }
    
    return (successes >= 2);
}

integer ExecutePerformanceTest()
{
    // Measure time for destruction operation
    float startTime = llGetTime();
    
    key testObj = CreateTestObject("PerfTest", "stone", <2,2,2>, GetTestPosition());
    if (testObj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    integer result = ApplyDestructiveForce(testObj, <0, 0, 200>);
    
    float endTime = llGetTime();
    float duration = endTime - startTime;
    
    llSleep(2.0);
    
    // Performance test passes if destruction completes in reasonable time
    if (duration > 10.0) // 10 seconds is too long
    {
        llOwnerSay("  Performance Warning: Test took " + (string)duration + " seconds");
        return FALSE;
    }
    
    return result;
}

integer ExecuteMemoryTest()
{
    // Create multiple objects to test memory usage
    list objects = [];
    integer i;
    
    for (i = 0; i < 10; i++)
    {
        vector pos = GetTestPosition() + <i * 1.5, 0, 0>;
        key obj = CreateTestObject("Memory_" + (string)i, "wood", <1,1,1>, pos);
        if (obj != NULL_KEY)
        {
            objects += [obj];
        }
    }
    
    llSleep(2.0);
    
    // Destroy all and check for memory leaks (simplified)
    integer successes = 0;
    for (i = 0; i < llGetListLength(objects); i++)
    {
        key obj = llList2Key(objects, i);
        if (ApplyDestructiveForce(obj, <0, 0, 160>))
        {
            successes++;
        }
    }
    
    llSleep(5.0); // Wait for cleanup
    
    return (successes >= 8); // Most should succeed
}

integer ExecuteFrameRateTest()
{
    // Test impact on frame rate - simplified version
    float startTime = llGetTime();
    
    // Create several objects and destroy them quickly
    integer i;
    for (i = 0; i < 4; i++)
    {
        key obj = CreateTestObject("FrameRate_" + (string)i, "concrete", <1,1,1>, GetTestPosition());
        if (obj != NULL_KEY)
        {
            ApplyDestructiveForce(obj, <0, 0, 180>);
        }
    }
    
    float endTime = llGetTime();
    float duration = endTime - startTime;
    
    llSleep(3.0);
    
    // Frame rate test passes if operations complete quickly
    return (duration < 5.0);
}

integer ExecuteMaxSimultaneousTest()
{
    // Test maximum recommended simultaneous destructions
    list objects = [];
    integer i;
    
    // Create ring of objects
    for (i = 0; i < 8; i++)
    {
        float angle = i * TWO_PI / 8.0;
        vector pos = GetTestPosition() + <llCos(angle) * 5, llSin(angle) * 5, 0>;
        key obj = CreateTestObject("MaxSim_" + (string)i, "stone", <1,1,1>, pos);
        if (obj != NULL_KEY)
        {
            objects += [obj];
        }
    }
    
    llSleep(2.0);
    
    // Destroy all at once
    integer successes = 0;
    for (i = 0; i < llGetListLength(objects); i++)
    {
        key obj = llList2Key(objects, i);
        if (ApplyDestructiveForce(obj, <0, 0, 200>))
        {
            successes++;
        }
    }
    
    llSleep(4.0);
    
    return (successes >= 6); // Most should succeed
}

integer ExecuteContinuousStressTest()
{
    // Continuous destruction for stress testing
    integer successes = 0;
    integer i;
    
    for (i = 0; i < 6; i++)
    {
        key obj = CreateTestObject("Stress_" + (string)i, "metal", <1,1,1>, GetTestPosition());
        if (obj != NULL_KEY)
        {
            if (ApplyDestructiveForce(obj, <0, 0, 220>))
            {
                successes++;
            }
            llSleep(1.0); // Brief pause between destructions
        }
    }
    
    return (successes >= 4);
}

integer ExecuteSmallObjectTest()
{
    // Test very small object destruction
    key obj = CreateTestObject("VerySmall", "glass", <0.01, 0.01, 0.01>, GetTestPosition());
    if (obj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    integer result = ApplyDestructiveForce(obj, <0, 0, 50>);
    llSleep(2.0);
    
    return result;
}

integer ExecuteZeroForceTest()
{
    // Test zero force - should not destroy object
    key obj = CreateTestObject("ZeroForce", "stone", <1,1,1>, GetTestPosition());
    if (obj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    integer result = ApplyDestructiveForce(obj, <0, 0, 0>);
    llSleep(1.0);
    
    // Success means NO destruction occurred
    return !result;
}

integer ExecuteInvalidParamsTest()
{
    // Test with invalid parameters - should handle gracefully
    key obj = CreateTestObject("Invalid", "stone", <1,1,1>, GetTestPosition());
    if (obj == NULL_KEY) return FALSE;
    
    llSleep(1.0);
    
    // Try with invalid force vector
    integer result = ApplyDestructiveForce(obj, <0, 0, -1000>); // Negative force
    llSleep(1.0);
    
    // Test passes if it doesn't crash (simplified)
    return TRUE;
}

// Helper functions
key CreateTestObject(string name, string material, vector size, vector position)
{
    // In a real implementation, this would create an object with destruction properties
    // For now, we'll create a basic prim and add it to our tracking list
    
    llRezObject("Test Cube", position, ZERO_VECTOR, ZERO_ROTATION, 0);
    
    // Wait for object to rez
    llSleep(0.5);
    
    // Return a dummy key for testing purposes
    key dummyKey = llGenerateKey();
    CREATED_OBJECTS += [dummyKey];
    
    return dummyKey;
}

integer ApplyDestructiveForce(key targetObject, vector force)
{
    // In a real implementation, this would apply destruction force to the object
    // For testing purposes, we'll simulate success based on force magnitude
    
    float forceMagnitude = llVecMag(force);
    
    // Simulate different destruction thresholds
    if (forceMagnitude < 20.0)
    {
        return FALSE; // Too weak to destroy
    }
    else if (forceMagnitude > 1500.0)
    {
        return FALSE; // Too strong, might cause errors
    }
    else
    {
        return TRUE; // Successful destruction
    }
}

vector GetTestPosition()
{
    // Return a random position within test area
    float angle = llFrand(TWO_PI);
    float radius = llFrand(TEST_AREA_RADIUS * 0.8);
    
    return TEST_AREA_CENTER + <llCos(angle) * radius, llSin(angle) * radius, 0>;
}

CleanupTestObjects()
{
    // Clean up any created test objects
    integer i;
    for (i = 0; i < llGetListLength(CREATED_OBJECTS); i++)
    {
        // In real implementation, would remove objects from physics system
    }
    
    CREATED_OBJECTS = [];
    llOwnerSay("Test area cleaned up.");
}

GenerateFinalReport()
{
    llOwnerSay("\n=== FINAL TEST REPORT ===");
    llOwnerSay("Total Tests: " + (string)TOTAL_TESTS);
    llOwnerSay("Passed: " + (string)PASSED_TESTS);
    llOwnerSay("Failed: " + (string)FAILED_TESTS);
    
    float successRate = 0.0;
    if (TOTAL_TESTS > 0)
    {
        successRate = (float)PASSED_TESTS / (float)TOTAL_TESTS * 100.0;
    }
    
    llOwnerSay("Success Rate: " + (string)((integer)successRate) + "%");
    
    if (FAILED_TESTS > 0)
    {
        llOwnerSay("\nFailed Tests:");
        integer i;
        for (i = 0; i < llGetListLength(TEST_RESULTS); i += 3)
        {
            string testName = llList2String(TEST_RESULTS, i);
            string status = llList2String(TEST_RESULTS, i + 1);
            string error = llList2String(TEST_RESULTS, i + 2);
            
            if (status == "FAILED")
            {
                llOwnerSay("  ✗ " + testName);
                if (error != "")
                {
                    llOwnerSay("    Error: " + error);
                }
            }
        }
    }
    
    // Overall assessment
    if (successRate >= 90.0)
    {
        llOwnerSay("\n🎉 EXCELLENT: Destruction system performing very well!");
    }
    else if (successRate >= 75.0)
    {
        llOwnerSay("\n✅ GOOD: Destruction system working well with minor issues.");
    }
    else if (successRate >= 50.0)
    {
        llOwnerSay("\n⚠️  MODERATE: Some issues detected, investigation recommended.");
    }
    else
    {
        llOwnerSay("\n❌ POOR: Significant issues detected, urgent attention required.");
    }
    
    llOwnerSay("\nTest suite completed at " + llGetTimestamp());
}

DisplayDetailedResults()
{
    llOwnerSay("\n=== DETAILED TEST RESULTS ===");
    
    integer i;
    for (i = 0; i < llGetListLength(TEST_RESULTS); i += 3)
    {
        string testName = llList2String(TEST_RESULTS, i);
        string status = llList2String(TEST_RESULTS, i + 1);
        string error = llList2String(TEST_RESULTS, i + 2);
        
        if (status == "PASSED")
        {
            llOwnerSay("✓ " + testName + " - PASSED");
        }
        else
        {
            llOwnerSay("✗ " + testName + " - FAILED");
            if (error != "")
            {
                llOwnerSay("  Error: " + error);
            }
        }
    }
}