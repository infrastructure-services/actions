#!/usr/bin/env node

import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { discoverRepository, inspectRawRepository, inspectRepository, parseInput, projectManagedRepository, runCli } from "../scripts/discover-repository-v2.mjs";

const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repository-discovery-v2-"));
let passed = 0;
const test = (name, fn) => { try { fn(); passed += 1; console.log(`PASS: ${name}`); } catch (error) { console.error(`FAIL: ${name}`); throw error; } };
const repo = name => { const root = path.join(temporaryRoot, name); fs.mkdirSync(root, { recursive: true }); fs.writeFileSync(path.join(root, "App.csproj"), '<Project Sdk="Microsoft.NET.Sdk" />\n'); return root; };
const migration = (root, id, directory = "Migrations", attribute = id) => {
  const target = path.join(root, directory); fs.mkdirSync(target, { recursive: true });
  fs.writeFileSync(path.join(target, `${id}.cs`), "public partial class MigrationBody {}\n");
  fs.writeFileSync(path.join(target, `${id}.Designer.cs`), `[Migration("${attribute}")]\npartial class MigrationMetadata {}\n`);
};
const adoptedIds = [
  "20260616182600_AjusteModeloNet10", "20260618202000_AddActivoToPerson", "20260619160000_RemoveActivoFromPerson",
  "20260619184141_AjusteModeloPendiente", "20260624000000_AddTablaPrueba", "20260624000001_AddIndexPrueba", "20260630141200_AddTablaPrueba2"
];
const revision = "a".repeat(40);
const digest = file => crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const adopted = name => {
  const root = path.join(temporaryRoot, name); const projectRoot = path.join(root, "src", "Infrastructure");
  fs.mkdirSync(projectRoot, { recursive: true }); fs.writeFileSync(path.join(projectRoot, "Infrastructure.csproj"), '<Project Sdk="Microsoft.NET.Sdk" />\n');
  const migrations = path.join(projectRoot, "Migrations"); fs.mkdirSync(migrations, { recursive: true });
  const legacy = path.join(migrations, "20211214152511_sql-init.cs"); fs.writeFileSync(legacy, "public partial class SqlInit : Migration {}\n");
  for (const id of adoptedIds) migration(projectRoot, id);
  fs.writeFileSync(path.join(migrations, "ApplicationDbContextModelSnapshot.cs"), "public class ApplicationDbContextModelSnapshot {}\n");
  return {
    root, projectRoot, legacy,
    efSource: {
      sourceRepository: "infrastructure-services/synthetic-api", sourceRevision: revision, projectPath: "src/Infrastructure/Infrastructure.csproj",
      managedMigrationStartId: adoptedIds[0],
      preAdoptionArtifacts: [{ path: "src/Infrastructure/Migrations/20211214152511_sql-init.cs", sha256: digest(legacy), disposition: "EXCLUDE_FROM_MANAGED_LINEAGE", reason: "Synthetic approved pre-adoption artifact" }]
    }
  };
};
const managed = fixture => discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: fixture.root, efSource: fixture.efSource, actualSourceRevision: revision }).repositorySource;
const expectIssue = (fixture, issue) => { const result = managed(fixture); assert.equal(result.status, "INVALID"); assert.equal(result.adoption.issues.includes(issue), true); };

