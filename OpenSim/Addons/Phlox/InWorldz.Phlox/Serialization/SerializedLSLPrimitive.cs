using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;
using OpenMetaverse;

namespace InWorldz.Phlox.Serialization
{
    [ProtoContract]
    public class SerializedLSLPrimitive
    {
        public object Value;

        private T? Get<T>() where T : struct
        {
            return (Value != null && Value is T) ? (T?)Value : (T?)null;
        }

        [ProtoMember(1)]
        private int? ValueInt
        {
            get { return Get<int>(); }
            set { Value = value; }
        }

        [ProtoMember(2)]
        private float? ValueFloat
        {
            get { return Get<float>(); }
            set { Value = value; }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [ProtoMember(3, IsRequired=false)]
        [Obsolete]
        private string ValueVectorPbuf1Deprecated
        {
            get 
            { 
                return null; 
            }
            set 
            {
                if (value != null)
                {
                    Value = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [ProtoMember(4, IsRequired=false)]
        [Obsolete]
        private string ValueRotationPbuf1Deprecated
        {
            get 
            { 
                return null; 
            }
            set 
            {
                if (value != null)
                {
                    Value = Quaternion.Parse(value);
                }
            }
        }

        [ProtoMember(5)]
        private string ValueString
        {
            get { return (Value != null && Value is string) ? (string)Value : (string)null; }
            set { Value = value; }
        }

        [ProtoMember(6)]
        private SerializedLSLList ValueList
        {
            get { return (Value != null && Value is SerializedLSLList) ? (SerializedLSLList)Value : (SerializedLSLList)null; }
            set { Value = value; }
        }

        [ProtoMember(7)]
        private VM.FunctionInfo ValueFunction
        {
            get { return (Value != null && Value is VM.FunctionInfo) ? (VM.FunctionInfo)Value : (VM.FunctionInfo)null; }
            set { Value = value; }
        }

		[ProtoMember(8)]
        private SerializedVector3 ValueVector
        {
            get { return (Value != null && Value is Vector3 v) ? new SerializedVector3(v) : null; }
            set { if (value != null) Value = value.ToVector3(); }
        }
        [ProtoMember(9)]
        private SerializedQuaternion ValueRotation
        {
            get { return (Value != null && Value is Quaternion q) ? new SerializedQuaternion(q) : null; }
            set { if (value != null) Value = value.ToQuaternion(); }
        }
        /*
        [ProtoMember(8)]
        private Types.Sentinel ValueSentinel
        {
            get { return (Value != null && Value is Types.Sentinel) ? (Types.Sentinel)Value : (Types.Sentinel)null; }
            set { Value = value; }
        }*/

        public SerializedLSLPrimitive()
        {
        }

        public static SerializedLSLPrimitive FromPrimitive(object obj)
        {
            if (!(obj is Types.LSLList))
            {
                SerializedLSLPrimitive primitive = new SerializedLSLPrimitive();
                primitive.Value = obj;

                return primitive;
            }
            else
            {
                SerializedLSLPrimitive primitive = new SerializedLSLPrimitive();
                primitive.Value = SerializedLSLList.FromList((Types.LSLList)obj);
                return primitive;
            }
        }

        public static object[] ToPrimitiveList(SerializedLSLPrimitive[] serPrimList)
        {
            if (serPrimList == null)
            {
                return new object[0];
            }

            object[] primitiveList = new object[serPrimList.Length];

            for (int i = 0; i < serPrimList.Length; i++)
            {
                SerializedLSLPrimitive obj = serPrimList[i];

                if (!(obj.Value is SerializedLSLList))
                {
                    /*if (validate)
                    {
                        if (!obj.IsValid())
                        {
                            throw new SerializationException(
                                String.Format(
                                    "ToPrimitiveList: Unable to deserialize LSLPrimitive to object: Type: {0} Value: {1}",
                                    obj != null && obj.Value != null ? obj.Value.GetType().FullName : "null", obj));
                        }
                    }*/

                    primitiveList[i] = obj.Value;
                }
                else
                {
                    SerializedLSLList list = (SerializedLSLList)obj.Value;
                    primitiveList[i] = list.ToList();
                }
            }

            return primitiveList;
        }

        public static SerializedLSLPrimitive[] FromPrimitiveList(object[] primList)
        {
            SerializedLSLPrimitive[] serPrimList = new SerializedLSLPrimitive[primList.Length];

            for (int i = 0; i < primList.Length; i++)
            {
                object obj = primList[i];
                serPrimList[i] = SerializedLSLPrimitive.FromPrimitive(obj);

                /*if (validate)
                {
                    if (!serPrimList[i].IsValid())
                    {
                        throw new SerializationException(
                            String.Format(
                                "FromPrimitiveList: Unable to serialize object to SerializedLSLPrimitive: Type: {0} Value: {1}",
                                obj != null ? obj.GetType().FullName : "null", obj));

                    }
                }*/
            }

            return serPrimList;
        }

        public static SerializedLSLPrimitive[] FromPrimitiveStack(Stack<object> primStack)
        {
            SerializedLSLPrimitive[] serPrimList = new SerializedLSLPrimitive[primStack.Count];

            int i = 0;
            foreach (object obj in primStack)
            {
                serPrimList[i] = SerializedLSLPrimitive.FromPrimitive(obj);
                /*if (validate)
                {
                    if (!serPrimList[i].IsValid())
                    {
                        throw new SerializationException(
                            String.Format(
                                "FromPrimitiveStack: Unable to serialize object to SerializedLSLPrimitive: Type: {0} Value: {1}",
                                obj != null ? obj.GetType().FullName : "null", obj));

                    }
                }*/

                i++;
            }

            return serPrimList;
        }

        public static Stack<object> ToPrimitiveStack(SerializedLSLPrimitive[] serializedLSLPrimitive)
        {
            if (serializedLSLPrimitive == null)
            {
                return new Stack<object>();
            }

            //push the primitives back onto the stack in reverse order
            Stack<object> primStack = new Stack<object>(serializedLSLPrimitive.Length);
            for (int i = serializedLSLPrimitive.Length - 1; i >= 0; i--)
            {
                SerializedLSLPrimitive obj = serializedLSLPrimitive[i];

                if (!(obj.Value is SerializedLSLList))
                {
                    /*if (validate)
                    {
                        if (!obj.IsValid())
                        {
                            throw new SerializationException(
                                String.Format(
                                    "ToPrimitiveStack: Unable to deserialize LSLPrimitive to object: Type: {0} Value: {1}",
                                    obj != null && obj.Value != null ? obj.Value.GetType().FullName : "null", obj.Value));
                        }
                    }*/

                    primStack.Push(obj.Value);
                }
                else
                {
                    SerializedLSLList list = (SerializedLSLList)obj.Value;
                    primStack.Push(list.ToList());
                }
            }

            return primStack;
        }

        public bool IsValid()
        {
            if (Value == null)
                return false;

            if (Value is int)
                return true;

            if (Value is float)
                return true;

            if (Value is Vector3)
                return true;

            if (Value is Quaternion)
                return true;

            if (Value is string)
                return true;

            if (Value is SerializedLSLList)
                return true;

			if (Value is VM.FunctionInfo)
				return true;
            if (Value is SerializedStackFrame)
                return true;
            /*
            if (Value is Types.Sentinel)
                return true;
            */
            return false;
        }
    }

    [ProtoContract]
    public class SerializedVector3
    {
        [ProtoMember(1)] public float X;
        [ProtoMember(2)] public float Y;
        [ProtoMember(3)] public float Z;
        public SerializedVector3() { }
        public SerializedVector3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }
        public Vector3 ToVector3() => new Vector3(X, Y, Z);
    }

    [ProtoContract]
    public class SerializedQuaternion
    {
        [ProtoMember(1)] public float X;
        [ProtoMember(2)] public float Y;
        [ProtoMember(3)] public float Z;
        [ProtoMember(4)] public float W;
        public SerializedQuaternion() { }
        public SerializedQuaternion(Quaternion q) { X = q.X; Y = q.Y; Z = q.Z; W = q.W; }
        public Quaternion ToQuaternion() => new Quaternion(X, Y, Z, W);
    }
}