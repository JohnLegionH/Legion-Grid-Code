# OpenSim Destruction System - Comprehensive Testing Plan

## 📋 Overview

This document outlines comprehensive testing procedures for the OpenSim Destruction System to validate stability, performance, and functionality after critical fixes.

## 🎯 Testing Objectives

1. **Stability** - Ensure no crashes or memory leaks
2. **Performance** - Validate smooth operation under load
3. **Functionality** - Verify all features work as designed
4. **Persistence** - Confirm data survives server restarts
5. **Integration** - Ensure compatibility with OpenSim framework

---

## 🚀 Pre-Test Setup

### 1. Enable Destruction System
**File:** `OpenSim.ini` or `BulletSim.ini`
```ini
[BulletSim]
EnableDestructiblePhysics = true
EnableRepairSystem = true
```

### 2. Create Test Region
- Use a dedicated test region
- Ensure adequate prim allowance (10,000+ recommended)
- Clear existing objects for clean testing

### 3. Verify Module Loading
**Console Command:**
```
show modules
```
**Expected Output:** Should show BulletSim and destruction-related modules loaded

---

## 📝 Test Scripts

### LSL Test Script 1: Basic Destruction
```lsl
// Basic Destruction Test Script
// Drop this script in a prim and touch to make it destructible

default
{
    state_entry()
    {
        llSetText("Touch to make destructible\n|DESTRUCT:1:15.0:0|\n|MATERIAL:0:2.5:0.1:0.9|", <1,1,1>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        // Add destruction parameters to description
        llSetObjectDesc("|DESTRUCT:1:15.0:0||MATERIAL:0:2.5:0.1:0.9|");
        llSetText("Destructible Object\nShoot or collide to destroy", <1,0,0>, 1.0);
        llOwnerSay("Object is now destructible - test by shooting or ramming with vehicle");
    }
}
```

### LSL Test Script 2: Auto-Destructing Test
```lsl
// Auto-Destruction Test Script
// Automatically destroys itself after 10 seconds for rapid testing

default
{
    state_entry()
    {
        llSetObjectDesc("|DESTRUCT:1:5.0:0||MATERIAL:1:1.5:0.2:0.8|");
        llSetText("Auto-destruct in 10s", <1,1,0>, 1.0);
        llSetTimerEvent(10.0);
    }
    
    timer()
    {
        llSetTimerEvent(0.0);
        // Trigger destruction via collision simulation
        vector pos = llGetPos();
        llSetPos(pos + <0,0,0.1>); // Small movement to trigger physics
        llApplyImpulse(<100,0,0>, FALSE); // Strong impulse to trigger destruction
    }
}
```

### LSL Test Script 3: Stress Test Generator
```lsl
// Stress Test Generator
// Creates multiple destructible objects for load testing

integer count = 0;
integer maxObjects = 20;

default
{
    state_entry()
    {
        llSetText("Touch to create " + (string)maxObjects + " test objects", <0,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        if(count >= maxObjects)
        {
            llOwnerSay("Maximum objects reached. Reset script to continue.");
            return;
        }
        
        vector myPos = llGetPos();
        vector offset = <llFrand(10.0)-5.0, llFrand(10.0)-5.0, llFrand(5.0)+2.0>;
        
        llRezObject("Object", myPos + offset, <0,0,0>, <0,0,0,1>, 1);
        count++;
        
        llSetText("Created: " + (string)count + "/" + (string)maxObjects, <0,1,0>, 1.0);
        
        if(count < maxObjects)
        {
            llSetTimerEvent(2.0); // Auto-create every 2 seconds
        }
    }
    
    timer()
    {
        llSetTimerEvent(0.0);
        // Trigger creation again
        touch_start(1);
    }
    
    object_rez(key id)
    {
        // Configure the newly rezzed object
        llGiveInventory(id, "Basic Destruction Test Script");
    }
}
```

---

## 🧪 Test Procedures

### Phase 1: Basic Functionality Tests

#### Test 1.1: Object Creation and Registration
**Steps:**
1. Create a cube primitive
2. Add the "Basic Destruction Test Script"
3. Touch the object to make it destructible

**Console Commands:**
```
destruction list
destruction status
```

**Expected Results:**
- Object appears in destruction list
- Status shows object as registered
- No error messages in console

#### Test 1.2: Simple Destruction
**Steps:**
1. Use a weapon/vehicle to impact the destructible object
2. Observe destruction effects

**Console Commands:**
```
destruction stats
destruction list
```

**Expected Results:**
- Object destroys with fragments
- Particle effects visible
- Statistics updated
- Fragments cleanup after timeout

#### Test 1.3: Zone System Testing
**Console Commands:**
```
destruction zone create "SafeZone" safe <100,100,25> <150,150,30>
destruction zone create "DamageZone" highdamage <200,200,25> <250,250,30>
destruction zone list
```

**Steps:**
1. Place destructible objects in safe zone - should not destroy
2. Place destructible objects in damage zone - should destroy easily
3. Test objects outside zones - normal destruction

**Expected Results:**
- Safe zone prevents destruction
- Damage zone increases destruction effects
- Normal areas work as expected

### Phase 2: Repair System Tests

#### Test 2.1: Manual Repair
**Steps:**
1. Destroy several objects
2. Use repair commands to restore them

**Console Commands:**
```
repair list
repair start <object_id> 1.0
repair status
```

**Expected Results:**
- Destroyed objects listed for repair
- Repair process completes successfully
- Objects restored to original state

