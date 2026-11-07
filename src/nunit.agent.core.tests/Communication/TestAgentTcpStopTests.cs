// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using NUnit.Engine.Agents;
using NUnit.Engine.Communication.Messages;
using NUnit.Engine.Communication.Protocols;
using NUnit.Engine.Communication.Transports.Tcp;
using NUnit.Framework;

namespace NUnit.Engine.Communication
{
    /// <summary>Exercises stop commands over the real agent wire while a synchronous run is blocked.</summary>
    public class TestAgentTcpStopTests
    {
        [Test]
        public void RequestStopReachesRunnerBeforeSynchronousRunReturns()
        {
            using var entered = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            using var stopped = new ManualResetEventSlim();
            var runner = Substitute.For<ITestEngineRunner>();
            runner.Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>()).Returns(call =>
            {
                entered.Set();
                if (!finish.Wait(10000))
                    throw new TimeoutException("The test did not release its fake runner.");
                call.Arg<ITestEventListener>().OnTestEvent("<test-case id='1' result='Passed'/>");
                return new TestEngineResult("<test-suite result='Passed'/>");
            });
            runner.When(value => value.RequestStop()).Do(_ => stopped.Set());
            using var session = new AgentSession(runner);
            Socket socket = session.Socket;
            try
            {
                Send(socket, MessageCode.RunCommand, "<filter/>");
                Assert.That(entered.Wait(5000), Is.True, "The run must have entered before requesting stop.");
                Send(socket, MessageCode.RequestStopCommand);
                Assert.That(stopped.Wait(1500), Is.True, "Stop must be read while the synchronous run remains blocked.");
                finish.Set();
                var reader = new SocketReader(socket, new BinarySerializationProtocol());
                Assert.That(reader.GetNextMessage().Code, Is.EqualTo(MessageCode.ProgressReport));
                var result = reader.GetNextMessage();
                Assert.That(result.Code, Is.EqualTo(MessageCode.CommandResult));
                Assert.That(result.Data, Does.Contain("Passed"));
                Send(socket, MessageCode.StopAgent);
                Assert.That(session.Agent.WaitForStop(5000), Is.True);
                runner.Received(1).RequestStop();
                runner.Received(1).Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>());
            }
            finally
            {
                finish.Set();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExecutionOrStopFailureClosesConnectionWithoutAResult(bool failStop)
        {
            using var entered = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            var runner = Substitute.For<ITestEngineRunner>();
            runner.Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>()).Returns(_ =>
            {
                entered.Set();
                if (!failStop)
                    throw new InvalidOperationException("execution failed");
                if (!finish.Wait(10000))
                    throw new TimeoutException("The test did not release its fake runner.");
                return new TestEngineResult("<test-suite result='Passed'/>");
            });
            runner.When(value => value.RequestStop()).Do(_ => throw new InvalidOperationException("stop failed"));
            using var session = new AgentSession(runner);
            Socket socket = session.Socket;
            try
            {
                Send(socket, MessageCode.RunCommand, "<filter/>");
                Assert.That(entered.Wait(5000), Is.True);
                if (failStop)
                    Send(socket, MessageCode.RequestStopCommand);
                Assert.That(session.Agent.WaitForStop(5000), Is.True, "An infrastructure failure must stop the agent.");
                try
                {
                    Assert.That(socket.Receive(new byte[1]), Is.Zero, "The failed operation must not fabricate a terminal result.");
                }
                catch (SocketException exception)
                {
                    Assert.That(exception.SocketErrorCode, Is.AnyOf(SocketError.ConnectionReset, SocketError.ConnectionAborted));
                }
            }
            finally
            {
                finish.Set();
            }
        }

        /// <summary>
        /// Concurrent framework callbacks must remain complete, independently decodable frames.
        /// </summary>
        [Test]
        public void ConcurrentProgressCallbacksPreserveEveryCompleteFrame()
        {
            const int CALLBACK_COUNT = 64;
            var runner = Substitute.For<ITestEngineRunner>();
            runner.Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>()).Returns(call =>
            {
                var callbacks = new Task[4];
                for (int worker = 0; worker < callbacks.Length; worker++)
                {
                    int first = worker;
                    callbacks[worker] = Task.Run(() =>
                    {
                        for (int index = first; index < CALLBACK_COUNT; index += callbacks.Length)
                            call.Arg<ITestEventListener>().OnTestEvent(ProgressReport(index));
                    });
                }
                if (!Task.WaitAll(callbacks, 10000))
                    throw new TimeoutException("Concurrent callbacks did not finish.");
                return new TestEngineResult("<test-suite result='Passed'/>");
            });

