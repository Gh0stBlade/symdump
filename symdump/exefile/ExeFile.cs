﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using symdump.exefile.disasm;
using symdump.exefile.instructions;
using symdump.exefile.operands;
using symdump.exefile.util;
using symdump.symfile;

namespace symdump.exefile
{
    public class ExeFile
    {
        private readonly Queue<uint> _analysisQueue = new Queue<uint>();
        private readonly byte[] _data;
        private readonly uint? _gpBase;

        private readonly Header _header;

        private readonly SortedDictionary<uint, Instruction> _instructions = new SortedDictionary<uint, Instruction>();
        private readonly SymFile _symFile;
        private readonly Dictionary<uint, HashSet<uint>> _xrefs = new Dictionary<uint, HashSet<uint>>();
        private readonly SortedSet<uint> _callees = new SortedSet<uint>();

        public ExeFile(EndianBinaryReader reader, SymFile symFile)
        {
            _symFile = symFile;
            reader.BaseStream.Seek(0, SeekOrigin.Begin);

            _header = new Header(reader);
            reader.BaseStream.Seek(0x800, SeekOrigin.Begin);
            _data = reader.ReadBytes((int) _header.TSize);

            _gpBase = _symFile.Labels
                .Where(byOffset => byOffset.Value.Any(lbl => lbl.Name.Equals("__SN_GP_BASE")))
                .Select(lbl => lbl.Key)
                .FirstOrDefault();
        }

        private string GetSymbolName(uint addr, int rel = 0)
        {
            addr = (uint) (addr + rel);

            List<Label> lbls;
            if (!_symFile.Labels.TryGetValue(addr, out lbls))
                return $"lbl_{addr:X}";

            return lbls.First().Name;
        }

        private IEnumerable<string> GetSymbolNames(uint addr)
        {
            List<Label> lbls;
            _symFile.Labels.TryGetValue(addr + _header.TAddr, out lbls);
            return lbls?.Select(l => l.Name);
        }

        private void AddCall(uint from, uint to)
        {
            AddXref(from, to);
            _callees.Add(to);
        }

        private void AddXref(uint from, uint to)
        {
            HashSet<uint> froms;
            if (!_xrefs.TryGetValue(to, out froms))
                _xrefs.Add(to, froms = new HashSet<uint>());

            froms.Add(from);

            if (!_instructions.ContainsKey(to))
                _analysisQueue.Enqueue(to);
        }

        private HashSet<uint> GetXrefs(uint to)
        {
            HashSet<uint> froms;
            _xrefs.TryGetValue(to, out froms);
            return froms;
        }

        private uint DataAt(uint ofs)
        {
            uint data;
            data = _data[ofs++];
            data |= (uint) _data[ofs++] << 8;
            data |= (uint) _data[ofs++] << 16;
            data |= (uint) _data[ofs++] << 24;
            return data;
        }

        private static Opcode ExtractOpcode(uint data)
        {
            return (Opcode) (data >> 26);
        }

        public void Disassemble()
        {
            _analysisQueue.Clear();
            _analysisQueue.Enqueue(_header.Pc0 - _header.TAddr);
            foreach (var addr in _symFile.Functions.Select(f => f.Address))
                _analysisQueue.Enqueue(addr - _header.TAddr);

            while (_analysisQueue.Count != 0)
            {
                var index = _analysisQueue.Dequeue();
                if (_instructions.ContainsKey(index) || index >= _data.Length)
                    continue;

                var data = DataAt(index);
                index += 4;
                var insn = _instructions[index - 4] = DecodeInstruction(data, index);

                var cbranchInsn = insn as ConditionalBranchInstruction;
                if (cbranchInsn != null)
                {
                    data = DataAt(index);
                    index += 4;
                    var insn2 = _instructions[index - 4] = DecodeInstruction(data, index);
                    insn2.IsBranchDelaySlot = true;

                    _analysisQueue.Enqueue(index);

                    continue;
                }

                var callInsn = insn as CallPtrInstruction;
                if (callInsn != null)
                {
                    data = DataAt(index);
                    index += 4;
                    var insn2 = _instructions[index - 4] = DecodeInstruction(data, index);
                    insn2.IsBranchDelaySlot = true;

                    continue;
                }

                _analysisQueue.Enqueue(index);
            }
        }

