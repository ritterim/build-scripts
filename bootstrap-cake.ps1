##########################################################################
# This is the Cake bootstrapper script for PowerShell.
# This file was downloaded from https://github.com/cake-build/resources
# Feel free to change this file to fit your needs.
##########################################################################

<#

.SYNOPSIS
This is a Powershell script to bootstrap a Cake build.

.DESCRIPTION
This Powershell script will download NuGet if missing, restore NuGet tools (including Cake)
and execute your Cake build script with the parameters you provide.

.PARAMETER Script
The build script to execute.
.PARAMETER Target
The build script target to run.
.PARAMETER Configuration
The build configuration to use.
.PARAMETER Verbosity
Specifies the amount of information to be displayed.
.PARAMETER ShowDescription
Shows description about tasks.
.PARAMETER DryRun
Performs a dry run.
.PARAMETER SkipToolPackageRestore
Skips restoring of packages.
.PARAMETER ScriptArgs
Remaining arguments are added here.

.LINK
https://cakebuild.net

#>

[CmdletBinding()]
Param(
    [string]$Script = "build.cake",
    [string]$Target,
    [string]$Configuration,
    [ValidateSet("Quiet", "Minimal", "Normal", "Verbose", "Diagnostic")]
    [string]$Verbosity,
    [switch]$ShowDescription,
    [Alias("WhatIf", "Noop")]
    [switch]$DryRun,
    [switch]$SkipToolPackageRestore,
    [Parameter(Position=0,Mandatory=$false,ValueFromRemainingArguments=$true)]
    [string[]]$ScriptArgs
)

Write-Host "Starting: $($MyInvocation.MyCommand.Name) -- Jan 20 2021"

#Set-PSDebug -Trace 2

# Attempt to set highest encryption available for SecurityProtocol.
# See also: 
# - https://github.com/cake-build/resources/blob/master/dotnet-framework/build.ps1
# - https://chocolatey.org/install.ps1
# - https://www.medo64.com/2020/05/using-tls-1-3-from-net-4-0-application/
# PowerShell will not set this by default (until maybe .NET 4.6.x). This
# will typically produce a message for PowerShell v2 (just an info
# message though)
try {
    # Set TLS 1.3 (12288) or TLS 1.2 (3072)
    # Use integers because the enumeration values for TLS 1.2 and TLS 1.1 won't
    # exist in .NET 4.0, even though they are addressable if .NET 4.5+ is
    # installed (.NET 4.5 is an in-place upgrade).
    [System.Net.ServicePointManager]::SecurityProtocol = 12288 -bor 3072
  } catch {
    Write-Output 'Unable to set PowerShell to use TLS 1.3 or TLS 1.2.'
  }

[Reflection.Assembly]::LoadWithPartialName("System.Security") | Out-Null
function MD5HashFile([string] $filePath)
{
    if ([string]::IsNullOrEmpty($filePath) -or !(Test-Path $filePath -PathType Leaf))
    {
        return $null
    }

    [System.IO.Stream] $file = $null;
    [System.Security.Cryptography.MD5] $md5 = $null;
    try
    {
        $md5 = [System.Security.Cryptography.MD5]::Create()
        $file = [System.IO.File]::OpenRead($filePath)
        return [System.BitConverter]::ToString($md5.ComputeHash($file))
    }
    finally
    {
        if ($null -ne $file)
        {
            $file.Dispose()
        }
    }
}

function GetProxyEnabledWebClient
{
    $wc = New-Object System.Net.WebClient
    $proxy = [System.Net.WebRequest]::GetSystemWebProxy()
    $proxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials
    $wc.Proxy = $proxy
    return $wc
}

Write-Host "Preparing to run build script..."

if(!$PSScriptRoot){
    $PSScriptRoot = Split-Path $MyInvocation.MyCommand.Path -Parent
}

$TOOLS_DIR = Join-Path $PSScriptRoot "tools"
$ADDINS_DIR = Join-Path $TOOLS_DIR "Addins"
$MODULES_DIR = Join-Path $TOOLS_DIR "Modules"
$NUGET_EXE = Join-Path $TOOLS_DIR "nuget.exe"
$CAKE_EXE = Join-Path $TOOLS_DIR "Cake/Cake.exe"
$MONO_EXECUTABLE = "mono"
[bool] $USE_MONO_FOR_NUGET = 1
$NUGET_URL = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"
$PACKAGES_CONFIG = Join-Path $TOOLS_DIR "packages.config"
$PACKAGES_CONFIG_MD5 = Join-Path $TOOLS_DIR "packages.config.md5sum"
$ADDINS_PACKAGES_CONFIG = Join-Path $ADDINS_DIR "packages.config"
$MODULES_PACKAGES_CONFIG = Join-Path $MODULES_DIR "packages.config"

