// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System;
using NUnit.Engine.Agents;
using NUnit.Engine.Communication.Transports.Tcp;
using NUnit.Framework;

namespace NUnit.Engine.Communication
{
    /// <summary>
    /// Checks transport initialization independently of test loading and execution.
    /// </summary>
    public class TestAgentTcpTransportTests
    {
        /// <summary>
        /// Verifies that creating a transport does not require XML serialization of package settings.
        /// </summary>
        [Test]
        public void ConstructorDoesNotRequireXmlSerializablePackageSettings()
        {
            var agent = new RemoteTestAgent(Guid.NewGuid());
            try
            {
                var transport = new TestAgentTcpTransport(agent, "127.0.0.1:12345");
                agent.Transport = transport;

                Assert.That(transport.Agent, Is.SameAs(agent));
                Assert.That(transport.ServerEndPoint.Port, Is.EqualTo(12345));
            }
            finally
            {
                // The constructor may fail before a transport can be assigned to the agent.
                if (agent.Transport is not null)
                    agent.Dispose();
            }
        }
    }
}
