
// High-Fidelity Collision Test Script (v3)

// This script creates a controlled environment to test collision reliability.
// It builds a box out of 5 simple prims and drops a ball into it.
// A successful test means the ball collides with the floor of the box.
// A failed test means the ball passes through the floor.

// --- Configuration ---
vector BOX_SIZE = <2.0, 2.0, 1.0>;
float WALL_THICKNESS = 0.1;

// --- Globals ---
integer gWallsRezzed = 0;

// Function to configure a piece of the box
configure_wall(string name)
{
    // **CRITICAL**: Tell the physics engine we want a MESH shape, not a HULL.
    // We also subscribe to collision events.
    llSetVehicleType(VEHICLE_TYPE_NONE); // Required for prim-specific physics settings
    llSetPrimFlags(PF_PHYSICS_SHAPE_TYPE, PRIM_PHYSICS_SHAPE_MESH);
    llCollisionSound("", 1.0); // A common way to subscribe to collisions

    // Make it phantom for the test so the ball can fall inside
    llSetPhantom(TRUE);

    // Set a color so we can see it
    llSetColor(<0.5, 0.5, 1.0>, ALL_SIDES); // Light blue
}

run_test()
{
    llSay(0, "Running collision reliability test (v3)...");
    llSay(0, "Rez-ing a 5-prim box. This may take a moment.");

    // --- Step 1: Create the Target Box (Floor first) ---
    vector floorPos = llGetPos() + <0, 0, 10>;
    llRezObject("TestBox_Floor", floorPos, ZERO_VECTOR, ZERO_ROTATION, 0);
}

default
{
    state_entry()
    {
        llSay(0, "Touch to begin the collision test (v3).");
        gWallsRezzed = 0;
    }

    touch_start(integer total_number)
    {
        run_test();
    }

    object_rez(key id)
    {
        string name = llGetObjectName();

        if (llSubStringIndex(name, "TestBox_") == 0)
        {
            // --- Step 2: Configure the Box parts ---
            configure_wall(name);
            gWallsRezzed++;

            // If this is the floor, rez the walls
            if (name == "TestBox_Floor")
            {
                vector pos = llGetPos();
                // Wall 1 (Positive X)
                llRezObject("TestBox_Wall1", pos + <(BOX_SIZE.x / 2.0), 0, (BOX_SIZE.z / 2.0)>, ZERO_VECTOR, ZERO_ROTATION, 0);
                // Wall 2 (Negative X)
                llRezObject("TestBox_Wall2", pos + <-(BOX_SIZE.x / 2.0), 0, (BOX_SIZE.z / 2.0)>, ZERO_VECTOR, ZERO_ROTATION, 0);
                // Wall 3 (Positive Y)
                llRezObject("TestBox_Wall3", pos + <0, (BOX_SIZE.y / 2.0), (BOX_SIZE.z / 2.0)>, ZERO_VECTOR, ZERO_ROTATION, 0);
                // Wall 4 (Negative Y)
                llRezObject("TestBox_Wall4", pos + <0, -(BOX_SIZE.y / 2.0), (BOX_SIZE.z / 2.0)>, ZERO_VECTOR, ZERO_ROTATION, 0);
            }

            // Once all 5 pieces are rezzed, drop the ball
            if (gWallsRezzed >= 5)
            {
                 // --- Step 3: Create and Drop the Projectile ---
                llSay(0, "Box construction complete. Dropping projectile...");
                vector ballPos = llGetPos() + <0, 0, 12>;
                llRezObject("Projectile Ball", ballPos, <0, 0, -5>, ZERO_ROTATION, 0);
            }
        }
        else if (name == "Projectile Ball")
        {
            // Configure the projectile
            llSetPrimitiveParams([
                PRIM_TYPE, PRIM_TYPE_SPHERE,
                PRIM_SIZE, <0.2, 0.2, 0.2>
            ]);
            llSetColor(<1,0,0>, ALL_SIDES); // Make it red
            llSetStatus(STATUS_PHYSICS, TRUE);

            // **CRITICAL**: Enable Continuous Collision Detection (CCD) for the ball
            llSetPrimFlags(PF_USE_CCD, TRUE);
        }
    }

    collision_start(integer num_detected)
    {
        // --- Step 4: Report Results ---
        integer i = 0;
        for (i = 0; i < num_detected; ++i)
        {
            if (llDetectedName(i) == "Projectile Ball")
            {
                llSay(0, "!!! SUCCESS: Collision detected between box and ball!");
                // Clean up
                llDie(); // End the test
            }
        }
    }
}
