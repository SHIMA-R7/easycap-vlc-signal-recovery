param(
    [switch]$Preview,
    [ValidateSet('DirectShow','FFmpeg')][string]$Backend = 'FFmpeg',
    [ValidateRange(1,86400)][int]$Seconds = 6,
    [string]$AudioDevice = '',
    [string]$RecordingPath = '',
    [ValidateRange(100,3000)][int]$CacheMs = 0,
    [switch]$Snapshots,
    [ValidateRange(0,3)][double]$Saturation = 1.25,
    [ValidateRange(0,2)][double]$Contrast = 0.95,
    [ValidateRange(0,2)][double]$Brightness = 0.993,
    [ValidateRange(0.01,10)][double]$Gamma = 0.76,
    [ValidateRange(-180,180)][double]$Hue = 0,
    [switch]$Denoise,
    [switch]$EnhanceColor = $true,
    [switch]$NoDenoise,
    [switch]$NaturalColor,
    [switch]$DryRun
)
$ErrorActionPreference = 'Stop'
$vlcCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'VideoLAN\VLC\vlc.exe'),
    (Join-Path $env:ProgramFiles 'VideoLAN\VLC\vlc.exe'),
    (Join-Path $PSScriptRoot 'VLC\vlc.exe')
)
$vlc = $vlcCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $vlc) { throw 'VLC not found. Install VLC or place it in the VLC subfolder.' }
# Match the audio endpoint to the EasyCAP USB container, not a webcam microphone.
if (-not $AudioDevice) {
    $captureVideo = Get-PnpDevice -PresentOnly -Class Camera -ErrorAction SilentlyContinue |
        Where-Object FriendlyName -eq 'AV TO USB2.0' | Select-Object -First 1
    if ($captureVideo) {
        $captureContainer = (Get-PnpDeviceProperty -InstanceId $captureVideo.InstanceId -KeyName DEVPKEY_Device_ContainerId).Data
        $captureAudio = Get-PnpDevice -PresentOnly -Class AudioEndpoint |
            Where-Object { $_.FriendlyName -match 'USB' -and $_.FriendlyName -match '^(マイク|Microphone)' } |
            Where-Object { (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_ContainerId).Data -eq $captureContainer } |
            Select-Object -First 1
        if ($captureAudio) { $AudioDevice = $captureAudio.FriendlyName }
    }
    if (-not $AudioDevice) { throw 'EasyCAP audio input not found. Connect EasyCAP or specify -AudioDevice.' }
}
# VLC 3.0.12 has no dshow-audio-buffer-size option (allocator requests 100ms).
# Keep clock synchronization enabled. Do not cap jitter during baseline diagnosis.
if ($CacheMs -eq 0) { if ($RecordingPath) { $CacheMs = 500 } else { $CacheMs = 300 } }
if ($RecordingPath) {
    $RecordingPath = [IO.Path]::GetFullPath($RecordingPath)
    $extension = [IO.Path]::GetExtension($RecordingPath)
    if ($Backend -eq 'FFmpeg' -and $extension -ne '.mkv') { throw 'FFmpeg capture requires a .mkv recording path (original MJPEG video / PCM audio).' }
    if ($Backend -eq 'DirectShow' -and $extension -ne '.ts') { throw 'DirectShow recording requires a .ts recording path.' }
    if ($RecordingPath.Contains("'")) { throw 'Recording path must not contain an apostrophe.' }
    if (Test-Path -LiteralPath $RecordingPath) { throw 'Recording file already exists.' }
    if (-not $DryRun) { New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($RecordingPath)) | Out-Null }
}
$frames = Join-Path $PSScriptRoot 'frames'
$takeSnapshots = $Snapshots -or (-not $Preview -and -not $RecordingPath)
# Separate logs prevent appended sessions and simultaneous runs from being confused.
$runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff'), $PID
$log = Join-Path $PSScriptRoot "capture-$runId.log"
$vlcArgs = @('--ignore-config','--no-one-instance','--no-qt-updates-notif',
    "--live-caching=$CacheMs",'--clock-synchro=-1',
    '--audio-time-stretch','--no-video-title-show','--avcodec-hw=none',
    '--verbose=2','--file-logging',"--logfile=$log")
