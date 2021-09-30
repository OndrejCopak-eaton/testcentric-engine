
//////////////////////////////////////////////////////////////////////
// TESTING HELPER METHODS
//////////////////////////////////////////////////////////////////////

static void CheckTestErrors(ref List<string> errorDetail)
{
    if(errorDetail.Count != 0)
    {
        var copyError = new List<string>();
        copyError = errorDetail.Select(s => s).ToList();
        errorDetail.Clear();
        throw new Exception("One or more tests failed, breaking the build.\n"
                              + copyError.Aggregate((x,y) => x + "\n" + y));
    }
}

private void RunNUnitLite(string testName, string framework, string directory)
{
	bool isDotNetCore = framework.StartsWith("netcoreapp");
	string ext = isDotNetCore ? ".dll" : ".exe";
	string testPath = directory + testName + ext;

	Information("==================================================");
	Information("Running tests under " + framework);
	Information("==================================================");

	int rc = isDotNetCore
		? StartProcess("dotnet", testPath)
		: StartProcess(testPath);

	if (rc > 0)
		ErrorDetail.Add($"{testName}: {rc} tests failed running under {framework}");
	else if (rc < 0)
		ErrorDetail.Add($"{testName} returned rc = {rc} running under {framework}");
}

// Class that knows how to run tests against either GUI
public class GuiTester
{
	private BuildParameters _parameters;

	public GuiTester(BuildParameters parameters)
	{
		_parameters = parameters;
	}

	public int RunGuiUnattended(string runnerPath, string arguments)
	{
		if (!arguments.Contains(" --run"))
			arguments += " --run";
		if (!arguments.Contains(" --unattended"))
			arguments += " --unattended";

		return RunGui(runnerPath, arguments);
	}

	public int RunGui(string runnerPath, string arguments)
	{
		return _parameters.Context.StartProcess(runnerPath, new ProcessSettings()
		{
			Arguments = arguments,
			WorkingDirectory = _parameters.OutputDirectory
		});
	}
}

// Representation of a single test to be run against a pre-built package.
// Each test has a Level, with the following values defined...
//  0 Do not run - used for temporarily disabling a test
//  1 Run for all CI tests - that is every time we test packages
//  2 Run only on PRs, dev builds and when publishing
//  3 Run only when publishing
public struct PackageTest
{
	public int Level;
	public string Description;
	public string Runner;
	public string Arguments;
	public ExpectedResult ExpectedResult;
	public string[] ExtensionsNeeded;
	
	public PackageTest(int level, string description, string runner, string arguments, ExpectedResult expectedResult, params string[] extensionsNeeded)
	{
		Level = level;
		Description = description;
		Runner = runner;
		Arguments = arguments;
		ExpectedResult = expectedResult;
		ExtensionsNeeded = extensionsNeeded;
	}
}

const string DEFAULT_TEST_RESULT_FILE = "TestResult.xml";

// Abstract base for all package testers. Currently, we only
// have one package of each type (Zip, NuGet, Chocolatey).
public abstract class PackageTester : GuiTester
{
	protected BuildParameters _parameters;
	private ICakeContext _context;

