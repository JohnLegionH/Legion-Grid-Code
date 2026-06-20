// PHLOX RUNTIME TEST 02 — Pre vs post increment expression values
// SL EXPECTED:
//   integer count = 0; if (++count == 1) -> TRUE     (pre evaluates AFTER increment)
//   integer count = 0; if (count++ == 1) -> FALSE    (post evaluates BEFORE increment)
//
// Drop in a prim and check Object Description.
// PASS = "PRE=1 POST=0 PRESTATE=1 POSTSTATE=1"
//
// STATIC-EVIDENCE PREDICTION: Op_Ipreinc_l vs Op_Ipostinc_l are distinct ops
// in the VM (Interpreter.cs lines 290-303). Should behave correctly.

default
{
    state_entry()
    {
        llSetObjectName("phlox-preinc-test");

        integer count = 0;
        integer preResult  = (++count == 1);   // expect 1
        integer preState   = count;            // expect 1

        count = 0;
        integer postResult = (count++ == 1);   // expect 0
        integer postState  = count;            // expect 1

        llSetObjectDesc("PRE=" + (string)preResult +
                        " POST=" + (string)postResult +
                        " PRESTATE=" + (string)preState +
                        " POSTSTATE=" + (string)postState);
    }
}
