// PHLOX RUNTIME TEST 05 — High-traffic list/string functions
// SL EXPECTED:
//   llGetListLength([1,2,3])                    == 3
//   llList2String([1, "a", 2.5], 1)             == "a"
//   llParseStringKeepNulls("a,,b", [","], [])   == ["a", "", "b"]   (length 3, middle empty)
//   llGetSubString("hello", -3, -1)              == "llo"
//
// Drop in a prim. Object Description should read:
//   "LEN=3 LIT=a EMPTY=3 SUB=llo"

default
{
    state_entry()
    {
        llSetObjectName("phlox-listfns-test");
        integer len  = llGetListLength([1, 2, 3]);
        string  lit  = llList2String([1, "a", 2.5], 1);
        list    sp   = llParseStringKeepNulls("a,,b", [","], []);
        integer emp  = llGetListLength(sp);                // expect 3 (a, "", b)
        string  sub  = llGetSubString("hello", -3, -1);    // expect "llo"
        llSetObjectDesc("LEN=" + (string)len +
                        " LIT=" + lit +
                        " EMPTY=" + (string)emp +
                        " SUB=" + sub);
    }
}
