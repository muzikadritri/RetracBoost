param([string]$path)
$d = Get-Content $path -Raw | ConvertFrom-Json
$ec2 = $d.prefixes | Where-Object { $_.service -eq 'EC2' }
Write-Host "EC2 Regions in Europe:"
$ec2 | Where-Object { $_.region -like 'eu-*' } | Group-Object region | ForEach-Object {
    Write-Host "$($_.Name): $($_.Count) CIDR blocks"
}