	public PackageTester(BuildParameters parameters)
		: base(parameters) 
	{
		_parameters = parameters;
		_context = parameters.Context;

		PackageTests = new List<PackageTest>();

		// Level 1 tests are run each time we build the packages
		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll under .NET 4.5", StandardRunner,
			"mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 31,
				Passed = 18,
				Failed = 5,
				Warnings = 0,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "net-4.5") }
			})) ;
		
		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll under .NET 3.5", StandardRunner,
			"engine-tests/net35/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 36,
				Passed = 23,
				Failed = 5,
				Warnings = 1,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "net-2.0") }
			}));
		
		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll under .NET Core 2.1", StandardRunner,
			"engine-tests/netcoreapp2.1/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 36,
				Passed = 23,
				Failed = 5,
				Warnings = 1,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "netcore-2.1") }
			}));

		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll under .NET Core 3.1", StandardRunner,
			"engine-tests/netcoreapp3.1/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 36,
				Passed = 23,
				Failed = 5,
				Warnings = 1,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "netcore-3.1") }
			}));

		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll targeting .NET Core 1.1", StandardRunner,
			"engine-tests/netcoreapp1.1/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 36,
				Passed = 23,
				Failed = 5,
				Warnings = 1,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "netcore-1.1") }
			}));

		PackageTests.Add(new PackageTest(1, "Run mock-assembly.dll under .NET 5.0", StandardRunner,
			"engine-tests/net5.0/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 32,
				Passed = 19,
				Failed = 5,
				Warnings = 1,
				Inconclusive = 1,
				Skipped = 7,
				Assemblies = new[] { new ExpectedAssemblyResult("mock-assembly.dll", "netcore-5.0") }
			}));

		PackageTests.Add(new PackageTest(1, "Run different builds of mock-assembly.dll together", StandardRunner,
			"engine-tests/net35/mock-assembly.dll engine-tests/netcoreapp2.1/mock-assembly.dll",
			new ExpectedResult("Failed")
			{
				Total = 72,
				Passed = 46,
				Failed = 10,
				Warnings = 2,
				Inconclusive = 2,
				Skipped = 14,
				Assemblies = new[] {
					new ExpectedAssemblyResult("mock-assembly.dll", "net-2.0"),
					new ExpectedAssemblyResult("mock-assembly.dll", "netcore-2.1") }
			}));

		// Level 2 tests are run for PRs and when packages will be published

		//PackageTests.Add(new PackageTest(2, "Run mock-assembly.dll built for NUnit V2", StandardRunner,
		//	"v2-tests/mock-assembly.dll",
		//	new ExpectedResult("Failed")
		//	{
		//		Total = 28,
		//		Passed = 18,
		//		Failed = 5,
		//		Warnings = 0,
		//		Inconclusive = 1,
		//		Skipped = 4
		//	},
		//	NUnitV2Driver));

		// TODO: Use --config option when it's supported by the extension.
		// Current test relies on the fact that the Release config appears
		// first in the project file.
		if (_parameters.Configuration == "Release")
			{
				PackageTests.Add(new PackageTest(2, "Run an NUnit project", StandardRunner,
					"../../GuiTests.nunit --trace=Debug",
					new ExpectedResult("Passed")
					{
						Assemblies = new[] { 
							new ExpectedAssemblyResult("TestCentric.Gui.Tests.dll", "net-4.5"),
							new ExpectedAssemblyResult("TestCentric.Gui.Model.Tests.dll", "net-4.5") }
					},
					NUnitProjectLoader));
			}
	}

	protected abstract string PackageName { get; }
	protected abstract FilePath PackageUnderTest { get; }
	protected abstract string PackageTestDirectory { get; }
	protected abstract string PackageTestBinDirectory { get; }
	protected abstract string ExtensionInstallDirectory { get; }

	protected virtual string NUnitV2Driver => "NUnit.Extension.NUnitV2Driver";
	protected virtual string NUnitProjectLoader => "NUnit.Extension.NUnitProjectLoader";

	// NOTE: Currently, we use the same tests for all packages. There seems to be
	// no reason for the three packages to differ in capability so the only reason
	// to limit tests on some of them would be efficiency... so far not a problem.
	private List<PackageTest> PackageTests { get; }

	protected string StandardRunner => PackageTestBinDirectory + GUI_RUNNER;

	public void RunAllTests()
	{
		Console.WriteLine("Testing package " + PackageName);

		RunPackageTests(_parameters.PackageTestLevel);

		CheckTestErrors(ref ErrorDetail);
	}

	private void CheckExtensionIsInstalled(string extension)
	{
		bool alreadyInstalled = _context.GetDirectories($"{ExtensionInstallDirectory}{extension}.*").Count > 0;

		if (!alreadyInstalled)
		{
			DisplayBanner($"Installing {extension}");
			InstallEngineExtension(extension);
		}
	}

	protected abstract void InstallEngineExtension(string extension);

	private void RunPackageTests(int testLevel)
	{
		var reporter = new ResultReporter(PackageName);

		foreach (var packageTest in PackageTests)
		{
			if (packageTest.Level > 0 && packageTest.Level <= testLevel)
			{
				foreach (string extension in packageTest.ExtensionsNeeded)
					CheckExtensionIsInstalled(extension);

				var resultFile = _parameters.OutputDirectory + DEFAULT_TEST_RESULT_FILE;
				// Delete result file ahead of time so we don't mistakenly
				// read a left-over file from another test run. Leave the
				// file after the run in case we need it to debug a failure.
				if (_context.FileExists(resultFile))
					_context.DeleteFile(resultFile);
				
				DisplayBanner(packageTest.Description);
				DisplayTestEnvironment(packageTest);

				RunGuiUnattended(packageTest.Runner, packageTest.Arguments);

				try
                {
					var result = new ActualResult(resultFile);
					var report = new PackageTestReport(packageTest, result);
					reporter.AddReport(report);

					Console.WriteLine(report.Errors.Count == 0
						? "\nSUCCESS: Test Result matches expected result!"
						: "\nERROR: Test Result not as expected!");
				}
				catch (Exception ex)
                {
					reporter.AddReport(new PackageTestReport(packageTest, ex));

					Console.WriteLine("\nERROR: No result found!");
				}
			}
		}

		bool anyErrors = reporter.ReportResults();
		Console.WriteLine();

		// All package tests are run even if one of them fails. If there are
		// any errors,  we stop the run at this point.
		if (anyErrors)
			throw new Exception("One or more package tests had errors!");
	}

	private void DisplayBanner(string message)
	{
		Console.WriteLine("\n========================================");;
		Console.WriteLine(message);
		Console.WriteLine("========================================");
	}

	private void DisplayTestEnvironment(PackageTest test)
	{
		Console.WriteLine("Test Environment");
		Console.WriteLine($"   OS Version: {Environment.OSVersion.VersionString}");
		Console.WriteLine($"  CLR Version: {Environment.Version}");
		Console.WriteLine($"       Runner: {test.Runner}");
		Console.WriteLine($"    Arguments: {test.Arguments}");
		Console.WriteLine();
	}

    protected FileCheck HasFile(string file) => HasFiles(new [] { file });
    protected FileCheck HasFiles(params string[] files) => new FileCheck(files);  

    protected DirectoryCheck HasDirectory(string dir) => new DirectoryCheck(dir);
}