#### Test 2.2: Auto-Repair Testing
**Console Commands:**
```
repair auto enable 30
repair config AutoRepairEnabled true
```

**Steps:**
1. Destroy objects with auto-repair enabled
2. Wait for auto-repair timeout
3. Verify automatic restoration

**Expected Results:**
- Objects automatically repair after delay
- Repair quality slightly reduced (0.9)
- Status updates correctly

### Phase 3: Persistence Tests

#### Test 3.1: Data Persistence
**Steps:**
1. Create and destroy several objects
2. Start some repair jobs
3. Restart the OpenSim server
4. Verify data restoration

**Console Commands:**
```
# Before restart:
repair save
destruction stats

# After restart:
repair load
repair list
destruction list
```

**Expected Results:**
- Destruction records persist across restart
- Active repair jobs resume
- No data loss

#### Test 3.2: Persistence Management
**Console Commands:**
```
repair save
repair purge 1
repair save
```

**Expected Results:**
- Manual save/load works correctly
- Old data purges successfully
- No file corruption or errors

### Phase 4: Performance & Stress Tests

#### Test 4.1: Multiple Simultaneous Destructions
**Steps:**
1. Create 20+ destructible objects using stress test script
2. Trigger simultaneous destruction (explosion/mass collision)
3. Monitor performance metrics

**Console Commands:**
```
destruction stats
destruction performance
show stats
```

**Expected Results:**
- Frame rates remain acceptable (>20 FPS)
- No memory leaks or crashes
- Fragments cleanup properly
- Performance metrics within acceptable ranges

#### Test 4.2: Memory Leak Detection
**Steps:**
1. Run continuous destruction/repair cycles for 30+ minutes
2. Monitor memory usage
3. Check for garbage collection efficiency

**Console Commands:**
```
show stats
destruction stats
repair cleanup
```

**Expected Results:**
- Memory usage stable over time
- No continuous memory growth
- Garbage collection working effectively

### Phase 5: Error Handling & Edge Cases

#### Test 5.1: Invalid Parameters
**Console Commands:**
```
destruction create "test" invalid_material <0,0,0> <0,0,0> <1,1,1> 1000
repair start 999999 2.0
destruction zone create "test" invalidtype <0,0,0> <10,10,10>
```

**Expected Results:**
- Graceful error handling
- Clear error messages
- No crashes or exceptions

#### Test 5.2: Resource Exhaustion
**Steps:**
1. Create maximum number of objects
2. Test system behavior at limits
3. Verify cleanup mechanisms

**Expected Results:**
- System prevents over-allocation
- Graceful degradation under stress
- Automatic cleanup when needed

---

## 📊 Performance Benchmarks

### Acceptable Performance Targets

| Metric | Target | Critical Threshold |
|--------|--------|--------------------|
| Frame Rate | >20 FPS | <15 FPS |
| Memory Usage | <+50MB over 30min | >+200MB |
| Fragment Count | <200 active | >500 active |
| Destruction Latency | <2 seconds | >5 seconds |
| Repair Time | <60 seconds | >300 seconds |

### Monitoring Commands
```bash
# Performance monitoring
show stats
destruction performance
destruction stats

# Memory monitoring  
show gc
show memory

# Fragment monitoring
destruction list fragments
destruction cleanup
```

---

## ✅ Test Completion Checklist

### Basic Functionality
- [ ] Object registration works
- [ ] Destruction triggers correctly  
- [ ] Fragments generate and cleanup
- [ ] Particle effects display
- [ ] Sound effects play

### Zone System
- [ ] Safe zones prevent destruction
- [ ] High-damage zones amplify effects
- [ ] Zone boundaries work correctly
- [ ] Parcel-specific rules apply

### Repair System
- [ ] Manual repairs complete successfully
- [ ] Auto-repair functions correctly
- [ ] Repair quality settings work
- [ ] Repair permissions enforced

### Persistence
- [ ] Data saves correctly
- [ ] Data loads after restart
- [ ] No corruption or data loss
- [ ] Cleanup functions work

### Performance
- [ ] No memory leaks detected
- [ ] Frame rates remain acceptable
- [ ] CPU usage reasonable
- [ ] No deadlocks or hangs

### Error Handling
- [ ] Invalid input handled gracefully
- [ ] Resource limits enforced
- [ ] Clear error messages provided
- [ ] System remains stable under stress

---

## 🐛 Issue Reporting Template

When issues are found, document them using this template:

```
**Issue:** Brief description
**Severity:** Critical/High/Medium/Low
**Steps to Reproduce:**
1. Step one
2. Step two
3. Step three

**Expected Result:** What should happen
**Actual Result:** What actually happened
**Console Output:** Any error messages
**Performance Impact:** FPS/memory impact if applicable
**Workaround:** Temporary solution if known
```

---

## 📈 Success Criteria

The testing phase is considered successful when:

1. **All basic functionality tests pass** without errors
2. **Performance targets are met** under normal and stress conditions  
3. **No critical issues** are identified
4. **Data persistence works reliably** across restarts
5. **Error handling is robust** for edge cases
6. **Memory usage is stable** over extended testing periods

---

## 🎯 Post-Testing Actions

After successful testing completion:

1. **Document any configuration optimizations** discovered
2. **Update user documentation** with validated procedures
3. **Create deployment checklist** for production use
4. **Plan maintenance schedule** for ongoing monitoring
5. **Identify areas for future enhancement**

---

*Happy Testing! 🧪*