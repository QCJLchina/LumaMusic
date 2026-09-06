# 以管理员运行：修复音频端点锁死（播放报"设备被占用（46）"，且确认没有其他播放器在运行）。
# 依次执行：重启音频端点构建服务（级联 Windows Audio）→ 重启 DAC 的 USB 音频接口 →
# 若驱动状态仍死锁（PnP 报"需要重新启动系统"）则重启 USB 父设备（等效断电重连）。
# 执行日志写入 build\zombie-fix.log。
#Requires -RunAsAdministrator
$log = "E:\test astra\LumaMusic\build\zombie-fix.log"
function Note($m) { ($m | Out-File $log -Append -Encoding utf8) }
try {
    Note ("=== run " + (Get-Date -Format o) + " ===")
    Note "restarting AudioEndpointBuilder..."
    Restart-Service AudioEndpointBuilder -Force
    Start-Sleep -Seconds 4
    Note ("AEB done, audiosrv: " + (Get-Service audiosrv).Status)
    Note "restarting SK02 PnP device..."
    $out = pnputil /restart-device "USB\VID_262A&PID_0001&MI_01\6&2844C2DA&0&0001" 2>&1 | Out-String
    Note $out
    Start-Sleep -Seconds 4
    $adg = Get-Process audiodg -ErrorAction SilentlyContinue
    Note ("audiodg: " + $(if ($adg) { $adg.Id.ToString() + " @ " + $adg.StartTime.ToString("HH:mm:ss") } else { "not running" }))
    Note "restarting parent USB device (power-cycle equivalent)..."
    $out2 = pnputil /restart-device "USB\VID_262A&PID_0001\5&1e2daf82&0&4" 2>&1 | Out-String
    Note $out2
    Start-Sleep -Seconds 4
    Note "done"
} catch {
    Note ("ERROR: " + $_.Exception.Message)
}
