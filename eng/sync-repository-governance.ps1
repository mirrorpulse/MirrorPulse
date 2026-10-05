[CmdletBinding()]
param([string[]]$Repository, [switch]$Apply,
      [string]$EvidencePath = 'artifacts/repository-governance-applied.json')
$ErrorActionPreference = 'Stop'
$settings = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'repository-governance.json') -Raw | ConvertFrom-Json
if ($settings.schemaVersion -ne 1 -or $settings.owner -cne 'MirrorPulse') { throw 'Unsupported governance configuration.' }
$repositories = @($settings.repositories | Where-Object { -not $Repository -or $_.name -cin $Repository })
if ($repositories.Count -eq 0 -or ($Repository -and $repositories.Count -ne $Repository.Count)) { throw 'Unknown or duplicate repository selection.' }
$plan = [ordered]@{ reference=$settings.referenceRepository; repositories=@($repositories.name);
    main='PR from develop; one approval; strict CI; stale reviews dismissed; admins included; linear history; conversations resolved; no force push or deletion';
    develop='Unprotected development branch'; stable='Protected branches; required reviewers; no self review';
    preview='No required reviewer; workflow requires develop'; tags='No update or deletion; no bypass'; applied=$false }
if (-not $Apply) { $plan | ConvertTo-Json -Depth 6; return }
$token = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } else { [Environment]::GetEnvironmentVariable('GITHUB_TOKEN', 'User') }
if ([string]::IsNullOrWhiteSpace($token)) { throw 'GITHUB_TOKEN is required to apply repository settings.' }
$headers = @{ Authorization="Bearer $token"; Accept='application/vnd.github+json'; 'X-GitHub-Api-Version'='2022-11-28' }
function Invoke-GovernanceApi([string]$Path, [string]$Method='GET', $Body=$null, [switch]$AllowNotFound) {
    $arguments = @{ Uri="https://api.github.com/$Path"; Headers=$headers; Method=$Method }
    if ($null -ne $Body) { $arguments.Body=$Body | ConvertTo-Json -Depth 15 -Compress; $arguments.ContentType='application/json' }
    for ($attempt=1; $attempt -le 3; $attempt++) {
        try { return Invoke-RestMethod @arguments } catch {
            $status = [int]$_.Exception.Response.StatusCode
            if ($AllowNotFound -and $status -eq 404) { return $null }
            if ($Method -ceq 'GET' -and $attempt -lt 3 -and ($status -eq 0 -or $status -in @(408,429,500,502,503,504))) {
                Start-Sleep -Seconds $attempt
                continue
            }
            throw "GitHub governance request failed: $Method $Path (HTTP $status). Reapply to reconcile an interrupted mutation."
        }
    }
}
$evidence = @()
try {
    $referenceStable = Invoke-GovernanceApi "repos/$($settings.referenceRepository)/environments/stable"
    $referenceReviewRule = @($referenceStable.protection_rules | Where-Object type -CEQ 'required_reviewers')
    if ($referenceReviewRule.Count -ne 1 -or -not $referenceReviewRule[0].prevent_self_review) { throw 'The CfSharp stable environment review policy requires review.' }
    $reviewers = @($referenceReviewRule[0].reviewers | ForEach-Object { [ordered]@{type=$_.type; id=$_.reviewer.id} })
    foreach ($repositorySettings in $repositories) {
        $name = $repositorySettings.name
        $prefix = "repos/$($settings.owner)/$name"
        $main = Invoke-GovernanceApi "$prefix/branches/main"
        if (-not (Invoke-GovernanceApi "$prefix/branches/develop" -AllowNotFound)) {
            $null = Invoke-GovernanceApi "$prefix/git/refs" 'POST' @{ref='refs/heads/develop';sha=$main.commit.sha}
        }
        foreach ($label in @(@{name='breaking';color='d73a4a';description='Incompatible public contract change'},
                             @{name='feature';color='a2eeef';description='Backward-compatible functionality'},
                             @{name='fix';color='0e8a16';description='Backward-compatible correction'})) {
            $existing = Invoke-GovernanceApi "$prefix/labels/$($label.name)" -AllowNotFound
            $null = if ($existing) { Invoke-GovernanceApi "$prefix/labels/$($label.name)" 'PATCH' $label } else { Invoke-GovernanceApi "$prefix/labels" 'POST' $label }
        }
        $protection = @{
            required_status_checks=@{strict=$true;contexts=@($repositorySettings.checks)};
            enforce_admins=$true; required_pull_request_reviews=@{dismiss_stale_reviews=$true;require_code_owner_reviews=$false;required_approving_review_count=1;require_last_push_approval=$false};
            restrictions=$null;required_linear_history=$true;allow_force_pushes=$false;allow_deletions=$false;required_conversation_resolution=$true
        }
        $null = Invoke-GovernanceApi "$prefix/branches/main/protection" 'PUT' $protection
        $null = Invoke-GovernanceApi "$prefix/environments/stable" 'PUT' @{
            wait_timer=0;reviewers=$reviewers;prevent_self_review=$true;
            deployment_branch_policy=@{protected_branches=$true;custom_branch_policies=$false}
        }
        $null = Invoke-GovernanceApi "$prefix/environments/preview" 'PUT' @{
            wait_timer=0;reviewers=@();prevent_self_review=$false;
            deployment_branch_policy=$null
        }
        $rules = @{name='Protect release tags';target='tag';enforcement='active';bypass_actors=@();
            conditions=@{ref_name=@{include=@($repositorySettings.tagPatterns);exclude=@()}};
            rules=@(@{type='deletion'},@{type='update'})}
        $allRules = Invoke-GovernanceApi "$prefix/rulesets"
        $existingRules = @($allRules | Where-Object name -CEQ $rules.name)
        if ($existingRules.Count -gt 1) { throw 'Duplicate release tag rulesets require review.' }
        $tagRule = if ($existingRules.Count -eq 1) { Invoke-GovernanceApi "$prefix/rulesets/$($existingRules[0].id)" 'PUT' $rules } else { Invoke-GovernanceApi "$prefix/rulesets" 'POST' $rules }
        $actual = Invoke-GovernanceApi "$prefix/branches/main/protection"
        $stable = Invoke-GovernanceApi "$prefix/environments/stable"
        $preview = Invoke-GovernanceApi "$prefix/environments/preview"
        $tags = Invoke-GovernanceApi "$prefix/rulesets/$($tagRule.id)"
        $reviewRule = @($stable.protection_rules | Where-Object type -CEQ 'required_reviewers')
        if (-not $actual.required_status_checks.strict -or
            @((Compare-Object @($actual.required_status_checks.contexts) @($repositorySettings.checks))).Count -ne 0 -or
            -not $actual.enforce_admins.enabled -or $actual.required_pull_request_reviews.required_approving_review_count -ne 1 -or
            -not $actual.required_pull_request_reviews.dismiss_stale_reviews -or -not $actual.required_linear_history.enabled -or
            -not $actual.required_conversation_resolution.enabled -or $actual.allow_force_pushes.enabled -or $actual.allow_deletions.enabled -or
            $reviewRule.Count -ne 1 -or -not $reviewRule[0].prevent_self_review -or
            @((Compare-Object @($reviewRule[0].reviewers.reviewer.id) @($reviewers.id))).Count -ne 0 -or
            -not $stable.deployment_branch_policy.protected_branches -or $stable.deployment_branch_policy.custom_branch_policies -or
            $null -ne $preview.deployment_branch_policy -or @($preview.protection_rules | Where-Object type -CEQ 'required_reviewers').Count -ne 0 -or
            $tags.enforcement -cne 'active' -or $tags.bypass_actors.Count -ne 0 -or
            @((Compare-Object @($tags.conditions.ref_name.include) @($repositorySettings.tagPatterns))).Count -ne 0 -or
            @((Compare-Object @($tags.rules.type) @('deletion','update'))).Count -ne 0) { throw "Governance readback differs for $name." }
        $evidence += [ordered]@{repository="$($settings.owner)/$name";mainSourceSha=$main.commit.sha;protection=$actual;stableEnvironment=$stable;previewEnvironment=$preview;releaseTags=$tags;verified=$true}
        $fullPath = [IO.Path]::GetFullPath($EvidencePath)
        New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
        [ordered]@{schemaVersion=1;complete=($evidence.Count -eq $repositories.Count);appliedAt=[DateTimeOffset]::UtcNow.ToString('O');referenceSourceSha=$settings.referenceSourceSha;repositories=$evidence} |
            ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $fullPath -Encoding utf8
        Write-Host "Verified branch, environment and immutable tag settings for $name."
    }
} finally { $headers.Clear(); $token=$null }
