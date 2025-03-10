// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using NUnit.Engine;
using NUnit.Engine.Extensibility;
using TestCentric.Engine.Drivers;
using TestCentric.Engine.Internal;

namespace TestCentric.Engine.Runners
{
    /// <summary>
    /// DirectTestRunner is the abstract base for runners
    /// that deal directly with a framework driver.
    /// </summary>
    public abstract class TestAgentRunner : AbstractTestRunner
    {
        // TestAgentRunner loads and runs tests in a particular AppDomain using
        // one driver per assembly. All test assemblies are ultimately executed by
        // an agent using one of its derived classes, either LocalTestRunner
        // or TestDomainRunner.
        //
        // TestAgentRunner creates an appropriate framework driver for the assembly
        // specified in the TestPackage.

        private static readonly Logger log = InternalTrace.GetLogger(typeof(TestAgentRunner));

        private IFrameworkDriver _driver;

        private ProvidedPathsAssemblyResolver _assemblyResolver;

        protected AppDomain TestDomain { get; set; }

        public TestAgentRunner(TestPackage package) : base(package)
        {
            Guard.ArgumentNotNull(package, nameof(package));
            Guard.ArgumentValid(package.SubPackages.Count == 0, "Only one assembly may be loaded by an agent", nameof(package));
            Guard.ArgumentValid(package.FullName != null, "Package may not be anonymous", nameof(package));
            Guard.ArgumentValid(package.IsAssemblyPackage(), "Must be an assembly package", nameof(package));

            // Bypass the resolver if not in the default AppDomain. This prevents trying to use the resolver within
            // NUnit's own automated tests (in a test AppDomain) which does not make sense anyway.
            if (AppDomain.CurrentDomain.IsDefaultAppDomain())
            {
                _assemblyResolver = new ProvidedPathsAssemblyResolver();
                _assemblyResolver.Install();
            }
        }

        /// <summary>
        /// Explores a previously loaded TestPackage and returns information
        /// about the tests found.
        /// </summary>
        /// <param name="filter">The TestFilter to be used to select tests</param>
        /// <returns>
        /// A TestEngineResult.
        /// </returns>
        public override TestEngineResult Explore(TestFilter filter)
        {
            EnsurePackageIsLoaded();

            try
            {
                return new TestEngineResult(_driver.Explore(filter.Text));
            }
            catch (Exception ex) when (!(ex is NUnitEngineException))
            {
                throw new NUnitEngineException("An exception occurred in the driver while exploring tests.", ex);
            }
        }

        /// <summary>
        /// Load a TestPackage for exploration or execution
        /// </summary>
        /// <returns>A TestEngineResult.</returns>
        protected override TestEngineResult LoadPackage()
        {
            var testFile = TestPackage.FullName;
            log.Info($"Loading package {testFile}");

            string targetFramework = TestPackage.GetSetting(EnginePackageSettings.ImageTargetFrameworkName, (string)null);
            string frameworkReference = TestPackage.GetSetting(EnginePackageSettings.ImageTestFrameworkReference, (string)null);
            bool skipNonTestAssemblies = TestPackage.GetSetting(EnginePackageSettings.SkipNonTestAssemblies, false);

            if (_assemblyResolver != null && !TestDomain.IsDefaultAppDomain()
                && TestPackage.GetSetting(EnginePackageSettings.ImageRequiresDefaultAppDomainAssemblyResolver, false))
            {
                // It's OK to do this in the loop because the Add method
                // checks to see if the path is already present.
                _assemblyResolver.AddPathFromFile(testFile);
            }

            _driver = GetDriver(TestDomain, testFile, targetFramework, skipNonTestAssemblies);
            _driver.ID = TestPackage.ID;
            log.Debug($"Using driver {_driver.GetType().Name}");
            
            
            try
            {
                return new TestEngineResult(_driver.Load(testFile, TestPackage.Settings));
            }
            catch (Exception ex) when (!(ex is NUnitEngineException))
            {
                throw new NUnitEngineException("An exception occurred in the driver while loading tests.", ex);
            }
        }

