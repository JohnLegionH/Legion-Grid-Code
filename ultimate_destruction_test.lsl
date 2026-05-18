// Ultimate Destruction Test - Enhanced Particles + Sound Effects
// Shows dramatic visual and audio differences between materials

integer currentMaterial = 0;
list materialNames = ["GLASS", "METAL", "WOOD", "STONE", "CONCRETE"];
list materialTypes = [MATERIAL_GLASS, MATERIAL_METAL, MATERIAL_WOOD, MATERIAL_STONE, MATERIAL_CONCRETE];
list thresholds = [5.0, 8.0, 6.0, 9.0, 12.0]; // Different breaking thresholds
list descriptions = [
    "💎 Bright blue sparkles, fast shards",
    "⚡ Orange sparks, metallic clang", 
    "🪵 Brown chunks, wood crack sound",
    "🗿 Gray dust cloud, stone rumble",
    "🏗️ Massive dust, concrete crash"
];

default
{
    state_entry()
    {
        llSay(0, "=== ULTIMATE DESTRUCTION TEST ===");
        llSay(0, "Enhanced particles + sound effects!");
        llSay(0, "Touch to cycle materials - Listen and watch!");
        
        // Start with glass
        SetupMaterial(0);
    }
    
    touch_start(integer total_number)
    {
        // Cycle to next material
        currentMaterial = (currentMaterial + 1) % 5;
        SetupMaterial(currentMaterial);
    }
    
    collision_start(integer num_detected)
    {
        string matName = llList2String(materialNames, currentMaterial);
        float threshold = llList2Float(thresholds, currentMaterial);
        string desc = llList2String(descriptions, currentMaterial);
        
        llSay(0, "*** " + matName + " IMPACT! ***");
        
        float impulse = llGetCollisionImpulse();
        llSay(0, "Force: " + (string)impulse + " | Need: " + (string)threshold);
        
        if (impulse > threshold)
        {
            llSay(0, "💥💥💥 DESTRUCTION! 💥💥💥");
            llSay(0, "Watch/Listen: " + desc);
            llSetText("BREAKING!\n" + matName + "\n" + desc, <1,0,0>, 1.0);
            
            // Flash the object red briefly when breaking
            llSetColor(<1,0,0>, ALL_SIDES);
            llSetTimerEvent(0.5); // Reset color after 0.5 seconds
        }
        else
        {
            llSay(0, "Hit but need more force!");
            llSetText("IMPACT: " + (string)impulse + "\nNeed: " + (string)threshold + "\n" + matName, <1,1,0>, 1.0);
        }
    }
    
    timer()
    {
        llSetTimerEvent(0.0); // Stop timer
        SetupMaterial(currentMaterial); // Reset to normal color
    }
}

SetupMaterial(integer materialIndex)
{
    string matName = llList2String(materialNames, materialIndex);
    integer matType = llList2Integer(materialTypes, materialIndex);
    float threshold = llList2Float(thresholds, materialIndex);
    string desc = llList2String(descriptions, materialIndex);
    
    llSay(0, "\n=== " + matName + " MATERIAL READY ===");
    
    // Set material properties for enhanced effects
    if (matType == MATERIAL_GLASS)
    {
        llSetPhysicsMaterial(matType, 2.5, 0.1, 0.9); // Light, slippery, bouncy
    }
    else if (matType == MATERIAL_METAL)
    {
        llSetPhysicsMaterial(matType, 7.8, 0.3, 0.2); // Heavy, medium friction, low bounce
    }
    else if (matType == MATERIAL_WOOD)
    {
        llSetPhysicsMaterial(matType, 0.6, 0.4, 0.3); // Light, medium friction, some bounce
    }
    else if (matType == MATERIAL_STONE)
    {
        llSetPhysicsMaterial(matType, 2.7, 0.5, 0.1); // Medium weight, high friction, no bounce
    }
    else // CONCRETE
    {
        llSetPhysicsMaterial(matType, 2.4, 0.6, 0.05); // Heavy, very high friction, no bounce
    }
    
    // Set destruction parameters
    llSetDestructible(TRUE, threshold, FRACTURE_RANDOM);
    
    // Material-specific colors and effects
    vector color;
    vector glow = <0,0,0>;
    
    if (matType == MATERIAL_GLASS) 
    {
        color = <0.8, 0.9, 1.0>;      // Light blue glass
        glow = <0.1, 0.1, 0.2>;       // Slight blue glow
    }
    else if (matType == MATERIAL_METAL) 
    {
        color = <0.7, 0.7, 0.8>;      // Metallic silver
        glow = <0.05, 0.05, 0.1>;     // Slight metallic glow
    }
    else if (matType == MATERIAL_WOOD) 
    {
        color = <0.6, 0.4, 0.2>;      // Rich brown wood
        glow = <0,0,0>;               // No glow for wood
    }
    else if (matType == MATERIAL_STONE) 
    {
        color = <0.6, 0.6, 0.5>;      // Gray stone
        glow = <0,0,0>;               // No glow for stone
    }
    else // CONCRETE
    {
        color = <0.5, 0.5, 0.5>;      // Dark concrete gray
        glow = <0,0,0>;               // No glow for concrete
    }
    
    // Apply visual properties
    llSetColor(color, ALL_SIDES);
    llSetText(matName + " TARGET\n" + desc + "\nThreshold: " + (string)threshold, color, 1.0);
    
    // Add glow for glass and metal
    if (glow != <0,0,0>)
    {
        llSetPrimitiveParams([PRIM_GLOW, ALL_SIDES, glow.x]);
    }
    else
    {
        llSetPrimitiveParams([PRIM_GLOW, ALL_SIDES, 0.0]);
    }
    
    llSay(0, "✓ " + matName + " configured");
    llSay(0, "Expected: " + desc);
    llSay(0, "Threshold: " + (string)threshold + " force units");
    llSay(0, "Touch to change material, hit with hammer to test!");
}