        public void Dump()
        {
            foreach (var insn in _instructions)
            {
                if (_callees.Contains(insn.Key))
                    Console.WriteLine("### FUNCTION");
                if (insn.Value.AsReadable().Equals("nop"))
                    continue;

                var f = _symFile.FindFunction(insn.Key + _header.TAddr);
                if (f != null)
                    Console.WriteLine();

                var xrefsHere = GetXrefs(insn.Key);
                if (xrefsHere != null)
                {
                    Console.WriteLine("# XRefs:");
                    foreach (var xref in xrefsHere)
                        Console.WriteLine("# - " + GetSymbolName(xref));
                    var names = GetSymbolNames(insn.Key);
                    if (names != null)
                        foreach (var name in names)
                            Console.WriteLine(name + ":");
                    else
                        Console.WriteLine(GetSymbolName(insn.Key) + ":");
                }

                if (f != null)
                    Console.WriteLine(f.GetSignature());

                Console.WriteLine($"  0x{insn.Key:X}  {insn.Value.AsReadable()}");
            }
        }

        private IOperand MakeGpBasedOperand(uint data, int shift, int offset)
        {
            var regofs = new RegisterOffsetOperand(data, shift, offset);
            if (_gpBase == null)
                return regofs;

            if (regofs.Register == Register.gp)
                return new LabelOperand(GetSymbolName(_gpBase.Value, regofs.Offset));

            return regofs;
        }

