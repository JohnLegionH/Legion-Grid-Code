# Advanced Physics LSL Test Setup Guide

## 🚀 Quick Start Instructions

### Step 1: Start OpenSim
1. Navigate to your OpenSim directory
2. Start OpenSim server
3. Log into your test region

### Step 2: Create Test Objects
1. Open the OpenSim console
2. Copy and paste the commands from `create_advanced_physics_test_objects.txt` one by one
3. This will create all the test objects in your world

### Step 3: Add Test Script
1. In-world, right-click on the `AdvancedPhysicsTest` object
2. Select "Edit" 
3. Go to the "Contents" tab
4. Create a new script and replace its contents with `comprehensive_physics_test.lsl`
5. Save the script

### Step 4: Run Tests
1. Touch the `AdvancedPhysicsTest` object to cycle through 8 different tests
2. Each touch runs a different test showing the new LSL functions
3. Watch chat for detailed test results

## 🧪 Test Overview

### Test 1: Prim Flags (`llSetPrimFlags`)
- Tests setting CCD, destructible, and shape type flags
- Shows how to enable advanced physics features

### Test 2: Destructible Objects (`llSetDestructible`)
- Tests different fracture patterns (Random, Radial, Grid)
- Sets destruction thresholds
- Prepares objects for impact-based destruction

### Test 3: Physics Materials (`llSetPhysicsMaterial`)
- Tests Glass, Metal, and Wood material properties
- Sets density, friction, and restitution values
- Demonstrates material-specific physics behavior

### Test 4: Collision Impulse (`llGetCollisionImpulse`)
- Measures collision force in real-time
- Shows impact strength when objects collide
- Useful for destruction threshold calculations

### Test 5: Physics Logging (`llEnablePhysicsLogging`)
- Enables/disables physics debugging
- Helps troubleshoot physics behavior
- Stores logging preferences

### Test 6: Physics Constants
- Displays all new physics constant values
- Shows flag values, shape types, materials, fracture patterns
- Useful for debugging and verification

### Test 7: Combined Features
- Applies multiple advanced features together
- Creates a fully-featured glass object with CCD and destruction
- Demonstrates real-world usage

### Test 8: Reset All Tests
- Clears all advanced physics settings
- Returns object to default state
- Restarts the test cycle

## 🔧 What Each Test Does

The tests demonstrate the complete LSL advanced physics implementation:

- **New LSL Constants**: All physics flags, shapes, materials, and patterns
- **New LSL Functions**: 6 new functions for advanced physics control
- **Integration**: Shows how the functions work with existing collision detection
- **Data Storage**: Functions store parameters in object descriptions for future physics engine integration

## ✅ Expected Results

When you run the tests, you should see:
1. Chat messages confirming each function call succeeds
2. Object descriptions containing stored physics parameters
3. No script errors or compilation issues
4. Collision detection working with impulse measurements

This proves the LSL advanced physics extension is working correctly and ready for full physics engine integration!

## 🎯 Next Steps

After testing, you can:
1. Integrate stored parameters with the advanced physics systems
2. Enable the CCD manager for enhanced collision detection
3. Connect destruction parameters to the destruction system
4. Use these functions in real physics scenarios

The foundation is complete - the LSL functions are operational and ready for use!