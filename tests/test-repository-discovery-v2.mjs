#!/usr/bin/env node

import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { discoverRepository, inspectRepository, parseInput, runCli } from "../scripts/discover-repository-v2.mjs";

const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "repository-discovery-v2-"));
let passed = 0;
const test = (name, fn) => { try { fn(); passed += 1; console.log(`PASS: ${name}`); } catch (error) { console.error(`FAIL: ${name}`); throw error; } };
const repo = name => { const root = path.join(temporaryRoot, name); fs.mkdirSync(root, { recursive: true }); fs.writeFileSync(path.join(root, "App.csproj"), '<Project Sdk="Microsoft.NET.Sdk" />\n'); return root; };
const migration = (root, id, directory = "Migrations", attribute = id) => {
  const target = path.join(root, directory); fs.mkdirSync(target, { recursive: true });
  fs.writeFileSync(path.join(target, `${id}.cs`), "public partial class MigrationBody {}\n");
  fs.writeFileSync(path.join(target, `${id}.Designer.cs`), `[Migration("${attribute}")]\npartial class MigrationMetadata {}\n`);
};

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
} finally {
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
}

console.log(`OK: ${passed} casos de Repository Discovery V2`);
