﻿using System.Collections.Generic;
using System.Linq;
using core;
using core.util;

namespace exefile.controlflow.cfg
{
    public sealed class EntryNode : Node
    {
        public EntryNode(IGraph graph) : base(graph)
        {
        }

        public override bool ContainsAddress(uint address) => false;

        public override IEnumerable<Instruction> Instructions => Enumerable.Empty<Instruction>();

        public override void Dump(IndentedTextWriter writer)
        {
            writer.WriteLine("EntryNode");
        }

        public override string Id => "entry";
    }
}
