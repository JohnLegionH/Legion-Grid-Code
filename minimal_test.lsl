// Ultra basic test - just to see if functions exist

default
{
    state_entry()
    {
        llSay(0, "Testing basic LSL function...");
        llSetText("BASIC TEST\nTouch me", <1,1,0>, 1.0);
    }
    
    touch_start(integer total_number)
    {
        llSay(0, "Testing llSetDestructible...");
        llSetDestructible(TRUE, 10.0, 1);
        llSay(0, "Function called successfully!");
        
        llSay(0, "Testing llGetCollisionImpulse...");
        float impulse = llGetCollisionImpulse();
        llSay(0, "Impulse: " + (string)impulse);
    }
}