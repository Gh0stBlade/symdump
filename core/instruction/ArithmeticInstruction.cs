﻿using System;
using System.Collections.Generic;
using System.Linq;
using core.expression;
using core.operand;

namespace core.instruction
{
    public class ArithmeticInstruction : Instruction
    {
        public readonly Operator Operator;

        public override uint? JumpTarget => null;

        public ArithmeticInstruction(Operator @operator, IOperand dest, IOperand lhs, IOperand rhs)
        {
            Operator = @operator;
            Operands = new[] {dest, lhs, rhs};
        }

        public override IOperand[] Operands { get; }

        public IOperand Destination => Operands[0];
        public IOperand Lhs => Operands[1];
        public IOperand Rhs => Operands[2];

        public override IEnumerable<int> OutputRegisters
        {
            get
            {
                switch (Destination)
                {
                    case RegisterOperand r:
                        yield return r.Register;
                        break;
                }
            }
        }

        public override IEnumerable<int> InputRegisters
            => Lhs.TouchedRegisters.Concat(Rhs.TouchedRegisters);

        public bool IsInplace => Destination.Equals(Lhs);

        public override string AsReadable()
        {
            var op = Operator.AsCode();

            return IsInplace
                ? $"{Destination} {op}= {Rhs}"
                : $"{Destination} = {Lhs} {op} {Rhs}";
        }

        public override IExpressionNode ToExpressionNode(IDataFlowState dataFlowState)
        {
            var lhsExpr = Lhs.ToExpressionNode(dataFlowState);
            var rhsExpr = Rhs.ToExpressionNode(dataFlowState);

            if (!(lhsExpr is ValueNode) || !(rhsExpr is ValueNode))
            {
                return new ExpressionNode(Operator, lhsExpr, rhsExpr);
            }

            var lhsVal = ((ValueNode) lhsExpr).Value;
            var rhsVal = ((ValueNode) rhsExpr).Value;

            switch (Operator)
            {
                case Operator.Add:
                    return new ValueNode(lhsVal + rhsVal);
                case Operator.Sub:
                    return new ValueNode(lhsVal - rhsVal);
                case Operator.Mul:
                    return new ValueNode(lhsVal * rhsVal);
                case Operator.Div:
                    return new ValueNode(lhsVal / rhsVal);
                case Operator.Shl:
                    return new ValueNode(lhsVal << (int) rhsVal);
                case Operator.Shr:
                    return new ValueNode(lhsVal >> (int) rhsVal);
                case Operator.Sar:
                    return new ValueNode(lhsVal >> (int) rhsVal);
                case Operator.BitAnd:
                    return new ValueNode(lhsVal & rhsVal);
                case Operator.BitOr:
                    return new ValueNode(lhsVal | rhsVal);
                case Operator.BitXor:
                    return new ValueNode(lhsVal ^ rhsVal);
                case Operator.Equal:
                case Operator.NotEqual:
                case Operator.Less:
                case Operator.SignedLess:
                case Operator.Greater:
                case Operator.SignedGreater:
                case Operator.LessEqual:
                case Operator.SignedLessEqual:
                case Operator.GreaterEqual:
                case Operator.SignedGreaterEqual:
                    return new ExpressionNode(Operator, Lhs.ToExpressionNode(dataFlowState),
                        Rhs.ToExpressionNode(dataFlowState));
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}