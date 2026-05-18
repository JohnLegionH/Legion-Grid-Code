# Correct Setup Instructions for Advanced Physics Testing

## 🚀 Quick Setup (The Right Way!)

### Step 1: Create the Object Creator
1. **In-world**: Create a basic prim (cube/sphere/etc.)
2. **Right-click** the prim → **Edit**
3. Go to **Contents** tab
4. Click **New Script**
5. **Replace** the default script with the contents of `advanced_physics_object_creator.lsl`
6. **Save** the script

### Step 2: Create All Test Objects
1. **Touch** the creator object repeatedly (10 times total)
2. Each touch creates different types of test objects:
   - Touch 1: Main test controller
   - Touch 2: Destructible targets (Glass, Metal, Wood)
   - Touch 3: CCD test objects
   - Touch 4: Collision force objects  
   - Touch 5: Material test objects
   - Touch 6: Test projectiles
   - Touch 7: Test floor
   - Touch 8: Target wall
   - Touch 9: Falling objects
   - Touch 10: Shows completion summary

### Step 3: Add the Main Test Script
1. **Find** the "TestController" object that was created
2. **Right-click** → **Edit** → **Contents** tab  
3. **Add** the `comprehensive_physics_test.lsl` script to it
4. **Save**

### Step 4: Run Advanced Physics Tests
1. **Touch** the TestController object
2. Each touch runs a different advanced physics test
3. **Watch chat** for detailed test results
4. **Touch repeatedly** to cycle through all 8 tests

## 🎯 What This Creates

The object creator script uses `llRezObject()` to properly create:

- **Test Controller** - Main object to run physics tests
- **Destructible Targets** - Glass, Metal, Wood objects for destruction testing
- **CCD Objects** - Fast launcher and target for continuous collision detection
- **Force Test Objects** - Heavy and light objects for collision force testing
- **Material Objects** - Stone and concrete for material property testing
- **Projectiles** - Various sized projectiles for collision testing
- **Environment** - Floor and walls for complete test setup
- **Dynamic Objects** - Falling balls for gravity/collision testing

## ✅ Why This Works

- Uses **LSL `llRezObject()`** instead of console commands
- Creates objects with **proper physics enabled**
- Sets **appropriate scales, colors, and text** automatically
- Uses **start parameters** to differentiate object types
- **Self-configures** each object when created
- Includes **auto-cleanup** for temporary objects

## 🧪 Testing the Advanced Physics Functions

Once setup is complete, the TestController will test:

1. **`llSetPrimFlags()`** - CCD, destructible, shape type flags
2. **`llSetDestructible()`** - Destruction thresholds and fracture patterns  
3. **`llSetPhysicsMaterial()`** - Material properties (density, friction, restitution)
4. **`llGetCollisionImpulse()`** - Real-time collision force measurement
5. **`llEnablePhysicsLogging()`** - Physics debugging control
6. **Physics Constants** - All new constant values
7. **Combined Features** - Multiple functions working together
8. **Reset Functions** - Clearing all settings

This is the correct OpenSim way to create and test objects! 🎉