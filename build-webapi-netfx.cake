#addin nuget:?package=Cake.FileHelpers&version=3.1.0
#tool nuget:?package=xunit.runner.console&version=2.4.1
#tool nuget:?package=vswhere&version=2.6.7

var target = Argument("target", "Default");
var version = FileReadText("./version.txt").Trim();

var isNewEnvironment = false;
bool.TryParse(EnvironmentVariable("NewRitterEnvironment"), out isNewEnvironment);

var packageVersion = version;
if (!AppVeyor.IsRunningOnAppVeyor)
{
    packageVersion += "-dev";
}
else if ((!isNewEnvironment && AppVeyor.Environment.Repository.Branch != "master")
          || (isNewEnvironment && !AppVeyor.Environment.Repository.Branch.StartsWith("release/")))
{
    packageVersion += "-alpha" + AppVeyor.Environment.Build.Number;
}

var configuration = "Release";

if (isNewEnvironment
    && !AppVeyor.Environment.PullRequest.IsPullRequest
    && AppVeyor.Environment.Repository.Branch == "master")
{
    configuration = "Development";
}
else if (!AppVeyor.Environment.PullRequest.IsPullRequest
         && AppVeyor.Environment.Repository.Branch == "development")
{
    configuration = "QA";
}

var artifactsDir = Directory("./artifacts");

// Assume a single solution per repository
var solution = GetFiles("./**/*.sln").First();

Task("Clean")
    .Does(() =>
    {
        CleanDirectory(artifactsDir);
    });

Task("Restore-NuGet-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        NuGetRestore(solution);
    });

Task("Version")
    .Does(() =>
    {
        foreach (var assemblyInfo in GetFiles("./src/**/AssemblyInfo.cs"))
        {
            CreateAssemblyInfo(
                assemblyInfo.ChangeExtension(".Generated.cs"),
                new AssemblyInfoSettings
                {
                    Version = version,
                    InformationalVersion = packageVersion
                });
        }
    });

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .IsDependentOn("Version")
    .Does(() =>
    {
        MSBuild(solution, settings =>
            settings
                .UseToolVersion(
                    VSWhereLatest().ToString().Contains("/Microsoft Visual Studio/2019/")
                        ? MSBuildToolVersion.VS2019
                        : MSBuildToolVersion.Default)
                .SetConfiguration(configuration)
                .WithProperty("TreatWarningsAsErrors", "True")
                .WithProperty("DeployOnBuild", "True")
                .SetVerbosity(Verbosity.Minimal)
                .AddFileLogger());
    });

Task("Run-Tests")
    .IsDependentOn("Build")
    .Does(() =>
    {
        XUnit2("./test*/**/bin/" + configuration + "/*.Tests.dll");
    });

Task("Package")
    .IsDependentOn("Run-Tests")
    .Does(() =>
    {
        var hostProjectName = GetFiles("./src/**/*.csproj")
            .Single(x =>
                (
                    x.GetFilename().FullPath.ToLowerInvariant().Contains("api")
                    || x.GetFilename().FullPath.ToLowerInvariant().Contains("host")
                )
                && !x.GetFilename().FullPath.ToLowerInvariant().Contains("webapi"))
            .GetFilenameWithoutExtension();

        System.IO.File.AppendAllText("./src/" + hostProjectName + "/obj/" + configuration + "/Package/PackageTmp/githash.txt", BuildSystem.AppVeyor.Environment.Repository.Commit.Id);

        Zip(
            "./src/" + hostProjectName + "/obj/" + configuration + "/Package/PackageTmp",
            "./artifacts/" + hostProjectName + ".zip"
        );

        var clientProjects = GetFiles("./src/**/*.csproj")
            .Where(x => x.GetFilename().FullPath.ToLowerInvariant().Contains("client"));

        foreach (var clientProject in clientProjects)
        {
            var clientProjectPath = clientProject.ToString();
            var isNetstandard = FindRegexMatchesInFile(
              clientProjectPath,
              @"<TargetFrameworks?>.*netstandard.*<\/TargetFrameworks?>",
              System.Text.RegularExpressions.RegexOptions.IgnoreCase)
              .Any();

            if (isNetstandard)
            {
                DotNetCorePack(clientProjectPath, new DotNetCorePackSettings
                {
                    Configuration = configuration,
                    MSBuildSettings = new DotNetCoreMSBuildSettings().SetVersion(packageVersion),
                    NoBuild = true,
                    OutputDirectory = artifactsDir
                });
            }
            else
            {
                NuGetPack(clientProjectPath, new NuGetPackSettings
                {
                    OutputDirectory = artifactsDir,
                    Version = packageVersion,
                    Properties = new Dictionary<string, string>
                    {
                        { "Configuration", configuration }
                    },
                    Symbols = true
                });
            }
        }

        var additionalZipDeploymentsFile = "./build-additional-zip-deployments.txt";

        if (FileExists(additionalZipDeploymentsFile))
        {
            var additionalZipDeployments = FileReadText(additionalZipDeploymentsFile)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());

            foreach (var additionalZipDeployment in additionalZipDeployments)
            {
                var projectName = GetFiles("./src/**/*.csproj")
                    .Single(x => x.GetFilename().FullPath == additionalZipDeployment + ".csproj")
                    .GetFilenameWithoutExtension();

                Zip(
                    "./src/" + projectName + "/obj/" + configuration + "/Package/PackageTmp",
                    "./artifacts/" + projectName + ".zip"
                );
            }
        }

        var additionalClientDeploymentsFile = "./build-additional-client-deployments.txt";

        if (FileExists(additionalClientDeploymentsFile))
        {
            var additionalClientDeployments = FileReadText(additionalClientDeploymentsFile)
                .Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim());

            foreach (var additionalClientDeployment in additionalClientDeployments)
            {
                string packFile;

                var filePathWithoutExtension = "./src/"
                    + additionalClientDeployment
                    + "/"
                    + additionalClientDeployment;

                var potentialNuspec = filePathWithoutExtension + ".nuspec";
                var potentialCsproj = filePathWithoutExtension + ".csproj";

                if (FileExists(potentialNuspec))
                {
                    packFile = potentialNuspec;
                }
                else
                {
                    packFile = potentialCsproj;
                }

                NuGetPack(packFile, new NuGetPackSettings
                {
                    OutputDirectory = artifactsDir,
                    Version = packageVersion,
                    Properties = new Dictionary<string, string>
                    {
                        { "Configuration", configuration }
                    },
                    Symbols = true
                });
            }
        }
    });

Task("Default")
    .IsDependentOn("Package");

RunTarget(target);
