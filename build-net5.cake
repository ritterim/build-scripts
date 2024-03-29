#addin nuget:?package=Cake.Docker&version=1.0.0
#addin nuget:?package=Cake.FileHelpers&version=4.0.1
#addin nuget:?package=Cake.Git&version=1.1.0
#addin nuget:?package=Cake.Npm&version=1.0.0
#tool nuget:?package=xunit.runner.console&version=2.4.1

Information("build-net5.cake -- Aug-22-2022");
var target = Argument("target", "Default");

// RELEASE STRATEGY: old vs new git flow (master branch vs trunk-based release strategy)
//   false (default) = only "release/*" branches result in production artifacts
//   true = any commit to master results in production artifacts
// Any git repo that wants to release on master, must set environment var: UseMasterReleaseStrategy=true
var useMasterReleaseStrategy = false;
bool.TryParse(EnvironmentVariable("UseMasterReleaseStrategy"), out useMasterReleaseStrategy);
Information($"useMasterReleaseStrategy={useMasterReleaseStrategy}");

// Calculate the version
var versionFromFile = FileReadText("./version.txt").Trim().Split('.')
    .Take(2).Aggregate("", (version, x) => $"{version}{x}.").Trim('.');
var buildNumber = AppVeyor.Environment.Build.Number;
var version = $"{versionFromFile}.{buildNumber}";
Information($"version={version}");

var packageVersion = version;
if (!AppVeyor.IsRunningOnAppVeyor)
{
    packageVersion += "-dev";
}
Information($"packageVersion={packageVersion}");

// Get the current git hash
// https://philipm.at/2018/versioning_assemblies_with_cake.html
var gitRepo = MakeAbsolute(Directory("./"));
var gitBranch = GitBranchCurrent(gitRepo);
var gitShortHash = gitBranch.Tip.Sha.Substring(0, 8);
Information($"gitShortHash={gitShortHash}");
var informationalVersion=$"{packageVersion}+{gitShortHash}";
Information($"informationalVersion={informationalVersion}");

var configuration = "Release";
Information($"configuration={configuration}");

var artifactsDir = Directory("./artifacts");

// Assume a single solution per repository
var solution = GetFiles("./**/*.sln").First().ToString();
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
var npmPackageLockFile = (hostDirectory != null)
    ? GetFiles($"{hostDirectory}/package-lock.json").SingleOrDefault()
    : null;
Information($"npmPackageLockFile={npmPackageLockFile}");

var createAzurite = false;
bool.TryParse(EnvironmentVariable("RIMDEV_CREATE_TEST_DOCKER_AZURITE"), out createAzurite);
Information($"createAzurite={createAzurite}");
var DockerAzuriteName = "test-azurite";

var createElasticsearchDocker = false;
bool.TryParse(EnvironmentVariable("RIMDEV_CREATE_TEST_DOCKER_ES"), out createElasticsearchDocker);
Information($"createElasticsearchDocker={createElasticsearchDocker}");
var DockerElasticsearchName = "test-es";

var createSqlDocker = false;
bool.TryParse(EnvironmentVariable("RIMDEV_CREATE_TEST_DOCKER_SQL"), out createSqlDocker);
Information($"createSqlDocker={createSqlDocker}");
var DockerSqlName = "test-mssql";

var anyDocker = createAzurite || createElasticsearchDocker || createSqlDocker;

Setup(context =>
{
    if (anyDocker)
    {
        Information("Starting up Docker test container(s).");

        // Output a list of the pre-installed docker images on the AppVeyor instance.
        // This could help us pick images that do not have to be downloaded on every run.
        // Requires build.ps1 to call Cake with --verbosity=Diagnostic
        DockerImageLs(new DockerImageLsSettings());
    }

    if (createAzurite)
    {
        var azuriteDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerAzuriteName}",
            Quiet = true,
        });
        Information($"Existing azuriteDockerId={azuriteDockerId}");
        if (azuriteDockerId != "") DockerStop(azuriteDockerId);

        DockerRun(new DockerContainerRunSettings
        {
            Detach = true,
            Name = DockerAzuriteName,
            Publish = new string[]
            {
                $"10000:10000", // blob
                $"10001:10001", // query
                $"10002:10002", // table
            },
            Rm = true,
        },
        "mcr.microsoft.com/azure-storage/azurite",
        null,
        null
        );

        azuriteDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerAzuriteName}",
            Quiet = true,
        });
        Information($"Created azuriteDockerId={azuriteDockerId}");
    }

    if (createSqlDocker)
    {
        var sqlPort = EnvironmentVariable("RIMDEVTESTS__SQL__PORT");
        if (string.IsNullOrWhiteSpace(sqlPort)) sqlPort = "11434";
        Information($"Create SQL Docker on port {sqlPort}.");

        var sqlPassword = EnvironmentVariable("RIMDEVTESTS__SQL__PASSWORD");
        if (string.IsNullOrWhiteSpace(sqlPassword)) sqlPassword = EnvironmentVariable("RIMDEV_TEST_DOCKER_MSSQL_SA_PASSWORD");

        var sqlDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerSqlName}",
            Quiet = true,
        });
        Information($"Existing sqlDockerId={sqlDockerId}");
        if (sqlDockerId != "") DockerStop(sqlDockerId);

        // https://hub.docker.com/_/microsoft-mssql-server
        DockerRun(new DockerContainerRunSettings
        {
            Detach = true,
            Env = new string[]
            {
                "ACCEPT_EULA=Y",
                $"SA_PASSWORD={sqlPassword}",
            },
            Name = DockerSqlName,
            Publish = new string[]
            {
                $"{sqlPort}:1433",
            },
            Rm = true,
        },
        "mcr.microsoft.com/mssql/server:2019-latest",
        null,
        null
        );

        sqlDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerSqlName}",
            Quiet = true,
        });
        Information($"Created sqlDockerId={sqlDockerId}");
    }

    if (createElasticsearchDocker)
    {
        var httpPort = EnvironmentVariable("RIMDEVTESTS__ELASTICSEARCH__PORT");
        if (string.IsNullOrWhiteSpace(httpPort)) httpPort = "9201";
        var transportPort = EnvironmentVariable("RIMDEVTESTS__ELASTICSEARCH__TRANSPORTPORT");
        if (string.IsNullOrWhiteSpace(transportPort)) transportPort = "9301";
        Information($"Create Elasticsearch Docker on ports {httpPort} and {transportPort}.");

        var elasticsearchDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerElasticsearchName}",
            Quiet = true,
        });
        Information($"Existing elasticsearchDockerId={elasticsearchDockerId}");
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
                $"{httpPort}:9200",
                $"{transportPort}:9300",
            },
            Rm = true,
        },
        "docker.elastic.co/elasticsearch/elasticsearch:6.8.13",
        null,
        null
        );

        elasticsearchDockerId = DockerPs(new DockerContainerPsSettings
        {
            All = true,
            Filter = $"name={DockerElasticsearchName}",
            Quiet = true,
        });
        Information($"Created elasticsearchDockerId={elasticsearchDockerId}");
    }

    if (anyDocker)
    {
        DockerPs(new DockerContainerPsSettings
        {
            All = true,
            NoTrunc = true,
            Size = true,
        });
    }
});

