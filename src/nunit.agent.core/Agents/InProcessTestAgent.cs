// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using NUnit.Engine.Runners;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NUnit.Engine.Agents
{
    public class InProcessTestAgent : TestAgent
    {
        public InProcessTestAgent(Guid agentId) : base(agentId)
        {
        }

        public override ITestEngineRunner CreateRunner(TestPackage package)
        {
#if NETFRAMEWORK
            return new TestDomainRunner(package);
#else
            return new LocalTestRunner(package);
#endif
        }

        public override bool Start()
        {
            return true;
        }

        public override void Stop()
        {
        }
    }
}
