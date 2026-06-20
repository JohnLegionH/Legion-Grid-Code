// PHLOX RUNTIME TEST 03 — Integer division/modulo sign
// SL EXPECTED:
//   -15 /  2 == -7   (truncate toward zero, not floor)
//    -7 %  3 == -1   (% takes sign of first operand)
//     7 % -3 ==  1
//
// Drop in a prim. Object Description should be: "DIV=-7 MOD1=-1 MOD2=1"
//
// STATIC-EVIDENCE PREDICTION: Op_Idiv and Op_Imod use C# / and % directly
// (Interpreter.Actions.cs lines 413-427). C# truncates toward zero and %
// takes sign of dividend, so this should be CONFORMANT.

default
{
    state_entry()
    {
        llSetObjectName("phlox-divmod-test");
        integer d  = -15 /  2;     // expect -7
        integer m1 = -7  %  3;     // expect -1
        integer m2 =  7  % -3;     // expect  1
        llSetObjectDesc("DIV=" + (string)d +
                        " MOD1=" + (string)m1 +
                        " MOD2=" + (string)m2);
    }
}
