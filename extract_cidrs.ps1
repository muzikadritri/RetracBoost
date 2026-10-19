param([string]$path)
$d = Get-Content $path -Raw | ConvertFrom-Json
$nonFrankfurt = $d.prefixes | Where-Object { 
    $_.service -eq 'EC2' -and 
    $_.region -like 'eu-*' -and 
    $_.region -ne 'eu-central-1' -and 
    $_.region -ne 'eu-central-2' 
} | Select-Object -ExpandProperty ip_prefix -Unique

Write-Host "Found $($nonFrankfurt.Count) non-Frankfurt EU EC2 CIDRs"
$nonFrankfurt | Out-File -FilePath "c:\Retrac tools\non_frankfurt_cidrs.txt" -Encoding ascii
