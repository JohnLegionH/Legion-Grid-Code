// PHLOX RUNTIME TEST 01 — Short-circuit evaluation
// SL EXPECTED: Math Error (Script run-time error: Math Error).
// SL semantics: && and || ALWAYS evaluate both operands. So 1/x with x=0
// should throw even though FALSE && (...) would logically be FALSE.
//
// Drop this in a prim and check the script-run-time error console.
//   * PASS = "Math Error" shows up
//   * FAIL = no error AND script keeps running -> Phlox short-circuited (non-conformant)
//
// STATIC-EVIDENCE PREDICTION (from Interpreter.Actions.cs::Op_Iland):
//   Op_Iland pops BOTH operands before evaluating -> non-short-circuit
//   -> Phlox should match SL here. This script confirms the static read.

default
{
    state_entry()
    {
        llSetObjectName("phlox-shortcircuit-test");
        llSetObjectDesc("BEFORE");

        integer x = 0;
        integer dummy;

        // If short-circuited, dummy stays 0 and we set desc to "AFTER".
        // If not, the inner 1/x throws Math Error and AFTER is never set.
        dummy = (FALSE && (1 / x));

        llSetObjectDesc("AFTER");
    }
}
