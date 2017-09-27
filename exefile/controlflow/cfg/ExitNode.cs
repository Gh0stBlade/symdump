﻿using System.Collections.Generic;
using System.Linq;
using core;
using core.util;

namespace exefile.controlflow.cfg
{
    public class ExitNode : Node
    {
        public ExitNode(IGraph graph) : base(graph)
        {
        }

        public override bool ContainsAddress(uint address) => false;

        public override IEnumerable<Instruction> Instructions => Enumerable.Empty<Instruction>();

        public override void Dump(IndentedTextWriter writer)
        {
            writer.WriteLine("ExitNode");
        }

        public override string Id => "exit";
    }
}
