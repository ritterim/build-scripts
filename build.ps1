
$failed = $false

$scripts = @(".\build-webapi-netfx.cake")

# Verify $scripts do not contain compilation errors
foreach ($script in $scripts) {
  Write-Output ""
  Write-Output "----- TESTING: $script -----"
  .\bootstrap-cake.ps1 -Script $script -Verbose --verbosity=Normal --Target=Clean --exclusive
  Write-Output "----- FINISH: $script -----"

  if ($LastExitCode -ne 0) {
      $failed = $true
  }
}

if ($failed) {
   Exit 1
}
