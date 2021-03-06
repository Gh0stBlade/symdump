using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using symdump.symfile.util;
using symdump.util;

namespace symdump.symfile
{
    public class Function
    {
        public readonly uint Address;
        private readonly List<Block> _blocks = new List<Block>();
        private readonly string _file;
        private readonly uint _lastLine;
        private readonly uint _line;
        private readonly uint _mask;
        private readonly int _maskOffs;
        private readonly string _name;

        private readonly List<string> _parameters = new List<string>();
        private readonly Register _register;
        private readonly string _returnType;
        private readonly Register _stackBase;
        private readonly uint _stackFrameSize;

        public Function(BinaryReader reader, uint ofs, IReadOnlyDictionary<string, string> funcTypes)
        {
            Address = ofs;

            _stackBase = (Register) reader.ReadUInt16();
            _stackFrameSize = reader.ReadUInt32();
            _register = (Register) reader.ReadUInt16();
            _mask = reader.ReadUInt32();
            _maskOffs = reader.ReadInt32();

            _line = reader.ReadUInt32();
            _file = reader.ReadPascalString();
            _name = reader.ReadPascalString();

            if (!funcTypes.TryGetValue(_name, out _returnType))
                _returnType = "__UNKNOWN__";

            while (true)
            {
                var typedValue = new TypedValue(reader);

                if (reader.SkipSld(typedValue))
                    continue;

                TypeInfo ti;
                string memberName;
                switch (typedValue.Type & 0x7f)
                {
                    case 14: // end of function
                        _lastLine = reader.ReadUInt32();
                        return;
                    case 16: // begin of block
                        _blocks.Add(new Block(reader, (uint) typedValue.Value, reader.ReadUInt32(), this));
                        continue;
                    case 20:
                        ti = reader.ReadTypeInfo(false);
                        memberName = reader.ReadPascalString();
                        break;
                    case 22:
                        ti = reader.ReadTypeInfo(true);
                        memberName = reader.ReadPascalString();
                        break;
                    default:
                        throw new Exception("Nope");
                }

                if (ti == null || memberName == null)
                    break;

                if (ti.ClassType == ClassType.Argument)
                    _parameters.Add($"{ti.AsCode(memberName)} /*stack {typedValue.Value}*/");
                else if (ti.ClassType == ClassType.RegParam)
                    _parameters.Add($"{ti.AsCode(memberName)} /*${(Register) typedValue.Value}*/");
            }
        }

        private IEnumerable<Register> SavedRegisters => Enumerable.Range(0, 32)
            .Where(i => ((1 << i) & _mask) != 0)
            .Select(i => (Register) i);

        public void Dump(IndentedTextWriter writer)
        {
            writer.WriteLine("/*");
            writer.WriteLine($" * Offset 0x{Address:X}");
            writer.WriteLine($" * {_file} (line {_line})");
            writer.WriteLine($" * Stack frame base ${_stackBase}, size {_stackFrameSize}");
            if (_mask != 0)
                writer.WriteLine($" * Saved registers at offset {_maskOffs}: {string.Join(" ", SavedRegisters)}");
            writer.WriteLine(" */");

            writer.WriteLine(GetSignature());

            _blocks.ForEach(b => b.Dump(writer));

            if (_blocks.Count != 0)
                return;

            writer.WriteLine("{");
            writer.WriteLine("}");
        }

        public string GetSignature()
        {
            return $"{_returnType} /*${_register}*/ {_name}({string.Join(", ", _parameters)})";
        }
    }
}
