// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

#if !NETCOREAPP2_1
using System.Linq;
using NUnit.Framework;

namespace TestCentric.Engine.Services
{
    using Fakes;
    using NUnit.Engine;

    public class TestAgencyTests
    {
        private ServiceContext _services;
        private TestAgency _testAgency;

        public static readonly string[] BUILTIN_AGENTS = new string[]
        {
            "Net20AgentLauncher", "Net40AgentLauncher", "NetCore21AgentLauncher", "NetCore31AgentLauncher", "Net50AgentLauncher"
        };

        [SetUp]
        public void CreateTestAgency()
        {
            _services = new ServiceContext();
            _services.Add(new TestAgency("TestAgencyTest", 0));
            _services.ServiceManager.StartServices();
            _testAgency = _services.GetService<TestAgency>();
            Assert.That(_testAgency.Status, Is.EqualTo(ServiceStatus.Started));
        }

        [TearDown]
        public void StopServices()
        {
            _testAgency.StopService();
            Assert.That(_testAgency.Status, Is.EqualTo(ServiceStatus.Stopped));
        }

        [Test]
        public void AvailableAgents()
        {
            Assert.That(_testAgency.GetAvailableAgents().Select((info) => info.AgentName),
                Is.EqualTo(BUILTIN_AGENTS));
        }

        [TestCase("net-2.0", "Net20AgentLauncher", "Net40AgentLauncher")]
        [TestCase("net-3.5", "Net20AgentLauncher", "Net40AgentLauncher")]
        [TestCase("net-4.0", "Net40AgentLauncher")]
        [TestCase("net-4.8", "Net40AgentLauncher")]
        [TestCase("netcore-1.1", "NetCore21AgentLauncher", "NetCore31AgentLauncher", "Net50AgentLauncher")]
        [TestCase("netcore-2.0", "NetCore21AgentLauncher", "NetCore31AgentLauncher", "Net50AgentLauncher")]
        [TestCase("netcore-2.1", "NetCore21AgentLauncher", "NetCore31AgentLauncher", "Net50AgentLauncher")]
        [TestCase("netcore-3.0", "NetCore31AgentLauncher", "Net50AgentLauncher")]
        [TestCase("netcore-3.1", "NetCore31AgentLauncher", "Net50AgentLauncher")]
        [TestCase("netcore-5.0", "Net50AgentLauncher")]
        public void GetAgentsForPackage(string targetRuntime, params string[] expectedAgents)
        {
            var package = new TestPackage("../net35/mock-assembly.dll");
            package.AddSetting(EnginePackageSettings.TargetRuntimeFramework, targetRuntime);

            Assert.That(_testAgency.GetAvailableAgents(package).Select((info) => info.AgentName),
                Is.EqualTo(expectedAgents));
        }
    }
}
#endif