# Make sure tools folder exists
if ((Test-Path $PSScriptRoot) -and !(Test-Path $TOOLS_DIR)) {
    Write-Verbose -Message "Creating tools directory..."
    New-Item -Path $TOOLS_DIR -Type directory | out-null
}
Write-Verbose -Message "TOOLS_DIR=$($TOOLS_DIR)"
Write-Verbose -Message "ADDINS_DIR=$($ADDINS_DIR)"
Write-Verbose -Message "MODULES_DIR=$($MODULES_DIR)"

# Make sure that packages.config exist.
if (!(Test-Path $PACKAGES_CONFIG)) {
    Write-Verbose -Message "Downloading $($PACKAGES_CONFIG)..."
    try {
        $wc = GetProxyEnabledWebClient
        $wc.DownloadFile("https://cakebuild.net/download/bootstrapper/packages", $PACKAGES_CONFIG)
    } catch {
        Throw "Could not download packages.config."
    }
}

if ($IsLinux -or $IsMacOS) {
    Write-Verbose -Message "Running on Linux/macOS"
    if (!(Test-Path $NUGET_EXE)) {        
        $existingPaths = $Env:PATH -Split ':' | Where-Object { (![string]::IsNullOrEmpty($_)) -and (Test-Path $_ -PathType Container) }
        Write-Verbose -Message "Trying to find nuget in PATH..."
        Write-Verbose -Message "ENV:PATH=$($Env:PATH)"
        $NUGET_EXE_IN_PATH = Get-ChildItem -Path $existingPaths -Filter "nuget" | Select-Object -First 1
        if ($NUGET_EXE_IN_PATH -ne $null -and (Test-Path $NUGET_EXE_IN_PATH.FullName)) {
            Write-Verbose -Message "Found in PATH at $($NUGET_EXE_IN_PATH.FullName)."
            $NUGET_EXE = $NUGET_EXE_IN_PATH.FullName
            $USE_MONO_FOR_NUGET = 0
        }
        if ($USE_MONO_FOR_NUGET) {
            Write-Verbose -Message "Trying to find mono in PATH..."
            $MONO_EXECUTABLE_IN_PATH = Get-ChildItem -Path $existingPaths -Filter "mono" | Select-Object -First 1
            if ($MONO_EXECUTABLE_IN_PATH -ne $null -and (Test-Path $MONO_EXECUTABLE_IN_PATH.FullName)) {
                Write-Verbose -Message "Found in PATH at $($MONO_EXECUTABLE_IN_PATH.FullName)."
                $MONO_EXECUTABLE = $MONO_EXECUTABLE_IN_PATH.FullName
            }        
        }
    }
} else {
    Write-Verbose -Message "Running on Windows"
    $USE_MONO_FOR_NUGET = 0
    # Try find NuGet.exe in path if not at build/nuget.exe location
    if (!(Test-Path $NUGET_EXE)) {
        Write-Verbose -Message "Trying to find nuget.exe in PATH..."
        $existingPaths = $Env:Path -Split ';' | Where-Object { (![string]::IsNullOrEmpty($_)) -and (Test-Path $_ -PathType Container) }
        $NUGET_EXE_IN_PATH = Get-ChildItem -Path $existingPaths -Filter "nuget.exe" | Select-Object -First 1
        if ($null -ne $NUGET_EXE_IN_PATH -and (Test-Path $NUGET_EXE_IN_PATH.FullName)) {
            Write-Verbose -Message "Found in PATH at $($NUGET_EXE_IN_PATH.FullName)."
            $NUGET_EXE = $NUGET_EXE_IN_PATH.FullName
        }
    }
}

# Try download NuGet.exe if not exists
if (!(Test-Path $NUGET_EXE)) {
    Write-Verbose -Message "Downloading NuGet.exe..."
    try {
        $wc = GetProxyEnabledWebClient
        $wc.DownloadFile($NUGET_URL, $NUGET_EXE)
    } catch {
        Throw "Could not download NuGet.exe."
    }
}

