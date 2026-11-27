# Prints the total CPU usage as one JSON line for PadNook.
# Uses the CIM perf class: works on any Windows language (counter names are not localized).
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$all = Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor
$total = [int]($all | Where-Object Name -eq '_Total').PercentProcessorTime
$cores = @($all | Where-Object Name -ne '_Total')
$max = [int]($cores | Measure-Object PercentProcessorTime -Maximum).Maximum
@{
  text  = "$total%"
  value = $total
  state = ($total -ge 80)
  sub   = "$($cores.Count) thread - max $max%"
  icon  = [char]::ConvertFromUtf32(0x1F5A5) + [char]0xFE0F
} | ConvertTo-Json -Compress
