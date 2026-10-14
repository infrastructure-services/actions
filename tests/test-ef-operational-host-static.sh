#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="$ROOT/tools/DatabaseReleaseQualification"
SOURCES=("$ENGINE/EfOperationalHost.cs" "$ENGINE/EfOperationalCli.cs" "$ENGINE/EfGitHubSourceHost.cs")
if grep -En 'SqlConnection|SqlCommand|ExecuteNonQuery|ExecuteSqlAsync|QualifyAsync|database update|CREATE DATABASE|DROP DATABASE' "${SOURCES[@]}"; then
  echo 'FAIL: operational EF host contains a SQL/execution path'; exit 1
fi
if grep -Ein 'CICDV3|3602|sqlv1testdcsrv1|SEBLOB|Luana|20260701161500|ExecutionAuthorized[[:space:]]*=>[[:space:]]*true|Certified[[:space:]]*=>[[:space:]]*true' "${SOURCES[@]}"; then
  echo 'FAIL: pilot policy or authority grant in generic EF host'; exit 1
fi
grep -Fq '"prepare-ef-release" => EfOperationalCli.RunAsync' "$ENGINE/Program.cs"
for state in '"INVALID" => 65' '"BLOCKED" => 66' '"TIMEOUT" => 124' '"CANCELLED" => 130' 'FileMode.CreateNew'; do
  grep -Fq "$state" "$ENGINE/EfOperationalCli.cs"
done
for boundary in 'AllowAutoRedirect = false' 'A2_1_NORMALIZED_PRODUCER_ADAPTER_REQUIRED' 'EXECUTED_HOST_ATTESTATION_REQUIRED' 'await CheckCurrent()' 'start.Environment.Clear()'; do
  grep -Fq "$boundary" "$ENGINE/EfGitHubSourceHost.cs"
done
if grep -En '[.](PostAsync|PutAsync|DeleteAsync)\(|HttpMethod[.](Post|Put|Delete|Patch)|Assembly[.]Load|GetEnvironmentVariable\(".*(PROVIDER|PLUGIN|ASSEMBLY)' "${SOURCES[@]}"; then
  echo 'FAIL: mutable HTTP or caller-selected provider implementation'; exit 1
fi
echo 'OK: EF operational host has bounded read-only sources, explicit missing producers and no SQL/write authority'
