<#
  Release-Signatur für PXMX Server Suite Updates (ECDSA P-256 / SHA-256, siehe docs\SIGNIERTE-UPDATES.md).

  Der private Schlüssel liegt DPAPI-geschützt (CurrentUser) in %APPDATA%\PXMX-ServerSuite\release-signing.key
  und verlässt diesen PC nie. Er wird nie ausgegeben.

  sign-release.ps1 -Init                       einmalig: Schlüsselpaar erzeugen, gibt NUR den öffentlichen Schlüssel aus
  sign-release.ps1 -PublicKey                  öffentlichen Schlüssel ausgeben (zum Einbetten in src\UpdateSignature.cs)
  sign-release.ps1 [-Exe <pfad>]               signiert PXMX.exe (Standard: .\PXMX.exe) und prüft die Signatur
                                               gegen den in src\UpdateSignature.cs eingebetteten Schlüssel
  sign-release.ps1 -Backup <datei>             passwortgeschützte Offline-Sicherung (AES-256, PBKDF2) schreiben
  sign-release.ps1 -Restore <datei>            Sicherung auf diesem PC/Benutzer wiederherstellen
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(ParameterSetName = 'Init')][switch]$Init,
    [Parameter(ParameterSetName = 'Pub')][switch]$PublicKey,
    [Parameter(ParameterSetName = 'Sign')][string]$Exe,
    [Parameter(ParameterSetName = 'Sign')][string]$Version,
    [Parameter(ParameterSetName = 'Sign')][switch]$Quiet,
    [Parameter(ParameterSetName = 'Backup')][string]$Backup,
    [Parameter(ParameterSetName = 'Restore')][string]$Restore
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security, System.Core
$root = $PSScriptRoot
$keyFile = Join-Path $env:APPDATA 'PXMX-ServerSuite\release-signing.key'
$entropy = [Text.Encoding]::UTF8.GetBytes('PXMX-ServerSuite-release-signing-v1')

function Load-Key {
    if (-not (Test-Path $keyFile)) { throw "Kein Signaturschlüssel in $keyFile – erst 'sign-release.ps1 -Init' (oder -Restore)." }
    $blob = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $entropy, 'CurrentUser')
    try { return [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob) }
    finally { [Array]::Clear($blob, 0, $blob.Length) }
}
function Save-Blob([byte[]]$blob) {
    New-Item -ItemType Directory -Force (Split-Path $keyFile) | Out-Null
    [IO.File]::WriteAllBytes($keyFile, [Security.Cryptography.ProtectedData]::Protect($blob, $entropy, 'CurrentUser'))
}
function Pub([Security.Cryptography.CngKey]$k) { [Convert]::ToBase64String($k.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)) }
function Embedded-Pub {
    $m = [regex]::Match((Get-Content (Join-Path $root 'src\UpdateSignature.cs') -Raw), 'PublicKey\s*=\s*"([^"]*)"')
    return $m.Groups[1].Value
}
function Read-Pw([string]$prompt) {
    $s = Read-Host $prompt -AsSecureString
    $p = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))
    if ($p.Length -lt 12) { throw 'Passwort zu kurz (mindestens 12 Zeichen).' }
    return $p
}