try {
  test("repo EF válido con migrations y orden determinístico", () => {
    const root = repo("valid"); migration(root, "20260202020202_Zeta"); migration(root, "20260101010101_Alpha");
    assert.deepEqual(inspectRepository(root), { status: "PRESENT_VALID", migrations: { count: 2, ids: ["20260101010101_Alpha", "20260202020202_Zeta"] } });
  });
  test("repo inspeccionado sin migrations es exactamente ABSENT", () => assert.deepEqual(inspectRepository(repo("absent")), { status: "ABSENT" }));
  test("NOT_ATTEMPTED explícito", () => assert.deepEqual(discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "NOT_ATTEMPTED" }), { exitCode: 0, repositorySource: { status: "NOT_ATTEMPTED" } }));
  test("UNKNOWN explícito", () => assert.deepEqual(discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "UNKNOWN" }), { exitCode: 0, repositorySource: { status: "UNKNOWN" } }));
  test("error técnico no se transforma en ABSENT", () => {
    const io = { statSync() { throw new Error("C:/secret/path"); } };
    assert.deepEqual(inspectRepository("C:/secret/path", io), { status: "ERROR" });
  });
  test("evidencia inválida", () => { const root = repo("invalid"); migration(root, "20260101010101_Initial", "Migrations", "wrong"); assert.equal(inspectRepository(root).status, "INVALID"); });
  test("MigrationId duplicado dentro de una evidencia", () => {
    const root = repo("duplicate"); migration(root, "20260101010101_Initial");
    fs.appendFileSync(path.join(root, "Migrations", "20260101010101_Initial.cs"), '[Migration("20260101010101_Initial")]\n');
    assert.deepEqual(inspectRepository(root), { status: "INVALID" });
  });
  test("ID inválido", () => { const root = repo("bad-id"); migration(root, "2026010101010_Bad"); assert.equal(inspectRepository(root).status, "INVALID"); });
  test("proyectos ambiguos", () => {
    const root = path.join(temporaryRoot, "ambiguous");
    for (const name of ["A", "B"]) { const project = path.join(root, name); fs.mkdirSync(project, { recursive: true }); fs.writeFileSync(path.join(project, `${name}.csproj`), "<Project />\n"); migration(project, `20260${name === "A" ? "1" : "2"}01010101_${name}`); }
    assert.equal(inspectRepository(root).status, "AMBIGUOUS");
  });
  test("varios proyectos junto a la misma migration son ambiguos", () => {
    const root = path.join(temporaryRoot, "same-directory-ambiguous"); fs.mkdirSync(root, { recursive: true });
    fs.writeFileSync(path.join(root, "A.csproj"), "<Project />\n"); fs.writeFileSync(path.join(root, "B.csproj"), "<Project />\n");
    migration(root, "20260101010101_Initial");
    assert.deepEqual(inspectRepository(root), { status: "AMBIGUOUS" });
  });
  test("snapshots de varios proyectos son ambiguos", () => {
    const root = path.join(temporaryRoot, "snapshot-ambiguous");
    for (const name of ["A", "B"]) { const project = path.join(root, name); fs.mkdirSync(project, { recursive: true }); fs.writeFileSync(path.join(project, `${name}.csproj`), "<Project />\n"); fs.writeFileSync(path.join(project, `${name}ModelSnapshot.cs`), "public class Snapshot {}\n"); }
    assert.deepEqual(inspectRepository(root), { status: "AMBIGUOUS" });
  });
  test("dos linajes en un mismo proyecto son ambiguos", () => {
    const root = repo("multiple-lineages"); migration(root, "20260101010101_First", "FirstMigrations"); migration(root, "20260202020202_Second", "SecondMigrations");
    assert.deepEqual(inspectRepository(root), { status: "AMBIGUOUS" });
  });
  test("entradas no regulares como symlinks no se siguen", () => {
    const io = { statSync: () => ({ isDirectory: () => true }), readdirSync: () => [{ name: "outside", isDirectory: () => false, isFile: () => false, isSymbolicLink: () => true }] };
    assert.deepEqual(inspectRepository("C:/repo", io), { status: "ABSENT" });
  });
  test("caracteres especiales inválidos fallan cerrado", () => { const root = repo("special"); migration(root, "20260101010101_Add_Ñandú"); assert.equal(inspectRepository(root).status, "INVALID"); });
  test("salida sanitizada no filtra paths", () => {
    let stdout = "", stderr = "";
    const exit = runCli({ argv: ["node", "script"], readInput: () => JSON.stringify({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: "C:/private/customer" }), io: { statSync() { throw new Error("C:/private/customer"); } }, writeStdout: x => { stdout += x; }, writeStderr: x => { stderr += x; } });
    assert.equal(exit, 75); assert.equal(`${stdout}${stderr}`.includes("private/customer"), false); assert.equal(JSON.parse(stdout).status, "ERROR");
  });
  test("request inválido falla cerrado", () => assert.equal(parseInput('{"inspectionStatus":"READY"}').exitCode, 65));
  test("adopción sintética conserva raw y proyecta siete migrations managed", () => {
    const fixture = adopted("adopted-valid"); const result = managed(fixture);
    assert.equal(result.status, "PRESENT_VALID");
    assert.deepEqual(result.migrations, { count: 7, ids: adoptedIds });
    assert.equal(result.rawEvidence.migrationArtifacts.length, 15);
    assert.equal(result.rawEvidence.validMigrations.length, 7);
    assert.equal(result.rawEvidence.snapshots.length, 1);
    assert.equal(result.rawEvidence.anomalies.some(item => item.paths.includes("src/Infrastructure/Migrations/20211214152511_sql-init.cs")), true);
    assert.deepEqual(result.adoption.excludedArtifacts, ["src/Infrastructure/Migrations/20211214152511_sql-init.cs"]);
  });
  test("raw evidence contiene paths relativos, proyecto y hashes reales", () => {
    const fixture = adopted("raw-contract"); const raw = inspectRawRepository(fixture.root);
    const legacy = raw.migrationArtifacts.find(item => item.path.endsWith("sql-init.cs"));
    assert.equal(legacy.projectPath, "src/Infrastructure/Infrastructure.csproj"); assert.equal(legacy.sha256, digest(fixture.legacy));
    assert.equal(raw.projects[0].path, "src/Infrastructure/Infrastructure.csproj");
  });
  test("sourceRevision incorrecta falla cerrado", () => { const fixture = adopted("bad-revision"); fixture.efSource.sourceRevision = "b".repeat(40); expectIssue(fixture, "SOURCE_REVISION_MISMATCH"); });
  test("hash incorrecto falla cerrado", () => { const fixture = adopted("bad-hash"); fixture.efSource.preAdoptionArtifacts[0].sha256 = "b".repeat(64); expectIssue(fixture, "PRE_ADOPTION_ARTIFACT_HASH_MISMATCH"); });
  test("artifact modificado falla cerrado", () => { const fixture = adopted("changed-artifact"); fs.appendFileSync(fixture.legacy, "// changed\n"); expectIssue(fixture, "PRE_ADOPTION_ARTIFACT_HASH_MISMATCH"); });
  test("artifact faltante falla cerrado", () => { const fixture = adopted("missing-artifact"); fixture.efSource.preAdoptionArtifacts[0].path = "src/Infrastructure/Migrations/20200101000000_missing.cs"; expectIssue(fixture, "PRE_ADOPTION_ARTIFACT_NOT_FOUND"); });
  test("artifact extra relevante falla cerrado", () => { const fixture = adopted("extra-artifact"); fs.writeFileSync(path.join(fixture.projectRoot, "Migrations", "20210101000000_extra.cs"), "partial class Extra : Migration {}\n"); expectIssue(fixture, "UNAUTHORIZED_PRE_ADOPTION_ARTIFACT"); });
  test("projectPath inconsistente falla cerrado", () => { const fixture = adopted("wrong-project"); fs.mkdirSync(path.join(fixture.root, "src", "Other"), { recursive: true }); fs.writeFileSync(path.join(fixture.root, "src", "Other", "Other.csproj"), "<Project />\n"); fixture.efSource.projectPath = "src/Other/Other.csproj"; fixture.efSource.preAdoptionArtifacts = []; expectIssue(fixture, "MANAGED_START_ID_NOT_FOUND"); });
  test("project inexistente falla cerrado", () => { const fixture = adopted("missing-project"); fixture.efSource.projectPath = "src/Missing/Missing.csproj"; fixture.efSource.preAdoptionArtifacts = []; expectIssue(fixture, "PROJECT_NOT_FOUND"); });
  test("startId inexistente falla cerrado", () => { const fixture = adopted("missing-start"); fixture.efSource.managedMigrationStartId = "20260601000000_Missing"; expectIssue(fixture, "MANAGED_START_ID_NOT_FOUND"); });
  test("startId duplicado falla cerrado", () => { const fixture = adopted("duplicate-start"); migration(fixture.projectRoot, adoptedIds[0], "OtherMigrations"); expectIssue(fixture, "MANAGED_START_ID_DUPLICATE"); });
  test("startId excluido falla cerrado", () => {
    const fixture = adopted("excluded-start"); const body = path.join(fixture.projectRoot, "Migrations", `${adoptedIds[0]}.cs`);
    fixture.efSource.preAdoptionArtifacts.push({ path: `src/Infrastructure/Migrations/${adoptedIds[0]}.cs`, sha256: digest(body), disposition: "EXCLUDE_FROM_MANAGED_LINEAGE", reason: "Invalid attempt" });
    expectIssue(fixture, "MANAGED_START_ID_EXCLUDED");
  });
  test("migration posterior sin Designer falla cerrado", () => { const fixture = adopted("missing-designer"); fs.rmSync(path.join(fixture.projectRoot, "Migrations", `${adoptedIds[6]}.Designer.cs`)); expectIssue(fixture, "MIGRATION_PAIR_INVALID"); });
  test("MigrationId duplicado falla cerrado", () => { const fixture = adopted("duplicate-id"); migration(fixture.projectRoot, adoptedIds[3], "DuplicateMigrations"); expectIssue(fixture, "MIGRATION_ID_DUPLICATE"); });
  test("snapshot ambiguo falla cerrado", () => { const fixture = adopted("ambiguous-snapshot"); fs.writeFileSync(path.join(fixture.projectRoot, "Migrations", "SecondModelSnapshot.cs"), "class SecondModelSnapshot {}\n"); expectIssue(fixture, "SNAPSHOT_AMBIGUOUS"); });
  test("contrato parcial falla cerrado", () => { const fixture = adopted("partial-contract"); delete fixture.efSource.sourceRepository; expectIssue(fixture, "ADOPTION_CONTRACT_INVALID"); });
  test("input adoption parcial falla en boundary", () => { const fixture = adopted("partial-input"); assert.equal(discoverRepository({ repositoryDiscoveryContractVersion: 1, inspectionStatus: "READY", workspace: fixture.root, efSource: fixture.efSource }).code, "ADOPTION_INPUT_PARTIAL"); });
  test("disposition desconocida falla cerrado", () => { const fixture = adopted("unknown-disposition"); fixture.efSource.preAdoptionArtifacts[0].disposition = "IGNORE"; expectIssue(fixture, "ADOPTION_CONTRACT_INVALID"); });
} finally {
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}

console.log(`OK: ${passed} casos de Repository Discovery V2`);
