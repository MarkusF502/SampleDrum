param(
    [Parameter(Mandatory)][string]$InputApk,
    [Parameter(Mandatory)][string]$OutputApk,
    [Parameter(Mandatory)][string]$BuildToolsDirectory,
    [string]$JavaDirectory = $env:JAVA_HOME,
    [string]$SigningDirectory = (Join-Path $PSScriptRoot '../mobile/android/.signing')
)
$ErrorActionPreference = 'Stop'
if (!$JavaDirectory) { throw 'Set JAVA_HOME or pass -JavaDirectory (JDK 17+).' }
$java = Join-Path $JavaDirectory 'bin/java.exe'
$keytool = Join-Path $JavaDirectory 'bin/keytool.exe'
$align = Join-Path $BuildToolsDirectory 'zipalign.exe'
$signer = Join-Path $BuildToolsDirectory 'lib/apksigner.jar'
foreach ($file in @($InputApk, $java, $keytool, $align, $signer)) {
    if (!(Test-Path -LiteralPath $file)) { throw "Missing: $file" }
}
New-Item -ItemType Directory -Path $SigningDirectory -Force | Out-Null
$key = Join-Path $SigningDirectory 'sampledrum-release.p12'
$passwordFile = Join-Path $SigningDirectory 'password.txt'
if (!(Test-Path -LiteralPath $key)) {
    if (Test-Path -LiteralPath $passwordFile) { throw 'Password exists without its key. Restore the signing backup first.' }
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [Convert]::ToBase64String($bytes) | Set-Content -LiteralPath $passwordFile -Encoding ascii -NoNewline
    & $keytool -genkeypair -keystore $key -storetype PKCS12 -alias sampledrum -keyalg RSA -keysize 3072 -sigalg SHA256withRSA -validity 10000 -dname 'CN=SampleDrum Mobile, O=MarkusF502' '-storepass:file' $passwordFile
    if ($LASTEXITCODE -ne 0) { throw 'Key generation failed. Keep signing files for recovery.' }
}
if (!(Test-Path -LiteralPath $passwordFile)) { throw 'Signing password missing. Restore the signing backup.' }
$aligned = "$OutputApk.aligned"
# Keep upstream license notices in APKs built before the asset was introduced, too.
$staged = "$OutputApk.unsigned"
Copy-Item -LiteralPath $InputApk -Destination $staged
$notices = Join-Path $PSScriptRoot '../mobile/android/app/src/main/assets/THIRD_PARTY_NOTICES.txt'
if (Test-Path -LiteralPath $notices) {
    $archive = [IO.Compression.ZipFile]::Open($staged, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry('assets/THIRD_PARTY_NOTICES.txt')
        if ($entry) { $entry.Delete() }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $notices, 'assets/THIRD_PARTY_NOTICES.txt') | Out-Null
    } finally { $archive.Dispose() }
}
& $align -f -P 16 4 $staged $aligned
if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }
& $java -jar $signer sign --ks $key --ks-key-alias sampledrum --ks-pass "file:$passwordFile" --out $OutputApk $aligned
if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
& $java -jar $signer verify --verbose --print-certs $OutputApk
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
& $align -c -P 16 4 $OutputApk
if ($LASTEXITCODE -ne 0) { throw 'Signed APK alignment verification failed.' }
Remove-Item -LiteralPath $aligned
Remove-Item -LiteralPath $staged
Write-Output "Signed APK: $OutputApk"