# Save to environment to be available to child processes
$ENV:NUGET_EXE = $NUGET_EXE
Write-Verbose -Message "NUGET_EXE=$($NUGET_EXE)"
if ($USE_MONO_FOR_NUGET) {
    $ENV:MONO_EXECUTABLE = $MONO_EXECUTABLE
    Write-Verbose -Message "Running nuget.exe using mono."
}

# Restore tools from NuGet?
if(-Not $SkipToolPackageRestore.IsPresent) {
    Push-Location
    Set-Location $TOOLS_DIR

    # Check for changes in packages.config and remove installed tools if true.
    [string] $md5Hash = MD5HashFile($PACKAGES_CONFIG)
    if((!(Test-Path $PACKAGES_CONFIG_MD5)) -Or
      ($md5Hash -ne (Get-Content $PACKAGES_CONFIG_MD5 ))) {
        Write-Verbose -Message "Missing or changed package.config hash..."
        Get-ChildItem -Exclude packages.config,nuget.exe,Cake.Bakery |
        Remove-Item -Recurse
    }

    Write-Verbose -Message "Restoring tools from NuGet..."
    # The '&' at the start is the Invoke-Expression "Call Operator"
    if ($USE_MONO_FOR_NUGET) {
        $NuGetOutput = Invoke-Expression "&`"$MONO_EXECUTABLE`" `"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$TOOLS_DIR`""
    } else {
        $NuGetOutput = Invoke-Expression "&`"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$TOOLS_DIR`""
    }

    if ($LASTEXITCODE -ne 0) {
        Throw "An error occurred while restoring NuGet tools."
    } else {
        $md5Hash | Out-File $PACKAGES_CONFIG_MD5 -Encoding "ASCII"
    }
    Write-Verbose -Message ($NuGetOutput | out-string)

    Pop-Location
}

# Restore addins from NuGet
if (Test-Path $ADDINS_PACKAGES_CONFIG) {
    Push-Location
    Set-Location $ADDINS_DIR

    Write-Verbose -Message "Restoring addins from NuGet..."
    if ($USE_MONO_FOR_NUGET) {
        $NuGetOutput = Invoke-Expression "&`"$MONO_EXECUTABLE`" `"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$ADDINS_DIR`""
    } else {
        $NuGetOutput = Invoke-Expression "&`"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$ADDINS_DIR`""
    }

    if ($LASTEXITCODE -ne 0) {
        Throw "An error occurred while restoring NuGet addins."
    }

    Write-Verbose -Message ($NuGetOutput | out-string)

    Pop-Location
}

# Restore modules from NuGet
if (Test-Path $MODULES_PACKAGES_CONFIG) {
    Push-Location
    Set-Location $MODULES_DIR

    Write-Verbose -Message "Restoring modules from NuGet..."
        
    if ($USE_MONO_FOR_NUGET) {
        $NuGetOutput = Invoke-Expression "&`"$MONO_EXECUTABLE`" `"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$MODULES_DIR`""
    } else {
        $NuGetOutput = Invoke-Expression "&`"$NUGET_EXE`" install -ExcludeVersion -OutputDirectory `"$MODULES_DIR`""
    }

    if ($LASTEXITCODE -ne 0) {
        Throw "An error occurred while restoring NuGet modules."
    }

    Write-Verbose -Message ($NuGetOutput | out-string)

    Pop-Location
}

# Make sure that Cake has been installed.
if (!(Test-Path $CAKE_EXE)) {
    Throw "Could not find Cake.exe at $CAKE_EXE"
}



# Build Cake arguments
$cakeArguments = @("$Script");
if ($Target) { $cakeArguments += "-target=$Target" }
if ($Configuration) { $cakeArguments += "-configuration=$Configuration" }
if ($Verbosity) { $cakeArguments += "-verbosity=$Verbosity" }
if ($ShowDescription) { $cakeArguments += "-showdescription" }
if ($DryRun) { $cakeArguments += "-dryrun" }
$cakeArguments += $ScriptArgs

# Start Cake
Write-Host "Running build script using Cake..."
if ($IsLinux -or $IsMacOS) {
    Invoke-Expression "&`"$MONO_EXECUTABLE`" `"$CAKE_EXE`" --version"
    Invoke-Expression "&`"$MONO_EXECUTABLE`" `"$CAKE_EXE`" $cakeArguments"
} else {
    Invoke-Expression "&`"$CAKE_EXE`" --version"
    Invoke-Expression "&`"$CAKE_EXE`" $cakeArguments"
}

exit $LASTEXITCODE
