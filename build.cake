// Load the recipe
#load nuget:?package=NUnit.Cake.Recipe&version=2.0.0-beta.4.8
// Comment out above line and uncomment below for local tests of recipe changes
//#load ../NUnit.Cake.Recipe/recipe/*.cake

// Initialize BuildSettings
BuildSettings.Initialize(
    Context,
    title: "NUnit Agent Core",
    githubRepository: "NUnit.Agent.Core",
    solutionFile: "NUnit.Agent.Core.slnx" );

//////////////////////////////////////////////////////////////////////
// INDIVIDUAL PACKAGE DEFINITIONS
//////////////////////////////////////////////////////////////////////

BuildSettings.Packages.Add
(
    new NuGetPackage
    (
        id: "NUnit.Agent.Core",
        source: BuildSettings.SourceDirectory + "nunit.agent.core/nunit.agent.core.csproj",
        checks: new PackageCheck[]
        {
            HasFiles("LICENSE.txt", "README.md", "NUnit_256.png"),
            HasDirectory("lib/net462").WithFile("nunit.agent.core.dll" ),
            HasDirectory("lib/net8.0").WithFiles("nunit.agent.core.dll"),
            HasDependency("NUnit.Engine.Api"),
            HasDependency("NUnit.Common"),
            HasDependency("TestCentric.Metadata")
        },
        symbols: new PackageCheck[]
        {
            HasDirectory("lib/net462").WithFile("nunit.agent.core.pdb"),
            HasDirectory("lib/net8.0").WithFile("nunit.agent.core.pdb")
        },
        testRunner: new PackageTestRunner(),
        tests: new PackageTest[]
        {
            new MockAssemblyTest("Net462Test", "net462"),
            new MockAssemblyTest("Net50Test", "net5.0"),
            new MockAssemblyTest("Net60Test", "net6.0"),
            new MockAssemblyTest("Net70Test", "net7.0"),
            new MockAssemblyTest("Net80Test", "net8.0"),
            new MockAssemblyTest("Net90Test", "net9.0"),
            new MockAssemblyTest("Net10Test", "net10.0")
        }
    ) 
);

private class MockAssemblyTest : PackageTest
{
    public MockAssemblyTest(string name, string subdir) : base(1, name)
    {
        Description = $"Run mock-assembly.dll targeting {subdir}";
        Arguments = $"{subdir}/mock-assembly.dll";
        ExpectedResult = new ExpectedResult("Failed")
        {
            Total = 37, Passed = 23, Failed = 5, Warnings = 1, Inconclusive = 1, Skipped = 7,
            Assemblies = [ new ExpectedAssemblyResult("mock-assembly.dll") ]
        };
    }
}

//////////////////////////////////////////////////////////////////////
// AGENT CORE PACKAGE TEST RUNNER
//////////////////////////////////////////////////////////////////////

public class PackageTestRunner : TestRunner, IPackageTestRunner
{
    public int RunPackageTest(string arguments, bool redirectOutput)
    {
        // First argument must be relative path to a test assembly.
        // It's immediate directory name is the name of the runtime.
        string testAssembly = arguments.Trim();
        testAssembly = BuildSettings.OutputDirectory + (testAssembly[0] == '"'
            ? testAssembly.Substring(1, testAssembly.IndexOf('"', 1) - 1)
            : testAssembly.Substring(0, testAssembly.IndexOf(' ')));

        if (!System.IO.File.Exists(testAssembly))
            throw new FileNotFoundException($"File not found: {testAssembly}");

        string testRuntime = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(testAssembly));
        string agentRuntime = testRuntime;

        if (agentRuntime.EndsWith("-windows"))
            agentRuntime = agentRuntime.Substring(0, 6);

        // We only are building PackageTestRunner.exe for net462, net8.0 and net10.0, 
        // so we need to map the test runtime to one of those.
        if (agentRuntime is "net20" or "net35")
            agentRuntime = "net462";
        else if (agentRuntime is "net5.0" or "net6.0" or "net7.0")
            agentRuntime = "net8.0";
        else if (agentRuntime is "net9.0")
            agentRuntime = "net10.0";

        var executablePath = BuildSettings.OutputDirectory + $"{agentRuntime}/PackageTestRunner.exe";

        if (!System.IO.File.Exists(executablePath))
            throw new FileNotFoundException($"File not found: {executablePath}");

        Console.WriteLine($"Trying to run {executablePath} with arguments {arguments}");

        return BuildSettings.Context.StartProcess(executablePath, new ProcessSettings()
        {
            Arguments = arguments,
            WorkingDirectory = BuildSettings.OutputDirectory
        });
    }
}

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

Build.Run()
