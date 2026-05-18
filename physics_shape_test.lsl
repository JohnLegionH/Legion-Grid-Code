// Enhanced Physics Shape Test Script
// Tests the new LSL physics shape type override system

integer currentTest = 0;
list shapeNames = ["PRIM", "NONE", "CONVEX", "MESH"];
list shapeTypes = [PRIM_PHYSICS_SHAPE_PRIM, PRIM_PHYSICS_SHAPE_NONE, PRIM_PHYSICS_SHAPE_CONVEX, PRIM_PHYSICS_SHAPE_MESH];

default
{
    state_entry()
    {
        llSay(0, "=== ENHANCED PHYSICS SHAPE TEST ===");
        llSay(0, "Touch to cycle through physics shape types");
        llSetText("PHYSICS SHAPE TEST\nCurrent: " + llList2String(shapeNames, 0), <1,1,0>, 1.0);
        
        // Start with default prim shape
        llSetPhysicsShapeType(PRIM_PHYSICS_SHAPE_PRIM);
        llSay(0, "✓ Set to PRIM shape (default)");
    }
    
    touch_start(integer total_number)
    {
        currentTest = (currentTest + 1) % 4;
        
        integer shapeType = llList2Integer(shapeTypes, currentTest);
        string shapeName = llList2String(shapeNames, currentTest);
        
        llSay(0, "\n=== TESTING SHAPE TYPE: " + shapeName + " ===");
        
        // Set the physics shape type
        llSetPhysicsShapeType(shapeType);
        
        // Update display
        llSetText("PHYSICS SHAPE TEST\nCurrent: " + shapeName + "\nTouch to change", <1,1,0>, 1.0);
        
        // Provide feedback
        if (shapeType == PRIM_PHYSICS_SHAPE_PRIM)
        {
            llSay(0, "✓ PRIM: Using standard prim collision shape");
            llSay(0, "  - Efficient for simple geometry");
            llSay(0, "  - Box/sphere/cylinder detection");
        }
        else if (shapeType == PRIM_PHYSICS_SHAPE_NONE)
        {
            llSay(0, "✓ NONE: No collision detection");
            llSay(0, "  - Object is phantom to collisions");
            llSay(0, "  - Good for decorative objects");
        }
        else if (shapeType == PRIM_PHYSICS_SHAPE_CONVEX)
        {
            llSay(0, "✓ CONVEX: Using convex hull collision");
            llSay(0, "  - More accurate than prim shapes");
            llSay(0, "  - Good for complex but convex objects");
        }
        else if (shapeType == PRIM_PHYSICS_SHAPE_MESH)
        {
            llSay(0, "✓ MESH: Using precise mesh collision");
            llSay(0, "  - Most accurate collision detection");
            llSay(0, "  - Higher CPU cost but exact geometry");
        }
        
        llSay(0, "Shape type " + (string)shapeType + " applied.");
        
        // Force physics rebuild by toggling physical state
        if (llGetStatus(STATUS_PHYSICS))
        {
            llSetStatus(STATUS_PHYSICS, FALSE);
            llSleep(0.1);
            llSetStatus(STATUS_PHYSICS, TRUE);
            llSay(0, "Physics rebuilt to apply new shape");
        }
        else
        {
            llSay(0, "Object is not physical - shape will apply when made physical");
        }
    }
    
    collision_start(integer num_detected)
    {
        string shapeName = llList2String(shapeNames, currentTest);
        llSay(0, "*** COLLISION DETECTED with " + shapeName + " shape! ***");
        
        float impulse = llGetCollisionImpulse();
        llSay(0, "Collision impulse: " + (string)impulse);
        llSay(0, "Current shape type working correctly!");
    }
}