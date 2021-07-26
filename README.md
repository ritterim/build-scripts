# Build scripts

Ritter Insurance Marketing project specific build scripts.

- `build-webapi-netfx.cake` ASP.NET WebApi2 with full .NET framework
- `build-netcoreapp.cake` ASP.NET Core (somewhat obsolete)
- `build-net5.cake` .NET 5 projects, including creating multiple Nuget packages

## Usage

Perform the following steps in a target project:

### Create build.cmd

```batch
@echo off

powershell -ExecutionPolicy RemoteSigned -File ./build.ps1
```

### Create build.ps1

```powershell
New-Item -ItemType directory -Path "build" -Force | Out-Null

try {
  Invoke-WebRequest https://raw.githubusercontent.com/ritterim/build-scripts/master/bootstrap-cake.ps1 -OutFile build\bootstrap-cake.ps1
  Invoke-WebRequest https://raw.githubusercontent.com/ritterim/build-scripts/master/build-[webapi-netfx OR net5].cake -OutFile build.cake
}
catch {
  Write-Output $_.Exception.Message
  Write-Output "Error while downloading shared build script, attempting to use previously downloaded scripts..."
}

.\build\bootstrap-cake.ps1
Exit $LastExitCode
```

### Create version.txt

The version number for the NuGet package is sourced from a version.txt file in the root directory of the solution.

Example version.txt:

```
1.0.0
```

Note that even if you provide the patch value for _major.minor.patch_, many of the Cake recipes ignore that value and replace the patch value with the AppVeyor build number.

### Use AssemblyInfo.Generated.cs *(`build-webapi-netfx.cake` only)*

If the generated file won't be included in another manner, replace `<Compile Include="Properties\AssemblyInfo.cs" />` with `<Compile Include="Properties\AssemblyInfo*.cs" />`.

### Create or update `.gitignore`

Include:

```
build/
tools/
build.cake

# `build-webapi-netfx.cake` only
AssemblyInfo.Generated.cs
```

## License

MIT