Teardown(context =>
{
    /* No need to run "docker stop" for builds under AppVeyor where the build worker is discarded.
     * It is taking five minutes to execute "stop" under the "Visual Studio 2019" build worker.
     */
    if (!AppVeyor.IsRunningOnAppVeyor)
    {
        if (anyDocker)
        {
             Information("Stopping Docker test container(s).");

             var sqlDockerId = DockerPs(new DockerContainerPsSettings
             {
                 All = true,
                 Filter = $"name={DockerSqlName}",
                 Quiet = true,
             });
             Information($"Found sqlDockerId={sqlDockerId}");
             if (sqlDockerId != "") DockerStop(sqlDockerId);

             var elasticsearchDockerId = DockerPs(new DockerContainerPsSettings
             {
                 All = true,
                 Filter = $"name={DockerElasticsearchName}",
                 Quiet = true,
             });
             Information($"Found elasticsearchDockerId={elasticsearchDockerId}");
             if (elasticsearchDockerId != "") DockerStop(elasticsearchDockerId);
        }
    }
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

Task("Restore-npm-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        if (hostDirectory is null || npmPackageLockFile is null) return;

        Information($"Found NPM package-lock.json.");
        NpmCi(new NpmCiSettings
        {
            LogLevel = NpmLogLevel.Warn,
            WorkingDirectory = hostDirectory
        });
    });

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .IsDependentOn("Restore-npm-Packages")
    .Does(() =>
    {
        if (hostDirectory != null && npmPackageLockFile != null)
        {
            NpmRunScript(new NpmRunScriptSettings
            {
                ScriptName = "webpack",
                WorkingDirectory = hostDirectory
            });
        }

        DotNetCoreBuild(solution, new DotNetCoreBuildSettings
        {
            Configuration = configuration,
            MSBuildSettings = new DotNetCoreMSBuildSettings
            {
                TreatAllWarningsAs = MSBuildTreatAllWarningsAs.Error,
                Verbosity = DotNetCoreVerbosity.Minimal
            }
            .SetVersion(packageVersion)
            .SetInformationalVersion(informationalVersion)

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

        // use 'solution' variable (no need to scan for projects)
        DotNetCoreTest(solution);
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

            DotNetCorePublish(hostProject.ToString(), new DotNetCorePublishSettings
            {
                Configuration = configuration,
                OutputDirectory = hostArtifactsDir,
                MSBuildSettings = new DotNetCoreMSBuildSettings()
                    .SetVersion(packageVersion)
                    .SetInformationalVersion(informationalVersion)
            });

            // add a githash.txt file to the host output directory (must be after DotNetCorePublish)
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
        }

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

                DotNetCorePack(clientProjectPath, new DotNetCorePackSettings
                {
                    Configuration = configuration,
                    MSBuildSettings = new DotNetCoreMSBuildSettings()
                        .SetVersion(packageVersion)
                        .SetInformationalVersion(informationalVersion),
                    NoBuild = true,
                    OutputDirectory = artifactsDir,
                    IncludeSymbols = true,

                    //TODO: Remove ArgumentCustomization, add SymbolPackageFormat once Cake 1.2 is released
                    // https://github.com/cake-build/cake/pull/3331
                    ArgumentCustomization = x => x.Append("-p:SymbolPackageFormat=snupkg")
                    //SymbolPackageFormat = "snupkg",
                });
            }
        }
    });

Task("Default")
    .IsDependentOn("Package");

RunTarget(target);
