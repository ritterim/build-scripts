#addin nuget:?package=Cake.FileHelpers&version=3.2.1
#tool nuget:?package=xunit.runner.console&version=2.4.1

var target = Argument("target", "Default");
Information("build-webapi-netfx.cake -- Dec-8-2021");

var versionFromFile = FileReadText("./version.txt")
                    .Trim()
                    .Split('.')
                    .Take(2)
                    .Aggregate("", (version, x) => $"{version}{x}.")
                    .Trim('.');

var buildNumber = AppVeyor.Environment.Build.Number;

var version = $"{versionFromFile}.{buildNumber}";

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
    packageVersion += "-alpha";
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
Information($"solution={solution}");

// Look for a "host" project (named either "host" or "api")
var hostProject = GetFiles("./src/**/*.csproj")
    .SingleOrDefault(x =>
        (
            x.GetFilename().FullPath.ToLowerInvariant().Contains("api")
            || x.GetFilename().FullPath.ToLowerInvariant().Contains("host")
        )
        && !x.GetFilename().FullPath.ToLowerInvariant().Contains("webapi"));
Information($"hostProject={hostProject}");
var hostDirectory = hostProject?.GetDirectory();
Information($"hostDirectory={hostDirectory}");

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
        XUnit2("./test*/**/bin/" + configuration + "/**/*.Tests.dll");
    });

Task("Package")
    .IsDependentOn("Run-Tests")
    .Does(() =>
    {
        if (hostProject != null)
        {
            Information($"Found a host/api project to build.");

            var hostArtifactsDir = artifactsDir + Directory("Host");
            Information($"hostArtifactsDir={hostArtifactsDir}");

            var hostProjectName = hostProject.GetFilenameWithoutExtension();
            Information($"hostProjectName={hostProjectName}");

            System.IO.File.AppendAllText(
                "./src/" + hostProjectName + "/obj/" + configuration + "/Package/PackageTmp/githash.txt",
                BuildSystem.AppVeyor.Environment.Repository.Commit.Id);

            Zip(
                "./src/" + hostProjectName + "/obj/" + configuration + "/Package/PackageTmp",
                "./artifacts/" + hostProjectName + ".zip"
            );

            // Search for class library DLLs that need to be published to NuGet/MyGet.
            // They must have PackageId defined in the .csproj file.
            Information("\nSearching for csproj files with PackageId defined to create NuGet packages...");
            var clientProjects = GetFiles("./src/**/*.csproj");

            foreach (var clientProject in clientProjects)
            {
                var clientProjectPath = clientProject.ToString();
                // XmlPeek - https://stackoverflow.com/a/34886946
                var packageId = XmlPeek(
                    clientProjectPath,
                    "/Project/PropertyGroup/PackageId/text()",
                    new XmlPeekSettings { SuppressWarning = true }
                    );

                if (!string.IsNullOrWhiteSpace(packageId))
                {
                    Information($"\nclientProjectPath={clientProjectPath}");
                    Information($"packageId={packageId}");

                    var isNetstandard = FindRegexMatchesInFile(
                        clientProjectPath,
                        @"<TargetFrameworks?>.*netstandard.*<\/TargetFrameworks?>",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase
                        ).Any();

                    if (isNetstandard)
                    {
                        DotNetCorePack(clientProjectPath, new DotNetCorePackSettings
                        {
                            Configuration = configuration,
                            MSBuildSettings = new DotNetCoreMSBuildSettings().SetVersion(packageVersion),
                            NoBuild = true,
                            OutputDirectory = artifactsDir,
                            IncludeSymbols = true
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
        }
    });

Task("Default")
    .IsDependentOn("Package");

RunTarget(target);
