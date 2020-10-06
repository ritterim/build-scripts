#addin nuget:?package=Cake.FileHelpers&version=3.2.1
#tool nuget:?package=xunit.runner.console&version=2.4.1

var target = Argument("target", "Default");
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
var solution = GetFiles("./**/*.sln").First().ToString();

Task("Clean")
    .Does(() =>
    {
        CleanDirectory(artifactsDir);
    });

Task("Restore-NuGet-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        DotNetCoreRestore(solution);
    });

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
    {
        DotNetCoreBuild(solution, new DotNetCoreBuildSettings
        {
            Configuration = configuration,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                TreatAllWarningsAs = MSBuildTreatAllWarningsAs.Error,
                Verbosity = DotNetCoreVerbosity.Minimal
            }
            .WithProperty("Version", version)

            // msbuild.log specified explicitly, see https://github.com/cake-build/cake/issues/1764
            .AddFileLogger(new MSBuildFileLoggerSettings { LogFile = "msbuild.log" })
        });
    });

Task("Run-Tests")
    .IsDependentOn("Build")
    .Does(() =>
    {
        var projectFiles = GetFiles("./tests/**/*.csproj");
        foreach (var file in projectFiles)
        {
            DotNetCoreTest(file.FullPath);
        }
    });

Task("Package")
    .IsDependentOn("Run-Tests")
    .Does(() =>
    {
        var hostArtifactsDir = artifactsDir + Directory("Host");

        var hostProject = GetFiles("./src/**/*.csproj")
            .Single(x =>
                (
                    x.GetFilename().FullPath.ToLowerInvariant().Contains("api")
                    || x.GetFilename().FullPath.ToLowerInvariant().Contains("host")
                )
                && !x.GetFilename().FullPath.ToLowerInvariant().Contains("webapi"));

        var hostProjectName = hostProject.GetFilenameWithoutExtension();

        DotNetCorePublish(hostProject.ToString(), new DotNetCorePublishSettings
        {
            Configuration = configuration,
            OutputDirectory = hostArtifactsDir,
            MSBuildSettings = new DotNetCoreMSBuildSettings().SetVersion(packageVersion)
        });

        System.IO.File.AppendAllText(
            hostArtifactsDir + File("githash.txt"),
            BuildSystem.AppVeyor.Environment.Repository.Commit.Id);

        // work around for datetime offset problem
        var now = DateTime.UtcNow;
        foreach(var file in GetFiles($"{hostArtifactsDir}/**/*.*"))
        {
            System.IO.File.SetLastWriteTimeUtc(file.FullPath, now);
        }

        Zip(
            hostArtifactsDir,
            "./artifacts/" + hostProjectName + ".zip"
        );

        var clientProjects = GetFiles("./src/**/*.csproj")
            .Where(x => x.GetFilename().FullPath.ToLowerInvariant().Contains("client"));

        foreach (var clientProject in clientProjects)
        {
            var clientProjectPath = clientProject.ToString();

            DotNetCorePack(clientProjectPath, new DotNetCorePackSettings
            {
                Configuration = configuration,
                MSBuildSettings = new DotNetCoreMSBuildSettings().SetVersion(packageVersion),
                NoBuild = true,
                OutputDirectory = artifactsDir,
                IncludeSymbols = true
            });
        }
    });

Task("Default")
    .IsDependentOn("Package");

RunTarget(target);