        private Instruction DecodeInstruction(uint data, uint index)
        {
            switch (ExtractOpcode(data))
            {
                case Opcode.RegisterFormat:
                    return DecodeRegisterFormat(data);
                case Opcode.PCRelative:
                    return DecodePcRelative(index, data);
                case Opcode.j:
                    AddCall(index - 4, (data & 0x03FFFFFF) << 2);
                    _analysisQueue.Enqueue((data & 0x03FFFFFF) << 2);
                    return new CallPtrInstruction(new LabelOperand(GetSymbolName((data & 0x03FFFFFF) << 2)), null);
                case Opcode.jal:
                    AddCall(index - 4, (data & 0x03FFFFFF) << 2);
                    _analysisQueue.Enqueue((data & 0x03FFFFFF) << 2);
                    return new CallPtrInstruction(new LabelOperand(GetSymbolName((data & 0x03FFFFFF) << 2)),
                        new RegisterOperand(Register.ra));
                case Opcode.beq:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    if (((data >> 16) & 0x1F) == 0)
                        return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.Equal,
                            new RegisterOperand(data, 21),
                            new ImmediateOperand(0),
                            new LabelOperand(GetSymbolName(index, (short) data << 2)));
                    else
                        return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.Equal,
                            new RegisterOperand(data, 21),
                            new RegisterOperand(data, 16),
                            new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.bne:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    if (((data >> 16) & 0x1F) == 0)
                        return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.NotEqual,
                            new RegisterOperand(data, 21),
                            new ImmediateOperand(0),
                            new LabelOperand(GetSymbolName(index, (short) data << 2)));
                    else
                        return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.NotEqual,
                            new RegisterOperand(data, 21),
                            new RegisterOperand(data, 16),
                            new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.blez:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.LessEqual,
                        new RegisterOperand(data, 21),
                        new ImmediateOperand(0),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.bgtz:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.Greater,
                        new RegisterOperand(data, 21),
                        new ImmediateOperand(0),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.addi:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Add,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((short) data));
                case Opcode.addiu:
                    if (((data >> 21) & 0x1F) == 0)
                        return new SimpleInstruction("li", "{0} = {1}", new RegisterOperand(data, 16),
                            new ImmediateOperand((short) data));
                    else
                        return new ArithmeticInstruction(ArithmeticInstruction.Operation.Add,
                            new RegisterOperand(data, 16),
                            new RegisterOperand(data, 21),
                            new ImmediateOperand((ushort) data));
                case Opcode.subi:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sub,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((short) data));
                case Opcode.subiu:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sub,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((ushort) data));
                case Opcode.andi:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitAnd,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((short) data));
                case Opcode.ori:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitOr,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((short) data));
                case Opcode.xori:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitXor,
                        new RegisterOperand(data, 16),
                        new RegisterOperand(data, 21),
                        new ImmediateOperand((short) data));
                case Opcode.lui:
                    return new SimpleInstruction("lui", "{0} = {1}",
                        new RegisterOperand(data, 16),
                        new ImmediateOperand((ushort) data << 16));
                case Opcode.CpuControl:
                    return DecodeCpuControl(index, data);
                case Opcode.FloatingPoint:
                    return new WordData(data);
                case Opcode.lb:
                    return new SimpleInstruction("lb", "{0} = (signed char){1}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lh:
                    return new SimpleInstruction("lh", "{0} = (short){1}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lwl:
                    return new SimpleInstruction("lwl", null, new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lw:
                    return new SimpleInstruction("lw", "{0} = (int){1}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lbu:
                    return new SimpleInstruction("lbu", "{0} = (unsigned char){1}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lhu:
                    return new SimpleInstruction("lhu", "{0} = (unsigned short){1}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.lwr:
                    return new SimpleInstruction("lwr", null, new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.sb:
                    return new SimpleInstruction("sb", "{1} = (char){0}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.sh:
                    return new SimpleInstruction("sh", "{1} = (short){0}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.swl:
                    return new SimpleInstruction("swl", null, new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.sw:
                    return new SimpleInstruction("sw", "{1} = (int){0}", new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.swr:
                    return new SimpleInstruction("swr", null, new RegisterOperand(data, 16),
                        MakeGpBasedOperand(data, 21, (short) data));
                case Opcode.swc1:
                    return new SimpleInstruction("swc1", null, new RegisterOperand(data, 16),
                        new ImmediateOperand((short) data), new RegisterOperand(data, 21));
                case Opcode.lwc1:
                    return new SimpleInstruction("lwc1", null, new C2RegisterOperand(data, 16),
                        new ImmediateOperand((short) data), new RegisterOperand(data, 21));
                case Opcode.cop0:
                    return new SimpleInstruction("cop0", null, new ImmediateOperand(data & ((1 << 26) - 1)));
                case Opcode.cop1:
                    return new SimpleInstruction("cop1", null, new ImmediateOperand(data & ((1 << 26) - 1)));
                case Opcode.cop2:
                    return DecodeCop2(data);
                case Opcode.cop3:
                    return new SimpleInstruction("cop3", null, new ImmediateOperand(data & ((1 << 26) - 1)));
                case Opcode.beql:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.Equal,
                        new RegisterOperand(data, 21),
                        new RegisterOperand(data, 16),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.bnel:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.NotEqual,
                        new RegisterOperand(data, 21),
                        new RegisterOperand(data, 16),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.blezl:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.SignedLessEqual,
                        new RegisterOperand(data, 21),
                        new ImmediateOperand(0),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                case Opcode.bgtzl:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.Greater,
                        new RegisterOperand(data, 21),
                        new ImmediateOperand(0),
                        new LabelOperand(GetSymbolName(index, (short) data << 2)));
                default:
                    return new WordData(data);
            }
        }

        private static Instruction DecodeRegisterFormat(uint data)
        {
            var rd = new RegisterOperand(data, 11);
            var rs2 = new RegisterOperand(data, 16);
            var rs1 = new RegisterOperand(data, 21);
            switch ((OpcodeFunction) (data & 0x3f))
            {
                case OpcodeFunction.sll:
                    if (data == 0)
                        return new SimpleInstruction("nop", null);
                    else
                        return new ArithmeticInstruction(ArithmeticInstruction.Operation.Shl,
                            rd, rs2,
                            new ImmediateOperand((int) (data >> 6) & 0x1F));
                case OpcodeFunction.srl:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Shr,
                        rd, rs2,
                        new ImmediateOperand((int) (data >> 6) & 0x1F));
                case OpcodeFunction.sra:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sar,
                        rd, rs2,
                        new ImmediateOperand((int) (data >> 6) & 0x1F));
                case OpcodeFunction.sllv:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Shl,
                        rd, rs2,
                        rs1);
                case OpcodeFunction.srlv:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Shr,
                        rd, rs2,
                        rs1);
                case OpcodeFunction.srav:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sar,
                        rd, rs2,
                        rs1);
                case OpcodeFunction.jr:
                    return new CallPtrInstruction(rs1, null);
                case OpcodeFunction.jalr:
                    return new CallPtrInstruction(rs1, rd);
                case OpcodeFunction.syscall:
                    return new SimpleInstruction("syscall", "trap(SYSCALL, {0})",
                        new ImmediateOperand((int) (data >> 6) & 0xFFFFF));
                case OpcodeFunction.break_:
                    return new SimpleInstruction("break", "trap(BREAK, {0})",
                        new ImmediateOperand((int) (data >> 6) & 0xFFFFF));
                case OpcodeFunction.mfhi:
                    return new SimpleInstruction("mfhi", "{0} = __DIV_REMAINDER_OR_MULT_HI()",
                        rd);
                case OpcodeFunction.mthi:
                    return new SimpleInstruction("mthi", "__LOAD_DIV_REMAINDER_OR_MULT_HI({0})",
                        rd);
                case OpcodeFunction.mflo:
                    return new SimpleInstruction("mflo", "{0} = __DIV_OR_MULT_LO()",
                        rd);
                case OpcodeFunction.mtlo:
                    return new SimpleInstruction("mtlo", "__LOAD_DIV_OR_MULT_LO({0})",
                        rd);
                case OpcodeFunction.mult:
                    return new SimpleInstruction("mult", "__MULT((signed){0}, (signed){1})",
                        rs1, rs2);
                case OpcodeFunction.multu:
                    return new SimpleInstruction("multu", "__MULT((unsigned){0}, (unsigned){1})",
                        rs1, rs2);
                case OpcodeFunction.div:
                    return new SimpleInstruction("div", "__DIV((signed){0}, (signed){1})",
                        rs1, rs2);
                case OpcodeFunction.divu:
                    return new SimpleInstruction("divu", "__DIV((unsigned){0}, (unsigned){1})",
                        rs1, rs2);
                case OpcodeFunction.add:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Add,
                        rd, rs1, rs2);
                case OpcodeFunction.addu:
                    if (((data >> 16) & 0x1F) == 0)
                        return new SimpleInstruction("move", "{0} = {1}", rd,
                            rs1);
                    else
                        return new ArithmeticInstruction(ArithmeticInstruction.Operation.Add,
                            rd, rs1, rs2);
                case OpcodeFunction.sub:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sub,
                        rd, rs1, rs2);
                case OpcodeFunction.subu:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.Sub,
                        rd, rs1, rs2);
                case OpcodeFunction.and:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitAnd,
                        rd, rs1, rs2);
                case OpcodeFunction.or:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitOr,
                        rd, rs1, rs2);
                case OpcodeFunction.xor:
                    return new ArithmeticInstruction(ArithmeticInstruction.Operation.BitXor,
                        rd, rs1, rs2);
                case OpcodeFunction.nor:
                    return new SimpleInstruction("nor", "{0} = ~({1} | {2})", rd,
                        rs1, rs2);
                case OpcodeFunction.slt:
                    return new SimpleInstruction("slt", "{0} = {1} < {2} ? 1 : 0",
                        rd, rs1,
                        rs2);
                case OpcodeFunction.sltu:
                    return new SimpleInstruction("sltu", "{0} = {1} < {2} ? 1 : 0",
                        rd, rs1,
                        rs2);
                default:
                    return new WordData(data);
            }
        }

        private Instruction DecodeCpuControl(uint index, uint data)
        {
            switch ((CpuControlOpcode) ((data >> 21) & 0x1f))
            {
                case CpuControlOpcode.mtc0:
                    return new SimpleInstruction("mtc0", null, new RegisterOperand(data, 16),
                        new C0RegisterOperand(data, 11));
                case CpuControlOpcode.bc0:
                    switch ((data >> 16) & 0x1f)
                    {
                        case 0:
                            AddXref(index - 4, (uint) ((index + (short) data) << 2));
                            return new SimpleInstruction("bc0f", null,
                                new LabelOperand(GetSymbolName(index, (ushort) data << 2)));
                        case 1:
                            AddXref(index - 4, (uint) ((index + (short) data) << 2));
                            return new SimpleInstruction("bc0t", null,
                                new LabelOperand(GetSymbolName(index, (ushort) data << 2)));
                        default:
                            return new WordData(data);
                    }
                case CpuControlOpcode.tlb:
                    return DecodeTlb(data);
                case CpuControlOpcode.mfc0:
                    return new SimpleInstruction("mfc0", null, new RegisterOperand(data, 16),
                        new C0RegisterOperand(data, 11));
                default:
                    return new WordData(data);
            }
        }

        private static Instruction DecodeTlb(uint data)
        {
            switch ((TlbOpcode) (data & 0x1f))
            {
                case TlbOpcode.tlbr:
                    return new SimpleInstruction("tlbr", null);
                case TlbOpcode.tlbwi:
                    return new SimpleInstruction("tlbwi", null);
                case TlbOpcode.tlbwr:
                    return new SimpleInstruction("tlbwr", null);
                case TlbOpcode.tlbp:
                    return new SimpleInstruction("tlbp", null);
                case TlbOpcode.rfe:
                    return new SimpleInstruction("rfe", "__RETURN_FROM_EXCEPTION()");
                default:
                    return new WordData(data);
            }
        }

        private Instruction DecodePcRelative(uint index, uint data)
        {
            var rs = new RegisterOperand(data, 21);
            var offset = new LabelOperand(GetSymbolName(index, (ushort) data << 2));
            switch ((data >> 16) & 0x1f)
            {
                case 0:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.SignedLess,
                        rs,
                        new ImmediateOperand(0),
                        offset);
                case 1:
                    AddXref(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalBranchInstruction(ConditionalBranchInstruction.Operation.SignedGreaterEqual,
                        rs,
                        new ImmediateOperand(0),
                        offset);
                case 16:
                    AddCall(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalCallInstruction(ConditionalBranchInstruction.Operation.SignedLess,
                        rs,
                        new ImmediateOperand(0),
                        offset);
                case 17:
                    AddCall(index - 4, (uint) ((index + (short) data) << 2));
                    _analysisQueue.Enqueue(index + (uint) ((short) data << 2));
                    return new ConditionalCallInstruction(ConditionalBranchInstruction.Operation.SignedGreaterEqual,
                        rs,
                        new ImmediateOperand(0),
                        offset);
                default:
                    return new WordData(data);
            }
        }

        private static Instruction DecodeCop2(uint data)
        {
            var opc = data & ((1 << 26) - 1);
            if (((data >> 25) & 1) != 0)
                return DecodeCop2Gte(opc);

            var cf = (opc >> 21) & 0x1F;
            switch (cf)
            {
                case 0:
                    return new SimpleInstruction("mfc2", null, new RegisterOperand(opc, 16),
                        new ImmediateOperand((short) opc), new C2RegisterOperand(opc, 21));
                case 2:
                    return new SimpleInstruction("cfc2", null, new RegisterOperand(opc, 16),
                        new ImmediateOperand((short) opc), new C2RegisterOperand(opc, 21));
                case 4:
                    return new SimpleInstruction("mtc2", null, new RegisterOperand(opc, 16),
                        new ImmediateOperand((short) opc), new C2RegisterOperand(opc, 21));
                case 6:
                    return new SimpleInstruction("ctc2", null, new RegisterOperand(opc, 16),
                        new ImmediateOperand((short) opc), new C2RegisterOperand(opc, 21));
                default:
                    return new WordData(data);
            }
        }

        private static Instruction DecodeCop2Gte(uint data)
        {
            switch (data & 0x1F003FF)
            {
                case 0x0400012:
                    return new SimpleInstruction("mvmva",
                        null,
                        new ImmediateOperand((int) (data >> 19) & 1),
                        new ImmediateOperand((int) (data >> 17) & 3),
                        new ImmediateOperand((int) (data >> 15) & 3),
                        new ImmediateOperand((int) (data >> 13) & 3),
                        new ImmediateOperand((int) (data >> 10) & 1)
                    );
                case 0x0a00428:
                    return new SimpleInstruction("sqr", null,
                        new ImmediateOperand((int) (data >> 19) & 1));
                case 0x170000C:
                    return new SimpleInstruction("op", null,
                        new ImmediateOperand((int) (data >> 19) & 1));
                case 0x190003D:
                    return new SimpleInstruction("gpf", null,
                        new ImmediateOperand((int) (data >> 19) & 1));
                case 0x1A0003E:
                    return new SimpleInstruction("gpl", null,
                        new ImmediateOperand((int) (data >> 19) & 1));
                default:
                    switch (data)
                    {
                        case 0x0180001:
                            return new SimpleInstruction("rtps", null);
                        case 0x0280030:
                            return new SimpleInstruction("rtpt", null);
                        case 0x0680029:
                            return new SimpleInstruction("dcpl", null);
                        case 0x0780010:
                            return new SimpleInstruction("dcps", null);
                        case 0x0980011:
                            return new SimpleInstruction("intpl", null);
                        case 0x0C8041E:
                            return new SimpleInstruction("ncs", null);
                        case 0x0D80420:
                            return new SimpleInstruction("nct", null);
                        case 0x0E80413:
                            return new SimpleInstruction("ncds", null);
                        case 0x0F80416:
                            return new SimpleInstruction("ncdt", null);
                        case 0x0F8002A:
                            return new SimpleInstruction("dpct", null);
                        case 0x108041B:
                            return new SimpleInstruction("nccs", null);
                        case 0x118043F:
                            return new SimpleInstruction("ncct", null);
                        case 0x1280414:
                            return new SimpleInstruction("cdp", null);
                        case 0x138041C:
                            return new SimpleInstruction("cc", null);
                        case 0x1400006:
                            return new SimpleInstruction("nclip", null);
                        case 0x158002D:
                            return new SimpleInstruction("avsz3", null);
                        case 0x168002E:
                            return new SimpleInstruction("avsz4", null);
                        default:
                            return new SimpleInstruction("cop2", null,
                                new ImmediateOperand(data));
                    }
            }
        }

        [SuppressMessage("ReSharper", "NotAccessedField.Local")]
        [SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
        private class Header
        {
            public readonly uint BAddr;
            public readonly uint BSize;
            public readonly uint DAddr;
            public readonly uint Data;
            public readonly uint DSize;
            public readonly uint Gp0;
            public readonly char[] ID;
            public readonly uint Pc0;
            public readonly uint SAddr;
            public readonly uint SavedFp;
            public readonly uint SavedGp;
            public readonly uint SavedRa;
            public readonly uint SavedS0;
            public readonly uint SavedSp;
            public readonly uint SSize;
            public readonly uint TAddr;
            public readonly uint Text;
            public readonly uint TSize;

            public Header(EndianBinaryReader reader)
            {
                ID = reader.ReadBytes(8).Select(b => (char) b).ToArray();

                if (!"PS-X EXE".Equals(new string(ID)))
                    throw new Exception("Header ID mismatch");

                Text = reader.ReadUInt32();
                Data = reader.ReadUInt32();
                Pc0 = reader.ReadUInt32();
                Gp0 = reader.ReadUInt32();
                TAddr = reader.ReadUInt32();
                TSize = reader.ReadUInt32();
                DAddr = reader.ReadUInt32();
                DSize = reader.ReadUInt32();
                BAddr = reader.ReadUInt32();
                BSize = reader.ReadUInt32();
                SAddr = reader.ReadUInt32();
                SSize = reader.ReadUInt32();
                SavedSp = reader.ReadUInt32();
                SavedFp = reader.ReadUInt32();
                SavedGp = reader.ReadUInt32();
                SavedRa = reader.ReadUInt32();
                SavedS0 = reader.ReadUInt32();
            }
        }
    }
}
