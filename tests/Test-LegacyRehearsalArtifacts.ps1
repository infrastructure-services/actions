param(
    [Parameter(Mandatory=$true)][string]$SyntheticEvidenceDirectory,
    [string]$WorkflowRoot = (Join-Path $PSScriptRoot '../../workflow'),
    [Parameter(Mandatory=$true)][string]$YamlAssembly
)
$ErrorActionPreference = 'Stop'
$schemaRoot = Join-Path $PSScriptRoot '../tools/DatabaseReleaseQualification/schemas'
$artifact = Get-Content -Raw (Join-Path $schemaRoot 'legacy-artifact-evidence-v1.schema.json') | ConvertFrom-Json -AsHashtable
$checks = 0
function Assert-Schema($name, $value, $expected = $true) {
    $schema = Get-Content -Raw (Join-Path $schemaRoot "$name.schema.json") | ConvertFrom-Json -AsHashtable
    if ($schema.properties.Contains('artifacts')) {
        $schema.properties.artifacts = @{ '$ref' = '#/$defs/valid' }
        $schema['$defs'] = $artifact['$defs']
    }
    $json = $value | ConvertTo-Json -Depth 100 -Compress
    $valid = Test-Json -Json $json -Schema ($schema | ConvertTo-Json -Depth 100 -Compress) -ErrorAction SilentlyContinue
    if ($valid -ne $expected) { throw "Schema assertion failed: $name expected $expected" }
    $script:checks++
}
$receipts = @(Get-ChildItem -LiteralPath $SyntheticEvidenceDirectory -Filter 'synthetic-*.json')
if ($receipts.Count -lt 18) { throw 'Synthetic receipt fixtures missing' }
foreach ($file in $receipts) {
    $receipt = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -AsHashtable
    if ($receipt.evidenceKind -ne 'SYNTHETIC') { throw 'Expected explicitly synthetic fixture' }
    Assert-Schema 'legacy-rehearsal-receipt-v1' $receipt
    $receipt.canProceedToPromotion = $true
    Assert-Schema 'legacy-rehearsal-receipt-v1' $receipt $false
}
$complete = Get-Content -Raw (Join-Path $SyntheticEvidenceDirectory 'synthetic-complete.json') | ConvertFrom-Json -AsHashtable
$grant = @{contractVersion=1;authorizationId='synthetic';targetId='synthetic';packageIdentity=$complete.packageIdentity;preconditionIdentity=('a'*64);engineCommit=('b'*40);environment='TEST';actor='synthetic';notBeforeUtc='2026-01-01T00:00:00Z';expiresAtUtc='2026-01-01T00:30:00Z';approvedPhases=@('FORWARD1','ROLLBACK','FORWARD2');executionAuthorized=$true;approvalReference='SYNTHETIC_ONLY'}
Assert-Schema 'legacy-rehearsal-authorization-v1' $grant
$grant.approvedPhases = @('FORWARD1')
Assert-Schema 'legacy-rehearsal-authorization-v1' $grant $false
$data = @{contractVersion=1;selector='synthetic';version=1;targetId='synthetic';providerKind='SCOPED_ROWSET_SHA256_V1';equality='EXACT_CONTENT_HASH_V1';scope=@{targets=@(@{schema='dbo';object='Synthetic';columns=@('Value')})};maximumRowsPerTable=10}
Assert-Schema 'legacy-data-contract-v1' $data
$data.maximumRowsPerTable = 0
Assert-Schema 'legacy-data-contract-v1' $data $false
$request = @{contractVersion=1;artifactSelection=@{contractVersion=1;manifestPath='synthetic/m.json';expectedCommit=('a'*40);expectedRepository=@{host='github.com';repositoryId='1';fullName='synthetic/repository'}};targetId='synthetic';expectedPackageIdentity=$complete.packageIdentity;authorizationSelector='synthetic';dataContractSelector=$null;executionAuthorized=$false}
Assert-Schema 'legacy-rehearsal-request-v1' $request
$request.truthClaim = 'READY'
Assert-Schema 'legacy-rehearsal-request-v1' $request $false
$freeze = @{contractVersion=1;targetId=$complete.targetId;releaseId=$complete.artifacts.releaseId;artifacts=$complete.artifacts;qualificationIdentity=$complete.qualificationIdentity;evidenceSetHash=$complete.evidenceSetHash;rehearsalReceiptHash=$complete.receiptHash;recoveryClass='FULL_REVERSIBLE';coverage=$complete.evaluation.recoveryCoverage;baselineIdentity=$complete.baselineIdentity;preEvidenceHash=('a'*64);freezeHash=('b'*64)}
Assert-Schema 'legacy-promotion-freeze-v1' $freeze
$freeze.recoveryClass = 'INVALID'
Assert-Schema 'legacy-promotion-freeze-v1' $freeze $false
Add-Type -LiteralPath $YamlAssembly
$yamlFiles = @('legacy-rehearsal-v1-test.yml','legacy-rehearsal-preflight-v1-test.yml','legacy-package-qualification-v1-test.yml','database-discovery-v2-test.yml','database-schema-capture-test.yml')
foreach ($file in $yamlFiles) {
    $path = Join-Path $WorkflowRoot ".github/workflows/$file"
    $reader = [System.IO.StringReader]::new([System.IO.File]::ReadAllText($path))
    try {
        $yaml = [YamlDotNet.RepresentationModel.YamlStream]::new()
        $yaml.Load($reader)
        if ($yaml.Documents.Count -ne 1) { throw "Invalid workflow YAML: $file" }
    } finally { $reader.Dispose() }
}
Write-Output "PASS: $checks schema assertions; $($yamlFiles.Count) workflow YAML documents; no network or runtime"
