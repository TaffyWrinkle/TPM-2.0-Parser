﻿/*++

Copyright (c) 2010-2015 Microsoft Corporation
Microsoft Confidential

*/
using System;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Reflection;
using System.IO;
using System.Xml.Serialization;
using System.Xml;
using System.Xml.Schema;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace Tpm2Lib
{
    
    /// <summary>
    /// Abstract base class for all TPM structures. All TpmStructureBase derived classes must implement the ToNet and ToHost
    /// methods. These will recursively marshal all contained elements.
    /// </summary>
    [DataContract]
    public abstract partial class TpmStructureBase
    {
        protected TpmStructureBase()
        {
        }

        public byte[] GetTpmRepresentation()
        {
            var m = new Marshaller();
            ToNet(m);
            return m.GetBytes();
        }

        public byte[] GetTpm2BRepresentation()
        {
            return Marshaller.ToTpm2B(GetTpmRepresentation());
        }

        public override String ToString()
        {
            var p = new TpmStructPrinter();
            p.PrintName(GetType().ToString());
            ToStringInternal(p);
            return p.ToString();
        }

        /// <summary>
        /// Implicit conversion of a TPM data structure to byte-array by means of
        /// marshaling it to the TPM's network representation.
        /// </summary>
        public static implicit operator byte[] (TpmStructureBase src)
        {
            return src.GetTpmRepresentation();
        }

        public static bool operator == (TpmStructureBase lhs, TpmStructureBase rhs)
        {
            if ((Object)lhs == null)
            {
                return (Object)rhs == null;  
            }
            return lhs.Equals(rhs);
        }

        public static bool operator != (TpmStructureBase lhs, TpmStructureBase rhs)
        {
            if ((Object)lhs == null)
            {
                return (Object)rhs != null;  
            }
            return !lhs.Equals(rhs);
        }

        public override bool Equals(Object obj)
        {
            if (obj == null || this.GetType() != obj.GetType())
            {
                return false;
            }
            byte[] b0 = this.GetTpmRepresentation(),
                   b1 = ((TpmStructureBase)obj).GetTpmRepresentation();
            return Globs.ArraysAreEqual(b0, b1);
        }

        public override int GetHashCode()
        {
            byte[] objectData = GetTpmRepresentation();
            return  BitConverter.ToInt32(objectData.Length <= sizeof(int) ? objectData : CryptoLib.HashData(TpmAlgId.Sha1, objectData), 0);
        }

        public void Copy()
        {
            throw new Exception("TpmStructureBase.Copy(): Should not be here");
        }

        static Dbg dbg = new Dbg(false);

        /// <summary>
        /// Reflection and additional information associated with a TPM structure member.
        /// </summary>
        class TpmStructMemberInfo
        {
            /// <summary>
            /// Reflection information associated with this structure member.
            /// </summary>
            MemberInfo                  Info;

            /// <summary>
            /// Reference to the info of the selector or size tag field associated with this member.
            /// </summary>
            public TpmStructMemberInfo  Tag;

            /// <summary>
            /// Marshaling attribute of this structure member.
            /// </summary>
            public MarshalType          WireType;

            /// <summary>
            /// (Unmarshaled) Value of this structure member.
            /// </summary>
            public object               Value;

            public int                  SizeLength;
            public string               SizeName;

            public TpmStructMemberInfo (MemberInfo mi)
            {
                Info = mi;
                WireType = MarshalType.Normal;
            }

            public static implicit operator TpmStructMemberInfo (MemberInfo mi)
            {
                return new TpmStructMemberInfo(mi);
            }

            public static implicit operator MemberInfo (TpmStructMemberInfo tsmi)
            {
                return tsmi.Info;
            }

            public string Name
            {
                get { return Info.Name; }
            }

            public uint GetValueAsUInt()
            {
                Type t = Globs.GetMemberType(this);
                return t == typeof(byte) ? (uint)(byte)Value
                                            : t == typeof(ushort) ? (uint)(ushort)Value : (uint)Value;
            }
        } // class TpmStructMemberInfo

        TpmStructMemberInfo[] GetFieldsToMarshal(bool trackTags = false)
        {
            var t = GetType();
            string caption = (trackTags ? "Unmarshaling" : "Marshaling") + " " + t.Name;
            if (!t.GetTypeInfo().IsValueType)
            {
                var b = t.GetTypeInfo().BaseType;
                if (b != null && b != typeof(TpmStructureBase) && b != typeof(object))
                {
                    t = b;
                    caption += " as " + b.Name;
                }
            }
            dbg.Trace(caption);
            dbg.Indent();
            var members = new SortedDictionary<int, TpmStructMemberInfo>();
            Dictionary<string, TpmStructMemberInfo> tags = null;
            //Dictionary<string, TpmStructMemberInfo> untaggedFields = null;
            if (trackTags)
            {
                tags = new Dictionary<string, TpmStructMemberInfo>();
                //untaggedFields = new Dictionary<string, TpmStructMemberInfo>();
            }
            foreach (var bf in new BindingFlags[] {BindingFlags.Public | BindingFlags.NonPublic})
            {
                var candidateMembers = t.GetMembers(BindingFlags.Instance | bf);
                foreach (var mi in candidateMembers)
                {
                    var memberAttrs = mi.CustomAttributes;
                    foreach (var a in memberAttrs)
                    {
                        if (a.AttributeType.Name != "MarshalAsAttribute")
                        {
                            continue;
                        }
                        int idx = 0;
                        var arg0 = a.ConstructorArguments[0];
                        if (arg0.ArgumentType == typeof(int))
                        {
                            idx = (int)arg0.Value;
                        }
                        else
                        {
                            // The only variant of the marshaling attribute with
                            // a non-int first argument:
                            //     arg0.ArgumentType == typeof(Type))
                            // is used for types only, and never for structure fields.
                            Debug.Assert(false);
                        }

                        members.Add(idx, mi);

                        var tsmi = members[idx];
                        var arg1 = a.ConstructorArguments[1];
                        Debug.Assert(arg1.ArgumentType == typeof(MarshalType));
                        var mt = (MarshalType)arg1.Value;
                        Debug.Assert(mt != MarshalType.ArrayCount && mt != MarshalType.LengthOfStruct);

                        tsmi.WireType = mt;
                        if (mt == MarshalType.VariableLengthArray || mt == MarshalType.SizedStruct)
                        {
                            tsmi.SizeName = (string)a.ConstructorArguments[2].Value;
                            tsmi.SizeLength = (int)a.ConstructorArguments[3].Value;
                            dbg.Trace("Preproc " + (mt == MarshalType.SizedStruct ? "Struct " : "Array ")
                                      + mi.Name + " with size tag " + tsmi.SizeName + "=" + tsmi.SizeLength);
                        }

                        if (trackTags)
                        {
                            var marshalType = (MarshalType)arg1.Value;
                            switch (marshalType)
                            {
                                case MarshalType.UnionSelector:
                                {
                                    tags.Add(mi.Name, tsmi);
                                    dbg.Trace("Preproc Selector: " + mi.Name);
                                    break;
                                }
                                case MarshalType.Union:
                                {
                                    var selector = a.ConstructorArguments[2].Value;
                                    dbg.Trace("Preproc Union " + mi.Name + " with selector " + selector);
                                    tsmi.Tag = tags[(string)selector];
                                    break;
                                }
                            }
                        }
                        break;
                    }
                }
            }
            dbg.Unindent();
            return members.Values.ToArray();
        }

        virtual internal void ToStringInternal(TpmStructPrinter p)
        {
            bool enabled = dbg.Enabled;
            dbg.Enabled = false;
            var members = GetFieldsToMarshal();
            dbg.Enabled = enabled;
            foreach (var mem in members)
            {
                MemberInfo memInfo = mem;
                object memVal = Globs.GetMember(memInfo, this);
                Type memType = Globs.GetMemberType(memInfo);
                p.Print(memInfo.Name, Globs.ToCSharpStyle(memType.Name), memVal);
            }
        }

        /// <summary>
        /// Implements marshaling logic for most of the TPM object types.
        /// Can be overridden if a custom marshaling logic is required (e.g. when
        /// marshaling of a field depends on other field's value).
        /// </summary>
        /// <returns></returns>
        internal virtual void ToNet(Marshaller m)
        {
            var members = GetFieldsToMarshal();
            dbg.Indent();
            for (int i = 0; i < members.Length; ++i)
            {
                var mem = members[i];
                object memVal = Globs.GetMember(mem, this);
                dbg.Trace(i + ": " + mem.Name  +  " = " + memVal);
                if (mem.SizeLength > 0)
                {
                    bool arr = mem.WireType == MarshalType.VariableLengthArray;
                    int len = arr ? (memVal == null ? 0 : ((Array)memVal).Length)
                                  : Marshaller.GetTpmRepresentation(memVal).Length;
                    dbg.Trace("Sending " + (arr ? "Array " : "Struct ") + mem.Name + " of size " + len);
                    m.PutSizeTag(len, mem.SizeLength, mem.SizeName);
                }
                m.Put(memVal, mem.Name);
            }
            dbg.Unindent();
        }

        internal virtual void ToHost(Marshaller m)
        {
            var members = GetFieldsToMarshal(true);
            dbg.Indent();
            for (int i = 0; i < members.Length; ++i)
            {
                TpmStructMemberInfo memInfo = members[i];
                Type memType = Globs.GetMemberType(memInfo);
                var wt = members[i].WireType;
                switch(wt)
                {
                    case MarshalType.Union:
                    {
                        dbg.Trace("Union " + memType.Name + " with selector " + memInfo.Tag.Value);
                        memInfo.Value = m.Get(UnionElementFromSelector(memType, memInfo.Tag.Value), memType.Name);
                        break;
                    }
                    case MarshalType.FixedLengthArray:
                    {
                        object arr = Globs.GetMember(memInfo, this);
                        memInfo.Value = m.GetArray(memType.GetElementType(), (arr as Array).Length, memInfo.Name);
                        break;
                    }
                    case MarshalType.VariableLengthArray:
                    {
                        int size = m.GetSizeTag(memInfo.SizeLength, memInfo.SizeName);
                        memInfo.Value = m.GetArray(memType.GetElementType(), size, memInfo.Name);
                        Debug.Assert(size == ((Array)memInfo.Value).Length);
                        dbg.Trace("Received Array " + memInfo.Name + " of size " + size);
                        break;
                    }
                    case MarshalType.SizedStruct:
                    {
                        int size = m.GetSizeTag(memInfo.SizeLength, memInfo.SizeName);
                        if (size != 0)
                        {
                            memInfo.Value = m.Get(memType, memInfo.Name);
                            Debug.Assert(size == Marshaller.GetTpmRepresentation(memInfo.Value).Length);
                        }
                        dbg.Trace("Received Struct " + memInfo.Name + " of size " + size);
                        break;
                    }
                    default:
                        // Only attempt unmarshaling a field, if it is not sized or
                        // if its size is non-zero.
                        if (memInfo.Tag == null ||
                            memInfo.Tag.GetValueAsUInt() != 0)
                        {
                            memInfo.Value = m.Get(memType, memInfo.Name);
                        }
                        break;
                }
                dbg.Trace((i + 1) + ": " + memInfo.Name +  " = " + memInfo.Value);
                // Some property values are dynamically obtained from their linked fields.
                // Correspondingly, they do not have a setter, so we bypass them here.
                Debug.Assert(wt != MarshalType.LengthOfStruct && wt != MarshalType.ArrayCount);
                if (wt != MarshalType.UnionSelector)
                {
                    Globs.SetMember(memInfo, this, memInfo.Value);
                }
            }
            dbg.Unindent();
        }
    } // class TpmStructureBase
}