switch ($PSCmdlet.ParameterSetName) {
    'Init' {
        if (Test-Path $keyFile) { throw "Es gibt bereits einen Signaturschlüssel ($keyFile) – wird NICHT überschrieben." }
        $p = New-Object Security.Cryptography.CngKeyCreationParameters
        $p.ExportPolicy = [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
        $k = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, [NullString]::Value, $p)   # NullString: $null would become "" = persisted named key
        $blob = $k.Export([Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        Save-Blob $blob; [Array]::Clear($blob, 0, $blob.Length)
        Write-Host "Schlüssel gespeichert (DPAPI, CurrentUser): $keyFile"
        Write-Host "Öffentlicher Schlüssel (in src\UpdateSignature.cs eintragen):"
        Pub $k
        $k.Dispose()
    }
    'Pub' { $k = Load-Key; Pub $k; $k.Dispose() }
    'Backup' {
        $k = Load-Key; $blob = $k.Export([Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob); $k.Dispose()
        $pw = Read-Pw 'Passwort für die Sicherung'
        $salt = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
        $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 200000)
        $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $kdf.GetBytes(32); $aes.GenerateIV()
        $enc = $aes.CreateEncryptor().TransformFinalBlock($blob, 0, $blob.Length); [Array]::Clear($blob, 0, $blob.Length)
        $mac = (New-Object Security.Cryptography.HMACSHA256 (, $kdf.GetBytes(32))).ComputeHash([byte[]]($aes.IV + $enc))
        $out = 'RPSIGBAK1:' + [Convert]::ToBase64String($salt) + ':' + [Convert]::ToBase64String($aes.IV) + ':' + [Convert]::ToBase64String($enc) + ':' + [Convert]::ToBase64String($mac)
        [IO.File]::WriteAllText($Backup, $out)
        Write-Host "Sicherung geschrieben: $Backup (offline aufbewahren, Passwort getrennt notieren)"
    }
    'Restore' {
        if (Test-Path $keyFile) { throw "Es gibt bereits einen Signaturschlüssel ($keyFile) – erst manuell wegräumen." }
        $parts = ([IO.File]::ReadAllText($Restore)).Trim().Split(':')
        if ($parts.Length -ne 5 -or $parts[0] -ne 'RPSIGBAK1') { throw 'Keine gültige Sicherungsdatei.' }
        $salt, $iv, $enc, $mac = $parts[1..4] | ForEach-Object { , [Convert]::FromBase64String($_) }
        $pw = Read-Pw 'Passwort der Sicherung'
        $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 200000)
        $aesKey = $kdf.GetBytes(32)
        $calc = (New-Object Security.Cryptography.HMACSHA256 (, $kdf.GetBytes(32))).ComputeHash([byte[]]($iv + $enc))
        if ([Convert]::ToBase64String($calc) -ne [Convert]::ToBase64String($mac)) { throw 'Falsches Passwort oder beschädigte Sicherung.' }
        $aes = [Security.Cryptography.Aes]::Create(); $aes.Key = $aesKey; $aes.IV = $iv
        $blob = $aes.CreateDecryptor().TransformFinalBlock($enc, 0, $enc.Length)
        $k = [Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPrivateBlob)
        Save-Blob $blob; [Array]::Clear($blob, 0, $blob.Length)
        Write-Host "Wiederhergestellt: $keyFile"
        if ((Pub $k) -ne (Embedded-Pub)) { Write-Warning 'Achtung: passt NICHT zum öffentlichen Schlüssel in src\UpdateSignature.cs!' }
        $k.Dispose()
    }
    'Sign' {
        if (-not $Exe) { $Exe = Join-Path $root 'PXMX.exe' }
        if (-not $Version) { $Version = [regex]::Match((Get-Content (Join-Path $root 'src\Version.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value }
        $data = [IO.File]::ReadAllBytes((Resolve-Path $Exe))
        $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($data) | ForEach-Object { $_.ToString('x2') })
        $msg = [Text.Encoding]::UTF8.GetBytes("PXMX-ServerSuite|$Version|$sha|$($data.LongLength)")
        $k = Load-Key
        $ec = New-Object Security.Cryptography.ECDsaCng $k
        $ec.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
        $sig = $ec.SignData($msg)
        $pubEc = New-Object Security.Cryptography.ECDsaCng ([Security.Cryptography.CngKey]::Import([Convert]::FromBase64String((Embedded-Pub)), [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
        $pubEc.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
        if (-not $pubEc.VerifyData($msg, $sig)) { throw 'Signatur passt nicht zum eingebetteten öffentlichen Schlüssel (src\UpdateSignature.cs)!' }
        $ec.Dispose(); $k.Dispose()
        $b64 = [Convert]::ToBase64String($sig)
        if (-not $Quiet) { Write-Host "version=$Version sha256=$sha size=$($data.LongLength)" }
        $b64
    }
}
