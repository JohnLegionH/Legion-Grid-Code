// PHLOX RUNTIME TEST 04 — llRemoveInventory(llGetScriptName()) self-removal
// SL EXPECTED behavior when a script removes itself:
//   1. The "BEFORE" line is logged.
//   2. llRemoveInventory returns; script execution does NOT continue past it.
//   3. The "AFTER" line is NEVER logged.
//   4. The script disappears from prim inventory.
//   5. After a region restart, the script does NOT reappear.
//
// To test:
//   a) Drop this script in a prim.
//   b) Touch the prim.
//   c) Watch the prim's Object Description for two updates.
//        - Description "BEFORE" -> Phase 1 confirmed.
//        - If Description ever becomes "AFTER", Phlox did NOT halt -> FAIL.
//   d) Open prim contents — script should be gone.
//   e) Restart the region. Script should still be gone (not respawn).
//
// This is the open behavioural item from current debugging.
//
// IMPORTANT: this script triggers from touch_start so you can place it
// without immediate self-deletion.

default
{
    state_entry()
    {
        llSetObjectName("phlox-self-remove-test");
        llSetObjectDesc("READY-touch-to-fire");
    }

    touch_start(integer total_number)
    {
        llSetObjectDesc("BEFORE");
        llSleep(1.0);                            // give viewer time to render
        llRemoveInventory(llGetScriptName());
        // If Phlox is SL-conformant, the next line never runs.
        llSetObjectDesc("AFTER");
    }
}
