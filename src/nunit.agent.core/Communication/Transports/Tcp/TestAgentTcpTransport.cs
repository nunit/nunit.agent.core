// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Engine.Agents;
using NUnit.Engine.Communication.Messages;
using NUnit.Engine.Communication.Protocols;

namespace NUnit.Engine.Communication.Transports.Tcp
{
    /// <summary>
    /// TestAgentTcpTransport uses TCP to support
    /// a TestAgent in communicating with a TestAgency and
    /// with the runners that make use of it.
    /// </summary>
    public class TestAgentTcpTransport : ITestAgentTransport, ITestEventListener
    {
        private static readonly Logger log = InternalTrace.GetLogger(typeof(TestAgentTcpTransport));
        private static readonly char[] PortSeparator = [':'];

        private readonly string _agencyUrl;
        private Socket? _clientSocket;
        private ITestEngineRunner? _runner;
        private readonly object _sendLock = new object();
        private int _runInProgress;

        public TestAgentTcpTransport(RemoteTestAgent agent, string serverUrl)
        {
            Guard.ArgumentNotNull(agent);
            Agent = agent;

            Guard.ArgumentNotNullOrEmpty(serverUrl);
            _agencyUrl = serverUrl;

            var parts = serverUrl.Split(PortSeparator);
            Guard.ArgumentValid(parts.Length == 2, "Invalid server address specified. Must be a valid endpoint including the port number", nameof(serverUrl));
            ServerEndPoint = new IPEndPoint(IPAddress.Parse(parts[0]), int.Parse(parts[1]));
        }

        public TestAgent Agent { get; }

        public IPEndPoint ServerEndPoint { get; }

        public bool Start()
        {
            log.Info("Connecting to TestAgency at {0}", _agencyUrl);

            // Connect to the server
            _clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _clientSocket.Connect(ServerEndPoint);

            // Immediately upon connection send the agent Id as a raw byte array
            _clientSocket.Send(Agent.Id.ToByteArray());

            // Start the loop that reads and executes commands
            Thread commandLoop = new Thread(CommandLoop);
            commandLoop.Start();

            return true;
        }

        public void Stop()
        {
            Agent.StopSignal.Set();
            _clientSocket?.Close();
        }

        public ITestEngineRunner CreateRunner(TestPackage package)
        {
            return Agent.CreateRunner(package);
        }

        private void CommandLoop()
        {
            bool keepRunning = true;
            var socketReader = new SocketReader(_clientSocket.ShouldNotBeNull(), new BinarySerializationProtocol());

            try
            {
                while (keepRunning)
                {
                    var command = socketReader.GetNextMessage();

                    // Only stop commands may operate on a runner while its run is active.
                    if (Volatile.Read(ref _runInProgress) != 0
                        && command.Code != MessageCode.RequestStopCommand
                        && command.Code != MessageCode.ForcedStopCommand
                        && command.Code != MessageCode.StopAgent)
                        throw new NUnitEngineException("A run is already active on this agent.");

                    switch (command.Code)
                    {
                        case MessageCode.CreateRunner:
                            var package = PackageHelper.FromXml(command.Data!);
                            _runner = CreateRunner(package);
                            break;
                        case MessageCode.LoadCommand:
                            SendResult(_runner.ShouldNotBeNull().Load().Xml.OuterXml);
                            break;
                        case MessageCode.ReloadCommand:
                            SendResult(_runner.ShouldNotBeNull().Reload().Xml.OuterXml);
                            break;
                        case MessageCode.UnloadCommand:
                            _runner.ShouldNotBeNull().Unload();
                            break;
                        case MessageCode.ExploreCommand:
                            var filter = new TestFilter(command.Data!);
                            SendResult(_runner.ShouldNotBeNull().Explore(filter).Xml.OuterXml);
                            break;
                        case MessageCode.CountCasesCommand:
                            filter = new TestFilter(command.Data!);
                            SendResult(_runner.ShouldNotBeNull().CountTestCases(filter).ToString());
                            break;
                        case MessageCode.RunCommand:
                            filter = new TestFilter(command.Data!);
                            StartRun(filter);
                            break;

                        case MessageCode.RunAsyncCommand:
                            filter = new TestFilter(command.Data!);
                            _runner.ShouldNotBeNull().RunAsync(this, filter);
                            break;

                        case MessageCode.RequestStopCommand:
                            _runner.ShouldNotBeNull().RequestStop();
                            break;

                        case MessageCode.ForcedStopCommand:
                            _runner.ShouldNotBeNull().ForcedStop();
                            break;

                        case MessageCode.StopAgent:
                            keepRunning = false;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // There is no error-result frame in this protocol. Close the connection
                // so the engine reports an infrastructure failure rather than success.
                if (!Agent.StopSignal.WaitOne(0))
                    log.Error("Agent command processing failed.", ex);
            }
            finally
            {
                Stop();
            }
        }

        /// <summary>
        /// Keeps the command reader available for stop requests throughout a synchronous run.
        /// The worker still sends the same progress frames and final result as the old loop.
        /// </summary>
        private void StartRun(TestFilter filter)
        {
            var runner = _runner.ShouldNotBeNull();
            Interlocked.Exchange(ref _runInProgress, 1);
            var worker = new Thread(() =>
            {
                try
                {
                    var result = runner.Run(this, filter).Xml.OuterXml;
                    // The runner is quiescent before its terminal result is visible.
                    Interlocked.Exchange(ref _runInProgress, 0);
                    SendResult(result);
                }
                catch (Exception ex)
                {
                    log.Error("Agent execution failed.", ex);
                    Stop();
                }
            })
            { IsBackground = true };
            worker.Start();
        }

        private void SendResult(string result)
        {
            var resultMessage = new TestEngineMessage(MessageCode.CommandResult, result);
            var bytes = new BinarySerializationProtocol().Encode(resultMessage);
            SendMessage(bytes);
        }

        public void OnTestEvent(string report)
        {
            var progressMessage = new TestEngineMessage(MessageCode.ProgressReport, report);
            var bytes = new BinarySerializationProtocol().Encode(progressMessage);
            SendMessage(bytes);
        }

        // A frame must be sent in full without interleaving another callback's frame.
        private void SendMessage(byte[] bytes)
        {
            lock (_sendLock)
            {
                var socket = _clientSocket.ShouldNotBeNull();
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int sent = socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
                    if (sent == 0)
                        throw new SocketException((int)SocketError.ConnectionReset);
                    offset += sent;
                }
            }
        }
    }
}
