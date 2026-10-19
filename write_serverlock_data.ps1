$head = @"
using System;

namespace RetracBoost
{
    static class ServerLockData
    {
        // 302 CIDR-Bloecke aller AWS EC2-Sub-Regionen in Europa AUSSER Frankfurt (eu-central-1) und Zuerich (eu-central-2).
        // Enthaelt London (eu-west-2), Irland (eu-west-1), Paris (eu-west-3), Stockholm (eu-north-1), Mailand (eu-south-1) und Spanien (eu-south-2).
"@
$body = Get-Content 'c:\Retrac tools\cidrs_code.cs' -Raw
$tail = @"
    }
}
"@
$all = $head + "`r`n" + $body + "`r`n" + $tail
[IO.File]::WriteAllText('c:\Retrac tools\RetracBoost\ServerLockData.cs', $all)
Write-Host "Written ServerLockData.cs length: $(([IO.FileInfo]::new('c:\Retrac tools\RetracBoost\ServerLockData.cs')).Length)"
