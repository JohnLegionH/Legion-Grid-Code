using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// Serialized version of a VM stackframe
    /// </summary>
    [ProtoContract]
    public class SerializedStackFrame
    {
        [ProtoMember(1)]
        public VM.FunctionInfo FunctionInfo;

        [ProtoMember(2)]
        public int ReturnAddress;

        [ProtoMember(3)]
        public SerializedLSLPrimitive[] Locals;

        [ProtoMember(4)]
        public SerializedClosure Closure; // set when this frame is executing a closure (upvalue access)

        public SerializedStackFrame()
        {
        }

        public static SerializedStackFrame FromStackFrame(VM.StackFrame frame)
        {
            if (frame == null) return null;

            SerializedStackFrame serFrame = new SerializedStackFrame();
            serFrame.FunctionInfo = frame.FunctionInfo;

            serFrame.ReturnAddress = frame.ReturnAddress;

            serFrame.Locals = new SerializedLSLPrimitive[frame.Locals.Length];
            for (int i = 0; i < serFrame.Locals.Length; i++)
            {
                serFrame.Locals[i] = SerializedLSLPrimitive.FromPrimitive(frame.Locals[i]);
            }

            serFrame.Closure = (frame.Closure != null) ? SerializedClosure.From(frame.Closure) : null;

            return serFrame;
        }

        public VM.StackFrame ToStackFrame()
        {
            VM.StackFrame frame = new VM.StackFrame(this.FunctionInfo, this.ReturnAddress);
            frame.Locals = SerializedLSLPrimitive.ToPrimitiveList(this.Locals);
            frame.Closure = (this.Closure != null) ? this.Closure.ToClosure() : null;

            return frame;
        }
    }
}
