using System.Collections.Generic;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// Protobuf serialization wrapper for <see cref="Types.LSLTable"/>, modeled on
    /// <see cref="SerializedLSLList"/>. Stores entries as ordered parallel key/value lists so the
    /// table's iteration order is preserved across a round-trip (deterministic resume mid-pairs).
    ///
    /// Unlike SerializedLSLList (whose elements are flat LSL primitives that never nest), table
    /// values can be tables or lists, so each key and value is wrapped via
    /// <see cref="SerializedLSLPrimitive.FromPrimitive"/> and rebuilt via
    /// <see cref="SerializedLSLPrimitive.ResolveValue"/> — giving full recursive nesting.
    /// </summary>
    [ProtoContract]
    public class SerializedLSLTable
    {
        [ProtoMember(1)]
        public List<SerializedLSLPrimitive> Keys;

        [ProtoMember(2)]
        public List<SerializedLSLPrimitive> Values;

        public SerializedLSLTable()
        {
        }

        public static SerializedLSLTable FromTable(Types.LSLTable table)
        {
            SerializedLSLTable s = new SerializedLSLTable();
            s.Keys = new List<SerializedLSLPrimitive>(table.Count);
            s.Values = new List<SerializedLSLPrimitive>(table.Count);

            foreach (object k in table.OrderedKeys)
            {
                s.Keys.Add(SerializedLSLPrimitive.FromPrimitive(k));
                s.Values.Add(SerializedLSLPrimitive.FromPrimitive(table.Get(k)));
            }

            return s;
        }

        public Types.LSLTable ToTable()
        {
            int n = (Keys != null) ? Keys.Count : 0;
            object[] keys = new object[n];
            object[] vals = new object[n];

            for (int i = 0; i < n; i++)
            {
                keys[i] = SerializedLSLPrimitive.ResolveValue(Keys[i].Value);
                vals[i] = SerializedLSLPrimitive.ResolveValue(Values[i].Value);
            }

            return new Types.LSLTable(keys, vals);
        }
    }
}
