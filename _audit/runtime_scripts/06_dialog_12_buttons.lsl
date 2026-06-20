// PHLOX RUNTIME TEST 06 — llDialog with the full 12-button list
// SL EXPECTED: opens a dialog to the toucher with twelve buttons rendered.
// Phlox is conformant if (a) all 12 buttons appear AND (b) the listen on
// channel -42 fires with the chosen text.
//
// Drop in a prim, touch it, observe dialog and chat output ("CHOSE: Btn07").

integer chan = -42;
integer hl;

default
{
    state_entry()
    {
        llSetObjectName("phlox-dialog-test");
        hl = llListen(chan, "", NULL_KEY, "");
    }

    touch_start(integer total_number)
    {
        llDialog(llDetectedKey(0), "Pick a button:",
            ["Btn01","Btn02","Btn03","Btn04","Btn05","Btn06",
             "Btn07","Btn08","Btn09","Btn10","Btn11","Btn12"], chan);
    }

    listen(integer channel, string name, key id, string message)
    {
        llOwnerSay("CHOSE: " + message);
    }
}
