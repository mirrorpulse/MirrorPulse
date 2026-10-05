[CmdletBinding()]
param([string]$SignaturePath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'adapter-signature-policy.ps1')
$inventory = @(foreach ($path in @('worker/a.dll', 'worker/B.dll', 'worker/A.dll', 'locales/é&.txt')) {
    [ordered]@{ path = $path; length = 7L; sha256 = ('A' * 64) }
})
$expected = '[{"path":"locales/\u00E9\u0026.txt","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/A.dll","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/B.dll","length":7,"sha256":"' + ('A' * 64) + '"},' +
    '{"path":"worker/a.dll","length":7,"sha256":"' + ('A' * 64) + '"}]'
$previousCulture = [Globalization.CultureInfo]::CurrentCulture
try {
    foreach ($culture in @('en-US', 'tr-TR', 'fr-FR')) {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($culture)
        if ((Get-AdapterSignatureCanonical -Inventory $inventory) -cne $expected) { throw 'Official signature encoding differs from the product contract.' }
    }
    if ($SignaturePath) {
        $envelope = Get-Content -LiteralPath $SignaturePath -Raw | ConvertFrom-Json
        $trust = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/MirrorPulse.Core/Security/MirrorPulseOfficialAdapterTrust.cs') -Raw
        $publicKey = [regex]::Match($trust, '(?s)-----BEGIN PUBLIC KEY-----.*?-----END PUBLIC KEY-----').Value
        $key = [Security.Cryptography.RSA]::Create()
        try {
            $key.ImportFromPem($publicKey)
            if ($envelope.signer -cne 'MirrorPulse Team' -or $envelope.algorithm -cne 'RSA-SHA256' -or
                !$key.VerifyData([Text.Encoding]::UTF8.GetBytes((Get-AdapterSignatureCanonical -Inventory @($envelope.files))),
                    [Convert]::FromBase64String($envelope.signature), [Security.Cryptography.HashAlgorithmName]::SHA256,
                    [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'The official signature is not trusted.' }
        } finally { $key.Dispose() }
    }
} finally { [Globalization.CultureInfo]::CurrentCulture = $previousCulture }
Write-Host 'Official signature ordinal order, invariant length and product JSON encoding verified.'
