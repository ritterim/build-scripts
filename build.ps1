
$failed = $false

$scripts = @(".\build-webapi-netfx.cake")

# Verify $scripts do not contain compilation errors
foreach ($script in $scripts) {
  .\bootstrap-cake.ps1 -Script $script -Target Clean --exclusive

  if ($LastExitCode -ne 0) {
      $failed = $true
  }
}

if ($failed) {
   Exit 1
}
