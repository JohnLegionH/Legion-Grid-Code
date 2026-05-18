// Enhanced Destruction Test with Particle Effects
// Shows off different material types and their particle effects

integer currentMaterial = 0;
list materialNames = ["GLASS", "METAL", "WOOD", "STONE", "CONCRETE"];
list materialTypes = [MATERIAL_GLASS, MATERIAL_METAL, MATERIAL_WOOD, MATERIAL_STONE, MATERIAL_CONCRETE];
list thresholds = [5.0, 10.0, 7.0, 8.0, 12.0]; // Different breaking thresholds

default
{
    state_entry()
    {
        llSay(0, "=== PARTICLE DESTRUCTION TEST ===");
        llSay(0, "Touch to cycle through materials and see different particle effects!");
        
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
        
        llSay(0, "*** " + matName + " OBJECT HIT! ***");
        
        float impulse = llGetCollisionImpulse();
        llSay(0, "Impact force: " + (string)impulse + " (threshold: " + (string)threshold + ")");
        
        if (impulse > threshold)
        {
            llSay(0, "💥 BREAKING! Watch for " + matName + " particle effects!");
            llSetText("BREAKING!\n" + matName + " particles!", <1,0,0>, 1.0);
        }
        else
        {
            llSay(0, "Hit but not hard enough - need force > " + (string)threshold);
            llSetText("HIT: " + (string)impulse + "\nNeed: " + (string)threshold + "\n" + matName, <1,1,0>, 1.0);
        }
    }
}

SetupMaterial(integer materialIndex)
{
    string matName = llList2String(materialNames, materialIndex);
    integer matType = llList2Integer(materialTypes, materialIndex);
    float threshold = llList2Float(thresholds, materialIndex);
    
    llSay(0, "\n=== SETTING UP " + matName + " ===");
    
    // Set material properties
    llSetPhysicsMaterial(matType, 2.0, 0.3, 0.5);
    llSay(0, "✓ Material: " + matName);
    
    // Set destruction parameters
    llSetDestructible(TRUE, threshold, FRACTURE_RANDOM);
    llSay(0, "✓ Destructible: YES, threshold " + (string)threshold);
    
    // Update display
    vector color;
    if (matType == MATERIAL_GLASS) color = <0.8, 0.9, 1.0>;      // Light blue
    else if (matType == MATERIAL_METAL) color = <0.7, 0.7, 0.8>; // Metallic
    else if (matType == MATERIAL_WOOD) color = <0.6, 0.4, 0.2>;  // Brown
    else if (matType == MATERIAL_STONE) color = <0.6, 0.6, 0.5>; // Gray
    else color = <0.5, 0.5, 0.5>; // Concrete gray
    
    llSetText(matName + " TARGET\nThreshold: " + (string)threshold + "\nTouch to change", color, 1.0);
    
    // Color the object to match material
    llSetColor(color, ALL_SIDES);
    
    llSay(0, "Ready! Hit with TestHammer to see " + matName + " particle effects!");
    llSay(0, "Touch me to cycle to next material type.");
}