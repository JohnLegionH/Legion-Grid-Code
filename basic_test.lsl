// Most Basic Test - Just check if LSL is working at all

default
{
    state_entry()
    {
        llSay(0, "BASIC TEST: Script started successfully");
        llSay(0, "Touch me to test basic functionality");
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "BASIC TEST: Touch detected - LSL is working!");
        llSay(0, "Trying to create a simple object...");
        
        // Try the most basic object creation
        llRezObject("Object", llGetPos() + <1, 0, 1>, ZERO_VECTOR, ZERO_ROTATION, 0);
    }
    
    object_rez(key id)
    {
        llSay(0, "BASIC TEST: Object created successfully! ID: " + (string)id);
        llSetText("I was created by LSL!", <1,1,1>, 1.0);
        llSetColor(<0,1,0>, ALL_SIDES);
    }
}