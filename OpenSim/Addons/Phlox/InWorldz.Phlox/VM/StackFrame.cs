using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.VM
{
    public class StackFrame
    {
        public const int MemSize = FunctionInfo.MemSize + 8;

        public FunctionInfo FunctionInfo;
        public int ReturnAddress;
        public object[] Locals;

        // The closure executing in this frame (null for plain named functions/events). Supplies the
        // upvalue cells for the getupval/setupval opcodes. Set by callv.
        public Types.LuaClosure Closure;

        public StackFrame(FunctionInfo funcInfo, int returnAddress)
        {
            FunctionInfo = funcInfo;
            ReturnAddress = returnAddress;

            int totalLocals = funcInfo.NumberOfArguments + funcInfo.NumberOfLocals;
            Locals = new object[totalLocals];
            
            //fill with sentinel values for null reference checks
            /*for (int i = 0; i < totalLocals; i++)
            {
                Locals[i] = Types.Sentinel.Instance;
            }*/
        }
    }
}