            using var session = new AgentSession(runner);
            Send(session.Socket, MessageCode.RunCommand, "<filter/>");
            var reader = new SocketReader(session.Socket, new BinarySerializationProtocol());
            var received = new HashSet<string>();
            for (int index = 0; index < CALLBACK_COUNT; index++)
            {
                var message = reader.GetNextMessage();
                Assert.That(message.Code, Is.EqualTo(MessageCode.ProgressReport));
                Assert.That(received.Add(message.Data!), Is.True, "A callback must not be duplicated.");
            }

            Assert.Multiple(() =>
            {
                for (int index = 0; index < CALLBACK_COUNT; index++)
                    Assert.That(received, Does.Contain(ProgressReport(index)));
                Assert.That(reader.GetNextMessage().Code, Is.EqualTo(MessageCode.CommandResult));
            });
        }

        /// <summary>
        /// A second operation must not use a runner concurrently with an active synchronous run.
        /// </summary>
        [TestCase(MessageCode.LoadCommand)]
        [TestCase(MessageCode.RunCommand)]
        public void ConflictingCommandFailsWithoutReenteringTheRunner(string command)
        {
            using var entered = new ManualResetEventSlim();
            using var finish = new ManualResetEventSlim();
            using var returned = new ManualResetEventSlim();
            var runner = Substitute.For<ITestEngineRunner>();
            runner.Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>()).Returns(_ =>
            {
                entered.Set();
                try
                {
                    if (!finish.Wait(10000))
                        throw new TimeoutException("The test did not release its fake runner.");
                    return new TestEngineResult("<test-suite result='Passed'/>");
                }
                finally
                {
                    returned.Set();
                }
            });

            using var session = new AgentSession(runner);
            try
            {
                Send(session.Socket, MessageCode.RunCommand, "<filter/>");
                Assert.That(entered.Wait(5000), Is.True);
                Send(session.Socket, command, "<filter/>");

                Assert.That(session.Agent.WaitForStop(5000), Is.True);
                try
                {
                    Assert.That(session.Socket.Receive(new byte[1]), Is.Zero);
                }
                catch (SocketException exception)
                {
                    Assert.That(exception.SocketErrorCode, Is.AnyOf(SocketError.ConnectionReset, SocketError.ConnectionAborted));
                }
                runner.DidNotReceive().Load();
                runner.Received(1).Run(Arg.Any<ITestEventListener>(), Arg.Any<TestFilter>());
            }
            finally
            {
                finish.Set();
                Assert.That(returned.Wait(5000), Is.True, "The fake execution must unwind before its synchronization handles are disposed.");
            }
        }

        private static string ProgressReport(int index) => $"<test-output id='{index}'>{new string('x', 16384)}</test-output>";

        private sealed class AgentSession : IDisposable
        {
            private readonly TcpListener _listener;

            public AgentSession(ITestEngineRunner runner)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Agent = new StubAgent(runner);
                Agent.Transport = new TestAgentTcpTransport(Agent, _listener.LocalEndpoint.ToString()!);
                try
                {
                    Assert.That(Agent.Start(), Is.True);
                    Socket = _listener.AcceptSocket();
                    Socket.ReceiveTimeout = 5000;
                    Socket.SendTimeout = 5000;
                    var identity = new byte[16];
                    int offset = 0;
                    while (offset < identity.Length)
                    {
                        int count = Socket.Receive(identity, offset, identity.Length - offset, SocketFlags.None);
                        if (count == 0)
                            throw new InvalidOperationException("The agent closed before sending its identity.");
                        offset += count;
                    }
                    Assert.That(new Guid(identity), Is.EqualTo(Agent.Id));
                    Send(Socket, MessageCode.CreateRunner, new TestPackage("mock-assembly.dll").SubPackages[0].ToXml());
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public StubAgent Agent { get; }
            public Socket Socket { get; } = null!;

            public void Dispose()
            {
                Agent.Stop();
                Socket?.Dispose();
                _listener.Stop();
                Agent.Dispose();
            }
        }

        private static void Send(Socket socket, string code, string? data = null)
        {
            var bytes = new BinarySerializationProtocol().Encode(new TestEngineMessage(code, data));
            int offset = 0;
            while (offset < bytes.Length)
            {
                int count = socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
                if (count == 0)
                    throw new InvalidOperationException("The connection closed before the command was sent.");
                offset += count;
            }
        }

        private sealed class StubAgent : RemoteTestAgent
        {
            private readonly ITestEngineRunner _runner;

            public StubAgent(ITestEngineRunner runner) : base(Guid.NewGuid())
            {
                _runner = runner;
            }

            public override ITestEngineRunner CreateRunner(TestPackage package) => _runner;
        }
    }
}
