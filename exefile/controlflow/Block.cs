﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using core;
using core.util;

namespace exefile.controlflow
{
    public class Block : IBlock
    {
        public IBlock trueExit { get; set; }

        public IBlock falseExit { get; set; }

        public uint start => instructions.Keys.First();

        public SortedDictionary<uint, Instruction> instructions { get; } = new SortedDictionary<uint, Instruction>();

        public ExitType? exitType { get; set; }

        public bool containsAddress(uint address)
        {
            if (instructions.Count == 0)
                return false;

            return address >= instructions.Keys.First() && address <= instructions.Keys.Last();
        }

        public void dump(IndentedTextWriter writer)
        {
            writer.WriteLine($"// exitType={exitType} start=0x{start:X}");
            if (trueExit != null)
                writer.WriteLine($"// trueExit=0x{trueExit.start:X}");
            if (falseExit != null)
                writer.WriteLine($"// falseExit=0x{falseExit.start:X}");

            ++writer.indent;
            foreach (var insn in instructions)
            {
                writer.WriteLine($"0x{insn.Key:X}  {insn.Value.asReadable()}");
            }
            --writer.indent;
        }
    }
}