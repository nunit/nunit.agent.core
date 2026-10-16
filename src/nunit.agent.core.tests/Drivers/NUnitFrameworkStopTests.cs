// Copyright (c) Charlie Poole, Rob Prouse and Contributors. MIT License - see LICENSE.txt

using System;
using System.Reflection;
using NUnit.Framework;

namespace NUnit.Engine.Drivers
{
    /// <summary>Checks the void stop APIs exposed by old and current frameworks.</summary>
    public class NUnitFrameworkStopTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void LegacyBooleanStopPreservesForceArgument(bool force)
        {
            var controller = new LegacyController();
            CreateApi(controller).StopRun(force);
            Assert.That(controller.Force, Is.EqualTo(force));
        }

        [Test]
        public void CurrentParameterlessStopIsInvoked()
        {
            var controller = new CurrentController();
            CreateApi(controller).StopRun(false);
            Assert.That(controller.Called, Is.True);
        }

        [Test]
        public void CurrentFrameworkDoesNotPretendToSupportForcedStop()
        {
            var controller = new CurrentController();
            Assert.Throws<NotSupportedException>(() => CreateApi(controller).StopRun(true));
            Assert.That(controller.Called, Is.False);
        }

        [Test]
        public void MissingStopApiIsReportedAsUnsupportedFramework()
        {
            Assert.Throws<NUnitEngineException>(() => CreateApi(new object()).StopRun(false));
        }

        [Test]
        public void StopBeforeLoadExplainsTheMissingLoad()
        {
            var api = new NUnitFrameworkApi2018("99", typeof(TestAttribute).Assembly.GetName());
            Assert.Throws<InvalidOperationException>(() => api.StopRun(false));
        }

        [Test]
        public void FrameworkStopExceptionIsPreserved()
        {
            var exception = Assert.Throws<TargetInvocationException>(() => CreateApi(new ThrowingController()).StopRun(false));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(exception.InnerException!.Message, Is.EqualTo("stop failed"));
        }

        private static NUnitFrameworkApi2018 CreateApi(object controller)
        {
            var api = new NUnitFrameworkApi2018("99", typeof(TestAttribute).Assembly.GetName());
            typeof(NUnitFrameworkApi2018).GetField("_frameworkController", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(api, controller);
            typeof(NUnitFrameworkApi2018).GetField("_frameworkControllerType", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(api, controller.GetType());
            return api;
        }

        private sealed class LegacyController
        {
            public bool? Force { get; private set; }

            public void StopRun(bool force) => Force = force;
        }

        private sealed class CurrentController
        {
            public bool Called { get; private set; }

            public void StopRun() => Called = true;
        }

        private sealed class ThrowingController
        {
            private readonly Exception _failure = new InvalidOperationException("stop failed");

            public void StopRun() => throw _failure;
        }
    }
}