public class ZipPackageTester : PackageTester
{
	public ZipPackageTester(BuildParameters parameters) : base(parameters) { }

	protected override string PackageName => _parameters.ZipPackageName;
	protected override FilePath PackageUnderTest => _parameters.ZipPackage;
	protected override string PackageTestDirectory => _parameters.ZipTestDirectory;
	protected override string PackageTestBinDirectory => PackageTestDirectory + "bin/";
	protected override string ExtensionInstallDirectory => PackageTestBinDirectory + "addins/";
	
	protected override void InstallEngineExtension(string extension)
	{
		Console.WriteLine($"Installing {extension} to directory {ExtensionInstallDirectory}");

		_parameters.Context.NuGetInstall(extension,
			new NuGetInstallSettings()
			{
				OutputDirectory = ExtensionInstallDirectory,
				Prerelease = true
			});
	}
}

public class NuGetPackageTester : PackageTester
{
	public NuGetPackageTester(BuildParameters parameters) : base(parameters) { }

	protected override string PackageName => _parameters.NuGetPackageName;
	protected override FilePath PackageUnderTest => _parameters.NuGetPackage;
	protected override string PackageTestDirectory => _parameters.NuGetTestDirectory;
	protected override string PackageTestBinDirectory => PackageTestDirectory + "tools/";
	protected override string ExtensionInstallDirectory => _parameters.TestDirectory;
	
	protected override void InstallEngineExtension(string extension)
	{
		_parameters.Context.NuGetInstall(extension,
			new NuGetInstallSettings()
			{
				OutputDirectory = ExtensionInstallDirectory,
				Prerelease = true
			});
	}
}

public class ChocolateyPackageTester : PackageTester
{
	public ChocolateyPackageTester(BuildParameters parameters) : base(parameters) { }

	protected override string PackageName => _parameters.ChocolateyPackageName;
	protected override FilePath PackageUnderTest => _parameters.ChocolateyPackage;
	protected override string PackageTestDirectory => _parameters.ChocolateyTestDirectory;
	protected override string PackageTestBinDirectory => PackageTestDirectory + "tools/";
	protected override string ExtensionInstallDirectory => _parameters.TestDirectory;
	
	// Chocolatey packages have a different naming convention from NuGet
	protected override string NUnitV2Driver => "nunit-extension-nunit-v2-driver";
	protected override string NUnitProjectLoader => "nunit-extension-nunit-project-loader";

	protected override void InstallEngineExtension(string extension)
	{
		// Install with NuGet because choco requires administrator access
		_parameters.Context.NuGetInstall(extension,
			new NuGetInstallSettings()
			{
				Source = new[] { "https://www.myget.org/F/nunit/api/v3/index.json" },
				OutputDirectory = ExtensionInstallDirectory,
				Prerelease = true
			});
	}
}
