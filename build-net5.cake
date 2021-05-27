#addin nuget:?package=Cake.FileHelpers&version=3.2.1
#tool nuget:?package=xunit.runner.console&version=2.4.1
#addin nuget:?package=Cake.Docker&version=0.11.1

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

// Output information about key variables
Information($"packageVersion={packageVersion}");
Information($"configuration={configuration}");
Information($"solution={solution}");

var createElasticsearchDocker = false;
bool.TryParse(EnvironmentVariable("RIMDEV_CREATE_TEST_DOCKER_ES"), out createElasticsearchDocker);
Information($"createElasticsearchDocker={createElasticsearchDocker}");

var createSqlDocker = false;
bool.TryParse(EnvironmentVariable("RIMDEV_CREATE_TEST_DOCKER_SQL"), out createSqlDocker);
Information($"createSqlDocker={createSqlDocker}");

var DockerSqlName = "test-mssql";
var DockerElasticsearchName = "test-es";

Setup(context =>
{
    Information("Starting up Docker test container(s).");
    
    // Output a list of the pre-installed docker images on the AppVeyor instance.
    // This could help us pick images that do not have to be downloaded on every run.
    // Requires build.ps1 to call Cake with --verbosity=Diagnostic
    DockerImageLs(new DockerImageLsSettings());

    if (createSqlDocker)
    {
        var sqlDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerSqlName}",
            Quiet = true,
        });
        Information($"sqlDockerId={sqlDockerId}");
        if (sqlDockerId != "") DockerStop(sqlDockerId);

        // https://hub.docker.com/_/microsoft-mssql-server    
        DockerRun(new DockerContainerRunSettings
        {
            Detach = true,
            Env = new string[] 
            {
                "ACCEPT_EULA=Y",
                $"SA_PASSWORD={EnvironmentVariable("RIMDEV_TEST_DOCKER_MSSQL_SA_PASSWORD")}", 
            },
            Name = DockerSqlName,
            Publish = new string[]
            {
                "11434:1433",
            },
            Rm = true,        
        },
        "mcr.microsoft.com/mssql/server:2019-latest",
        null,
        null
        );
    }

    if (createElasticsearchDocker)
    {
        var elasticsearchDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerElasticsearchName}",
            Quiet = true,
        });
        Information($"elasticsearchDockerId={elasticsearchDockerId}");
        if (elasticsearchDockerId != "") DockerStop(elasticsearchDockerId);

        // https://hub.docker.com/_/elasticsearch    
        DockerRun(new DockerContainerRunSettings
        {
            Detach = true,
            Env = new string[] 
            {
                "discovery.type=single-node",
                "ES_JAVA_OPTS=-Xms256m -Xmx256m", 
            },
            Name = DockerElasticsearchName,
            Publish = new string[]
            {
                "9201:9200",
                "9301:9300",
            },
            Rm = true,        
        },
        "docker.elastic.co/elasticsearch/elasticsearch:6.8.13",
        null,
        null
        );
    }

    DockerPs(new DockerContainerPsSettings
    {
        All = true,
        NoTrunc = true,
        Size = true,
    });
});

Teardown(context =>
{
    Information("Stopping Docker test container(s).");

    var sqlDockerId = DockerPs(new DockerContainerPsSettings
    {
        All = true,
        Filter = $"name={DockerSqlName}",
        Quiet = true,
    });
    Information($"sqlDockerId={sqlDockerId}");
    if (sqlDockerId != "") DockerStop(sqlDockerId);

    var elasticsearchDockerId = DockerPs(new DockerContainerPsSettings
    {
        All = true,
        Filter = $"name={DockerElasticsearchName}",
        Quiet = true,
    });
    Information($"sqlDockerId={sqlDockerId}");
    if (elasticsearchDockerId != "") DockerStop(elasticsearchDockerId);
});

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
        if (AppVeyor.IsRunningOnAppVeyor) 
        {
            DockerPs(new DockerContainerPsSettings());
        }

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
