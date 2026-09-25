$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:JAVA_HOME = "C:\Program Files\Microsoft\jdk-21.0.12.101-hotspot"
$env:ANDROID_HOME = Join-Path $env:LOCALAPPDATA "Android\Sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:Path = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:ANDROID_HOME\cmdline-tools\latest\bin;$env:Path"

if (-not (Test-Path "$env:JAVA_HOME\bin\java.exe")) {
    throw "JDK 17 not found at $env:JAVA_HOME"
}

if (-not (Test-Path "$env:ANDROID_HOME\platforms")) {
    throw "Android SDK is not installed yet. Wait for SDK setup to finish."
}

npx ng build
npx cap sync android
Set-Location (Join-Path $root "android")
.\gradlew.bat assembleDebug --no-daemon
$apk = Join-Path $root "android\app\build\outputs\apk\debug\app-debug.apk"
$out = Join-Path (Resolve-Path "$root\..\..\..") "HaseebBank.apk"
Copy-Item $apk $out -Force
Write-Host "APK ready: $out"
