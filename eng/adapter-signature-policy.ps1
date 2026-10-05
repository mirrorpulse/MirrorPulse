Set-StrictMode -Version Latest


function Get-AdapterSignatureCanonical {
    param([Parameter(Mandatory)][object[]]$Inventory)
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $Inventory) {
        Assert-AdapterPackagePath $file.path
        if ($file.length -lt 0 -or $file.sha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $entries.ContainsKey($file.path)) {
            throw 'The signature inventory is invalid.'
        }
        $entries.Add($file.path, $file)
    }
    $paths = [string[]]@($entries.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $builder = [Text.StringBuilder]::new('[')
    $separator = ''
    foreach ($path in $paths) {
        $file = $entries[$path]
        # Match the product UTF-8 JSON encoder, ordinal order and exact field order.
        $encoded = [Text.Json.JsonSerializer]::Serialize($path, [string], [Text.Json.JsonSerializerOptions]::Default)
        $null = $builder.Append($separator).Append('{"path":').Append($encoded).Append(',"length":')
        $null = $builder.Append(([long]$file.length).ToString([Globalization.CultureInfo]::InvariantCulture))
        $null = $builder.Append(',"sha256":"').Append($file.sha256).Append('"}')
        $separator = ','
    }
    return $builder.Append(']').ToString()
}

function Assert-AdapterPackagePath {
    param([Parameter(Mandatory)][string]$Path)
    if ($Path.Length -gt 1024 -or $Path.Contains('\') -or $Path.StartsWith('/') -or $Path.Contains('~')) { throw 'The package path is not canonical.' }
    foreach ($segment in $Path.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment.Length -gt 255 -or $segment.Trim() -cne $segment -or
            $segment.EndsWith('.') -or $segment -in @('.', '..') -or $segment -match '[\x00-\x1f\x7f<>:"|?*]' -or
            $segment.Split('.')[0] -match '^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|CLOCK\$|COM[1-9¹²³]|LPT[1-9¹²³])$' -or
            $segment.Normalize() -cne $segment) { throw 'The package path is not canonical.' }
    }
}
