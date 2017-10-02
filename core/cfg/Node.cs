﻿using System.Collections.Generic;
using System.IO;
using System.Text;
using core.util;

namespace core.cfg
{
    public abstract class Node : INode
    {
        protected Node(IGraph graph)
        {
            Graph = graph;
        }

        public abstract bool ContainsAddress(uint address);

        public abstract IEnumerable<Instruction> Instructions { get; }

        public abstract string Id { get; }
        public abstract IEnumerable<int> InputRegisters { get; }
        public abstract IEnumerable<int> OutputRegisters { get; }

        public abstract void Dump(IndentedTextWriter writer, IDataFlowState dataFlowState);

        public IGraph Graph { get; }

        public IEnumerable<IEdge> Ins => Graph.GetIns(this);

        public IEnumerable<IEdge> Outs => Graph.GetOuts(this);

        public sealed override string ToString()
        {
            var sb = new StringBuilder();
            Dump(new IndentedTextWriter(new StringWriter(sb)), null);
            return sb.ToString();
        }
    }
}