if ($takeSnapshots) {
    if (-not $DryRun) { New-Item -ItemType Directory -Path $frames -Force | Out-Null }
    $vlcArgs += @('--scene-format=png','--scene-ratio=25',
        '--scene-prefix=easycap','--scene-replace',"--scene-path=$frames")
}
# Reference-based display correction (Kirby real hardware / RetroArch comparison). Recorded TS retains input colors.
$displayFilters = @()
if ($Preview -and $Denoise -and -not $NoDenoise) {
    $displayFilters += 'hqdn3d'
    # Spatial smoothing reduces diagonal interference without temporal ghosting.
    $vlcArgs += @('--hqdn3d-luma-spat=6','--hqdn3d-chroma-spat=4',
        '--hqdn3d-luma-temp=0','--hqdn3d-chroma-temp=0')
}
if ($Preview -and $EnhanceColor -and -not $NaturalColor) {
    $displayFilters += 'adjust'
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $vlcArgs += @("--saturation=$($Saturation.ToString($culture))", "--contrast=$($Contrast.ToString($culture))",
        "--brightness=$($Brightness.ToString($culture))", "--gamma=$($Gamma.ToString($culture))", "--hue=$($Hue.ToString($culture))")
}
if ($takeSnapshots) { $displayFilters += 'scene' }
if ($displayFilters.Count -gt 0) { $vlcArgs += "--video-filter=$($displayFilters -join ':')" }
if ($Preview) {
    $vlcArgs += @('--meta-title=EasyCAP','--no-fullscreen','--no-qt-video-autoresize',
        '--width=480','--height=360','--zoom=0.666667','--audio')
} else {
    $vlcArgs += @('-I','dummy','--vout=dummy',"--run-time=$Seconds")
    if (-not $RecordingPath) { $vlcArgs += '--no-audio' }
}
if ($Backend -eq 'FFmpeg') {
    $ffmpeg = Join-Path $PSScriptRoot 'tools\ffmpeg.exe'
    $bridge = Join-Path $PSScriptRoot 'EasyCAP-Bridge.exe'
    if (-not (Test-Path -LiteralPath $ffmpeg) -or -not (Test-Path -LiteralPath $bridge)) { throw 'FFmpeg or EasyCAP-Bridge.exe is missing.' }
    # Arrival-time timestamps stay on the PC clock across console signal loss.
    # Copy MJPEG without video re-encoding. Count PCM samples for a steady audio clock; do not stretch audio to packet arrival jitter.
    $ffmpegArgs = @('-hide_banner','-n','-thread_queue_size','64','-rtbufsize','32M',
        '-use_wallclock_as_timestamps','1','-f','dshow','-video_size','720x480',
        '-framerate','25','-vcodec','mjpeg','-sample_rate','48000','-sample_size','16','-channels','2',
        '-i',"video=AV TO USB2.0:audio=$AudioDevice",'-copyts','-start_at_zero',
        '-map','0:v:0','-map','0:a:0','-c:v','copy','-c:a','pcm_s16le',
        '-af','asetpts=N/SR/TB','-fps_mode','passthrough')
    if (-not $Preview) { $ffmpegArgs += @('-t',"$Seconds") }
    $ffmpegArgs += @('-max_interleave_delta','100000','-f','matroska','-live','1',
        '-cluster_time_limit','100','-flush_packets','1','pipe:1')
    $vlcArgs += @("--file-caching=$CacheMs",'--play-and-exit','--demux=avformat','--avformat-format=matroska','fd://0')
    $ffmpegLog = Join-Path $PSScriptRoot "capture-$runId-ffmpeg.log"
    if ($DryRun) {
        [pscustomobject]@{ Executable=$vlc; Version=(Get-Item $vlc).VersionInfo.ProductVersion;
            Arguments=$vlcArgs; Log=$log; FFmpeg=$ffmpeg; FFmpegArguments=$ffmpegArgs;
            FFmpegLog=$ffmpegLog; RecordingPath=$RecordingPath }
        return
    }
    $config = @{ FFmpeg=$ffmpeg; VLC=$vlc; FFmpegArguments=$ffmpegArgs; VLCArguments=$vlcArgs;
        FFmpegLog=$ffmpegLog; RecordingPath=$RecordingPath }
    $configFile = Join-Path ([IO.Path]::GetTempPath()) "easycap-bridge-$runId.json"
    try {
        [IO.File]::WriteAllText($configFile,($config | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
        Write-Host "EasyCAP: FFmpeg wall clock capture; VLC display. Log: $ffmpegLog"
        if ($RecordingPath) { Write-Host "Recording: $RecordingPath" }
        & $bridge $configFile | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "EasyCAP capture failed. See $ffmpegLog" }
    } finally {
        if (Test-Path -LiteralPath $configFile) { Remove-Item -LiteralPath $configFile }
    }
    if ($RecordingPath) {
        $recording = Get-Item -LiteralPath $RecordingPath -ErrorAction SilentlyContinue
        if (-not $recording -or $recording.Length -eq 0) { throw "No recording produced. See $ffmpegLog" }
        $recording | Select-Object FullName,LastWriteTime,Length
    }
    return
}
if ($RecordingPath) {
    $recordTarget = $RecordingPath.Replace('\','/')
    # TS carries explicit timestamps; avoid the AVI negative-length failure path.
    # Decode once for recording; preview branches before the encoder.
    $fileChain = "transcode{vcodec=mp2v,vb=6000,acodec=mpga,ab=192,channels=2,samplerate=48000}:std{access=file,mux=ts,dst='$recordTarget'}"
    if ($Preview) { $sout = "#duplicate{dst=display,dst=`"$fileChain`"}" }
    else { $sout = "#$fileChain" }
    $vlcArgs += @('--sout',$sout,'--sout-mux-caching=300')
}
$vlcArgs += @('dshow://',':dshow-vdev=AV TO USB2.0',":dshow-adev=$AudioDevice",
    ':dshow-size=720x480',':dshow-aspect-ratio=4:3',
    ':dshow-audio-samplerate=48000',':dshow-audio-bitspersample=16',':dshow-audio-channels=2')
# Let the driver negotiate FPS: the old log reports 25fps, so do not force 29.97.
if (-not $Preview) { $vlcArgs += 'vlc://quit' }
if ($DryRun) {
    [pscustomobject]@{ Executable=$vlc; Version=(Get-Item $vlc).VersionInfo.ProductVersion; Arguments=$vlcArgs; Log=$log }
    return
}
Write-Host "VLC: $vlc ($((Get-Item $vlc).VersionInfo.ProductVersion))"
Write-Host "EasyCAP audio input: $AudioDevice; cache: ${CacheMs}ms; log: $log"
$captureStarted = Get-Date
& $vlc @vlcArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "VLC exited with code $LASTEXITCODE. See $log" }
if ($RecordingPath) {
    $recording = Get-Item -LiteralPath $RecordingPath -ErrorAction SilentlyContinue
    if (-not $recording -or $recording.Length -eq 0) { throw "No recording produced. See $log" }
    $recording | Select-Object FullName,LastWriteTime,Length
}
if (-not $Preview -and $takeSnapshots) {
    $frame = Get-Item -LiteralPath (Join-Path $frames 'easycap.png') -ErrorAction SilentlyContinue
    if (-not $frame -or $frame.LastWriteTime -lt $captureStarted) { throw "No fresh frame captured. See $log" }
    $frame | Select-Object FullName,LastWriteTime,Length
}