        // TODO: This is a temporary fix while we decide how to handle
        // loadable drivers outside of the context of DriverService
        public IFrameworkDriver GetDriver(AppDomain domain, string assemblyPath, string targetFramework, bool skipNonTestAssemblies)
        {
            if (!File.Exists(assemblyPath))
                return new InvalidAssemblyFrameworkDriver(assemblyPath, "File not found: " + assemblyPath);

            var ext = Path.GetExtension(assemblyPath).ToLowerInvariant();
            if (ext != ".dll" && ext != ".exe")
                return new InvalidAssemblyFrameworkDriver(assemblyPath, "File type is not supported");

#if !NETSTANDARD
            if (targetFramework != null)
            {
                // This takes care of an issue with Roslyn. It may get fixed, but we still
                // have to deal with assemblies having this setting. I'm assuming that
                // any true Portable assembly would have a Profile as part of its name.
                var platform = targetFramework == ".NETPortable,Version=v5.0"
                    ? ".NETStandard"
                    : targetFramework.Split(new char[] { ',' })[0];
                if (platform == "Silverlight" || platform == ".NETPortable" || platform == ".NETStandard" || platform == ".NETCompactFramework")
                    return new InvalidAssemblyFrameworkDriver(assemblyPath, platform + " test assemblies are not supported by this version of the engine");
            }
#endif

            try
            {
                var factories = new IDriverFactory[]
                {
                    new NUnit3DriverFactory(),
                    //new NUnit2DriverFactory()
                };

                var frameworkAssemblyName = TestPackage.GetSetting(EnginePackageSettings.ImageTestFrameworkReference, (string)null);
                if (frameworkAssemblyName != null)
                {
                    var referencedFramework = new AssemblyName(frameworkAssemblyName);
                    foreach (var factory in factories)
                    {
                        if (factory.IsSupportedTestFramework(referencedFramework))
#if NETSTANDARD
                            return factory.GetDriver(referencedFramework);
#else
                            return factory.GetDriver(domain, referencedFramework);
#endif
                    }
                }
            }
            catch (BadImageFormatException ex)
            {
                return new InvalidAssemblyFrameworkDriver(assemblyPath, ex.Message);
            }

            return new InvalidAssemblyFrameworkDriver(assemblyPath,
                $"No suitable test driver was found. Either the assembly contains no tests or it uses an unsupported test framework.");
        }

        /// <summary>
        /// Count the test cases that would be run under
        /// the specified filter.
        /// </summary>
        /// <param name="filter">A TestFilter</param>
        /// <returns>The count of test cases</returns>
        public override int CountTestCases(TestFilter filter)
        {
            EnsurePackageIsLoaded();

            try
            {
                return _driver.CountTestCases(filter.Text);
            }
            catch (Exception ex) when (!(ex is NUnitEngineException))
            {
                throw new NUnitEngineException("An exception occurred in the driver while counting test cases.", ex);
            }
        }


        /// <summary>
        /// Run the tests in the loaded TestPackage.
        /// </summary>
        /// <param name="listener">An ITestEventHandler to receive events</param>
        /// <param name="filter">A TestFilter used to select tests</param>
        /// <returns>
        /// A TestEngineResult giving the result of the test execution
        /// </returns>
        protected override TestEngineResult RunTests(ITestEventListener listener, TestFilter filter)
        {
            log.Info($"Running tests for {TestPackage.FullName}");

            EnsurePackageIsLoaded();

            string driverResult;

            if (filter.Excludes(TestPackage))
                driverResult = $"<test-suite type='Assembly' name='{TestPackage.Name}' fullname='{TestPackage.FullName}' result='Skipped'><reason><message>Filter excludes this assembly</message></reason></test-suite>";
            else
                try
                {
                    driverResult = _driver.Run(listener, filter.Text);
                    log.Debug("Got driver Result");
                }
                catch (Exception ex) when (!(ex is NUnitEngineException))
                {
                    log.Debug("An exception occurred in the driver while running tests.", ex);
                    throw new NUnitEngineException("An exception occurred in the driver while running tests.", ex);
                }

            if (_assemblyResolver != null)
                _assemblyResolver.RemovePathFromFile(TestPackage.FullName);

            return new TestEngineResult(driverResult);
        }

        /// <summary>
        /// Cancel the ongoing test run. If no  test is running, the call is ignored.
        /// </summary>
        /// <param name="force">If true, cancel any ongoing test threads, otherwise wait for them to complete.</param>
        public override void StopRun(bool force)
        {
            if (force)
            {
                log.Info("Cancelling test run");
                Environment.Exit(0);
            }
            else
            {
                log.Info("Requesting stop");

                EnsurePackageIsLoaded();

                try
                {
                    _driver.StopRun(false);
                }
                catch (Exception ex) when (!(ex is NUnitEngineException))
                {
                    throw new NUnitEngineException("An exception occurred in the driver while stopping the run.", ex);
                }
            }
        }

        private void EnsurePackageIsLoaded()
        {
            if (!IsPackageLoaded)
                LoadResult = LoadPackage();
        }
    }
}
