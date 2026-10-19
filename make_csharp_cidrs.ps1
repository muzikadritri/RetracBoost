$lines = Get-Content 'c:\Retrac tools\non_frankfurt_cidrs.txt'
$sb = New-Object System.Text.StringBuilder
$sb.AppendLine("        public static readonly string[] NonFrankfurtCidrs = new[]") | Out-Null
$sb.AppendLine("        {") | Out-Null
for ($i = 0; $i -lt $lines.Count; $i++) {
    $comma = if ($i -lt $lines.Count - 1) { "," } else { "" }
    $sb.AppendLine("            `"$($lines[$i])`"$comma") | Out-Null
}
$sb.AppendLine("        };") | Out-Null
[IO.File]::WriteAllText('c:\Retrac tools\cidrs_code.cs', $sb.ToString())
Write-Host "Generated c:\Retrac tools\cidrs_code.cs with $($lines.Count) entries"
