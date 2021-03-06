﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using symdump.symfile.util;
using symdump.util;

namespace symdump.symfile
{
    public class EnumDef : IEquatable<EnumDef>
    {
        private readonly Dictionary<string, int> _members = new Dictionary<string, int>();
        private readonly string _name;
        private readonly uint _size;

        public EnumDef(BinaryReader stream, string name)
        {
            _name = name;
            while (true)
            {
                var typedValue = new TypedValue(stream);
                if (typedValue.Type == (0x80 | 20))
                {
                    var ti = stream.ReadTypeInfo(false);
                    var memberName = stream.ReadPascalString();

                    if (ti.ClassType == ClassType.EndOfStruct)
                        break;

                    if (ti.ClassType != ClassType.EnumMember)
                        throw new Exception("Unexpected class");

                    _members.Add(memberName, typedValue.Value);
                }
                else if (typedValue.Type == (0x80 | 22))
                {
                    var ti = stream.ReadTypeInfo(true);
                    if (ti.TypeDef.BaseType != BaseType.Null)
                        throw new Exception($"Expected baseType={BaseType.Null}, but it's {ti.TypeDef.BaseType}");

                    if (ti.Dims.Length != 0)
                        throw new Exception($"Expected dims=0, but it's {ti.Dims.Length}");

                    if (ti.Tag != name)
                        throw new Exception($"Expected name={name}, but it's {ti.Tag}");

                    var tag = stream.ReadPascalString();
                    if (tag != ".eos")
                        throw new Exception($"Expected tag=.eos, but it's {tag}");

                    if (ti.ClassType != ClassType.EndOfStruct)
                        throw new Exception($"Expected classType={ClassType.EndOfStruct}, but it's {ti.ClassType}");

                    _size = ti.Size;
                    break;
                }
                else
                {
                    throw new Exception("Unexpected entry");
                }
            }
        }

        public bool Equals(EnumDef other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return _members.SequenceEqual(other._members) && string.Equals(_name, other._name);
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((EnumDef) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (_members.GetHashCode() * 397) ^ _name.GetHashCode();
            }
        }

        public void Dump(IndentedTextWriter writer)
        {
            string cType;
            switch (_size)
            {
                case 1:
                    cType = "char";
                    break;
                case 2:
                    cType = "short";
                    break;
                case 4:
                    cType = "int";
                    break;
                default:
                    throw new Exception("$Cannot determine primitive type for size {size}");
            }

            writer.WriteLine($"enum {_name} : {cType} {{");
            ++writer.Indent;
            foreach (var kvp in _members)
                writer.WriteLine($"{kvp.Key} = {kvp.Value},");
            --writer.Indent;
            writer.WriteLine("};");
        }
    }
}
