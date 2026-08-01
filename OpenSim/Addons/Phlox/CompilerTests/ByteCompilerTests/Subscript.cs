using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using NUnit.Framework;
using Antlr.Runtime;
using InWorldz.Phlox.VM;
using OpenMetaverse;

namespace CompilerTests.ByteCompilerTests
{
    [TestFixture]
    class Subscript : BaseTest
    {
        [Test]
        public void TestSubscriptAccess()
        {
            string test = @"
                            .globals 2
                            .statedef default
                            vconst <1.1,1.2,1.3>
                            gstore 0
                            gload.sub 0,0
                            trace
                            gload.sub 0,1
                            trace
                            gload.sub 0,2
                            trace
                            rconst <1.1,1.2,1.3,1.4>
                            gstore 1
                            gload.sub 1,0
                            trace
                            gload.sub 1,1
                            trace
                            gload.sub 1,2
                            trace
                            gload.sub 1,3
                            trace
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ";

            Compiler.Compile(new ANTLRStringStream(test));
            CompiledScript script = Compiler.Result;
            Assert.IsNotNull(script);

            Interpreter i = new Interpreter(script, null);
            i.TraceDestination = Listener.TraceDestination;
            while (i.ScriptState.RunState == RuntimeState.Status.Running)
            {
                i.Tick();
            }

            Assert.IsTrue(i.ScriptState.Operands.Count == 0); //stack should be empty
            Console.WriteLine(Listener.TraceDestination.ToString());
            Assert.IsTrue(Listener.MessagesContain($"1.1{Environment.NewLine}1.2{Environment.NewLine}1.3{Environment.NewLine}1.1{Environment.NewLine}1.2{Environment.NewLine}1.3{Environment.NewLine}1.4"));
        }

        [Test]
        public void TestSubscriptIncrement()
        {
            string test = @"
                            .globals 1
                            .statedef default
                            vconst <1.1,1.2,1.3>
                            gstore 0
                            fpreinc.g.sub 0,1
                            trace
                            gload 0
                            trace
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ";

            Compiler.Compile(new ANTLRStringStream(test));
            CompiledScript script = Compiler.Result;
            Assert.IsNotNull(script);

            Interpreter i = new Interpreter(script, null);
            i.TraceDestination = Listener.TraceDestination;
            while (i.ScriptState.RunState == RuntimeState.Status.Running)
            {
                i.Tick();
            }

            Assert.IsTrue(i.ScriptState.Operands.Count == 0); //stack should be empty
            Console.WriteLine(Listener.TraceDestination.ToString());
            Assert.IsTrue(Listener.MessagesContain($"2.2{Environment.NewLine}<1.1, 2.2, 1.3>"));
        }

        [Test]
        public void TestSubscriptIntCoercion()
        {
            // vec.z = 10;  -- assigning an INT to a vector component must COERCE to float, not throw
            // "Unable to cast object of type 'System.Int32' to type 'System.Single'" (the _AssignSubscript
            // hard-cast bug). Pre-fix, the gstore.sub below threw during Tick(); post-fix ConvToFloat coerces.
            string test = @"
                            .globals 1
                            .statedef default
                            vconst <1.1,1.2,1.3>
                            gstore 0
                            iconst 10
                            gstore.sub 0,2
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ";

            Compiler.Compile(new ANTLRStringStream(test));
            CompiledScript script = Compiler.Result;
            Assert.IsNotNull(script);

            Interpreter i = new Interpreter(script, null);
            i.TraceDestination = Listener.TraceDestination;
            while (i.ScriptState.RunState == RuntimeState.Status.Running)
            {
                i.Tick();   // pre-fix: throws InvalidCastException here on the int -> vector component store
            }

            Assert.IsTrue(i.ScriptState.Operands.Count == 0);
            Vector3 v = (Vector3)i.ScriptState.Globals[0];
            Assert.AreEqual(10.0f, v.Z);              // int 10 coerced to float 10.0 in the component
            Assert.AreEqual(1.1f, v.X, 0.0001f);      // float components pass through unchanged
            Assert.AreEqual(1.2f, v.Y, 0.0001f);
        }

        [Test]
        public void TestRotationSubscriptIntCoercion()
        {
            // rot.s = 5;  -- same coercion on a rotation (quaternion) component (the 4 quaternion sites).
            string test = @"
                            .globals 1
                            .statedef default
                            rconst <1.1,1.2,1.3,1.4>
                            gstore 0
                            iconst 5
                            gstore.sub 0,3
                            halt

                            .evt default/state_entry: args=0, locals=0
                            ret
                            ";

            Compiler.Compile(new ANTLRStringStream(test));
            CompiledScript script = Compiler.Result;
            Assert.IsNotNull(script);

            Interpreter i = new Interpreter(script, null);
            i.TraceDestination = Listener.TraceDestination;
            while (i.ScriptState.RunState == RuntimeState.Status.Running)
            {
                i.Tick();   // pre-fix: threw on the int -> quaternion component store
            }

            Assert.IsTrue(i.ScriptState.Operands.Count == 0);
            Quaternion q = (Quaternion)i.ScriptState.Globals[0];
            Assert.AreEqual(5.0f, q.W);               // int 5 coerced to float 5.0 (component index 3 = W/s)
            Assert.AreEqual(1.1f, q.X, 0.0001f);      // other components unchanged
        }
    }
}
