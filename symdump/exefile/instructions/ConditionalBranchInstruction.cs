﻿using System;
using symdump.exefile.dataflow;
using symdump.exefile.expression;
using symdump.exefile.operands;

namespace symdump.exefile.instructions
{
    public class ConditionalBranchInstruction : Instruction
    {
        public readonly Operation operation;

        public ConditionalBranchInstruction(Operation operation, IOperand lhs, IOperand rhs, IOperand target)
        {
            this.operation = operation;
            operands = new[] {lhs, rhs, target};
        }

        public IOperand lhs => operands[0];
        public IOperand rhs => operands[1];
        public IOperand target => operands[2];

        public override IOperand[] operands { get; }

        public override string asReadable()
        {
            var op = operation.toCode();

            return $"if({lhs} {op} {rhs}) goto {target}";
        }

        public override IExpressionNode toExpressionNode(DataFlowState dataFlowState)
        {
            return new ConditionalBranchNode(operation, lhs.toExpressionNode(dataFlowState), rhs.toExpressionNode(dataFlowState), target.toExpressionNode(dataFlowState) as LabelNode);
        }
    }
}