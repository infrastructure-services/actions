#!/usr/bin/env node

import fs from "node:fs";
import crypto from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";

const MIGRATION_ID_PATTERN = /^[0-9]{14}_[A-Za-z0-9_]+$/;
const REPOSITORY_PATTERN = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/;
const SOURCE_REVISION_PATTERN = /^[0-9a-fA-F]{40}$/;
const SHA256_PATTERN = /^[0-9a-fA-F]{64}$/;
const SAFE_PATH_PATTERN = /^[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.-]+)*$/;
const EXCLUDED_DIRECTORIES = new Set([".git", "bin", "obj", "node_modules"]);
const PROJECT_AMBIGUOUS = Symbol("PROJECT_AMBIGUOUS");

function isPlainObject(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value) && Object.getPrototypeOf(value) === Object.prototype;
}

function exactKeys(value, required, allowed = required) {
  return isPlainObject(value) && required.every(key => Object.hasOwn(value, key)) && Object.keys(value).every(key => allowed.includes(key));
}

function invalid(code) { return { exitCode: 65, code }; }
function source(status) { return { status }; }
function relative(root, file) { return path.relative(root, file).split(path.sep).join("/"); }
function sha256(file, io) { return crypto.createHash("sha256").update(io.readFileSync(file)).digest("hex"); }
function validPath(value) { return typeof value === "string" && SAFE_PATH_PATTERN.test(value) && value.split("/").every(part => part !== "." && part !== ".."); }

function listFiles(root, io) {
  const files = [];
  const visit = directory => {
    const entries = io.readdirSync(directory, { withFileTypes: true })
      .sort((left, right) => left.name.localeCompare(right.name, "en", { sensitivity: "variant" }));
    for (const entry of entries) {
      const absolute = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        if (!EXCLUDED_DIRECTORIES.has(entry.name)) visit(absolute);
      } else if (entry.isFile()) {
        files.push(absolute);
      }
    }
  };
  visit(root);
  return files;
}

function nearestProject(file, workspace, projects) {
  let directory = path.dirname(file);
  while (directory === workspace || directory.startsWith(`${workspace}${path.sep}`)) {
    const matches = projects.filter(project => path.dirname(project) === directory);
    if (matches.length === 1) return matches[0];
    if (matches.length > 1) return PROJECT_AMBIGUOUS;
    if (directory === workspace) break;
    directory = path.dirname(directory);
  }
  return null;
}

function projectReference(project, root) {
  if (project === PROJECT_AMBIGUOUS) return "AMBIGUOUS";
  return project === null ? null : relative(root, project);
}

export function inspectRawRepository(workspace, io = fs) {
  const root = path.resolve(workspace);
  if (!io.statSync(root).isDirectory()) throw new Error("WORKSPACE_NOT_DIRECTORY");
  const files = listFiles(root, io);
  const projects = files.filter(file => file.endsWith(".csproj"));
  const csFiles = files.filter(file => file.endsWith(".cs"));
  const snapshots = csFiles.filter(file => file.endsWith("ModelSnapshot.cs"));
  const artifacts = [];

  for (const file of csFiles) {
    const name = path.basename(file);
    const isDesigner = name.endsWith(".Designer.cs");
    const stem = name.replace(/\.Designer\.cs$|\.cs$/u, "");
    const text = io.readFileSync(file, "utf8");
    const attributes = [...text.matchAll(/\[\s*(?:[A-Za-z0-9_.]+\.)?Migration(?:Attribute)?\s*\(\s*"([^"]*)"/gu)].map(match => match[1]);
    if (attributes.length === 0 && !/^\d{14}_/u.test(stem)) continue;
    const project = nearestProject(file, root, projects);
    artifacts.push({
      path: relative(root, file),
      projectPath: projectReference(project, root),
      kind: isDesigner ? "MIGRATION_DESIGNER" : "MIGRATION_BODY",
      stem,
      migrationAttributes: attributes,
      sha256: sha256(file, io)
    });
  }

  const rawSnapshots = snapshots.map(file => ({
    path: relative(root, file),
    projectPath: projectReference(nearestProject(file, root, projects), root),
    sha256: sha256(file, io)
  }));
  const byStem = new Map();
  for (const artifact of artifacts) {
    const directory = path.posix.dirname(artifact.path);
    const key = `${directory}/${artifact.stem}`;
    const pair = byStem.get(key) ?? { bodies: [], designers: [] };
    (artifact.kind === "MIGRATION_DESIGNER" ? pair.designers : pair.bodies).push(artifact);
    byStem.set(key, pair);
  }

  const validMigrations = [];
  const anomalies = [];
  for (const pair of byStem.values()) {
    const paths = [...pair.bodies, ...pair.designers].map(item => item.path).sort();
    if (pair.bodies.length !== 1 || pair.designers.length !== 1) {
      anomalies.push({ code: "MIGRATION_PAIR_INVALID", paths });
      continue;
    }
    const body = pair.bodies[0];
    const designer = pair.designers[0];
    const attributes = [...body.migrationAttributes, ...designer.migrationAttributes];
    if (!MIGRATION_ID_PATTERN.test(body.stem) || attributes.length !== 1 || attributes[0] !== body.stem) {
      anomalies.push({ code: "MIGRATION_METADATA_INVALID", paths });
      continue;
    }
    if (body.projectPath !== designer.projectPath) {
      anomalies.push({ code: "MIGRATION_PROJECT_MISMATCH", paths });
      continue;
    }
    validMigrations.push({ id: body.stem, projectPath: body.projectPath, bodyPath: body.path, designerPath: designer.path });
  }
  const counts = new Map();
  for (const migration of validMigrations) counts.set(migration.id, (counts.get(migration.id) ?? 0) + 1);
  for (const [id, count] of counts) if (count > 1) {
    anomalies.push({ code: "MIGRATION_ID_DUPLICATE", migrationId: id, paths: validMigrations.filter(item => item.id === id).flatMap(item => [item.bodyPath, item.designerPath]).sort() });
  }
  for (const artifact of artifacts) {
    if (artifact.projectPath === "AMBIGUOUS") anomalies.push({ code: "PROJECT_ASSOCIATION_AMBIGUOUS", paths: [artifact.path] });
    else if (artifact.projectPath === null) anomalies.push({ code: "PROJECT_ASSOCIATION_MISSING", paths: [artifact.path] });
  }
  if (rawSnapshots.length > 1) anomalies.push({ code: "SNAPSHOT_AMBIGUOUS", paths: rawSnapshots.map(item => item.path).sort() });
  for (const snapshot of rawSnapshots) {
    if (snapshot.projectPath === "AMBIGUOUS") anomalies.push({ code: "SNAPSHOT_PROJECT_AMBIGUOUS", paths: [snapshot.path] });
    else if (snapshot.projectPath === null) anomalies.push({ code: "SNAPSHOT_PROJECT_MISSING", paths: [snapshot.path] });
  }

  return {
    rawEvidenceContractVersion: 1,
    projects: projects.map(file => ({ path: relative(root, file) })),
    migrationArtifacts: artifacts,
    validMigrations,
    snapshots: rawSnapshots,
    anomalies
  };
}

function legacyProjection(raw) {
  if (raw.migrationArtifacts.length === 0) {
    if (raw.snapshots.length === 0) return source("ABSENT");
    const snapshotProjects = new Set(raw.snapshots.map(item => item.projectPath));
    if (snapshotProjects.has("AMBIGUOUS") || snapshotProjects.size > 1) return source("AMBIGUOUS");
    return source("INVALID");
  }
  const recordProjects = new Set(raw.migrationArtifacts.map(item => item.projectPath));
  if (recordProjects.has("AMBIGUOUS")) return source("AMBIGUOUS");
  if (recordProjects.has(null)) return source("INVALID");
  if (recordProjects.size > 1) return source("AMBIGUOUS");
  if (new Set(raw.migrationArtifacts.map(item => path.posix.dirname(item.path))).size > 1) return source("AMBIGUOUS");
  if (raw.snapshots.length > 1) return source("AMBIGUOUS");
  if (raw.snapshots.length === 1 && raw.snapshots[0].projectPath !== raw.migrationArtifacts[0].projectPath) return source("AMBIGUOUS");
  if (raw.anomalies.length > 0 || raw.validMigrations.length === 0) return source("INVALID");
  const ids = raw.validMigrations.map(item => item.id);
  if (new Set(ids).size !== ids.length) return source("INVALID");
  ids.sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
  return { status: "PRESENT_VALID", migrations: { count: ids.length, ids } };
}

function validEfSource(value) {
  if (!exactKeys(value, ["sourceRepository", "sourceRevision", "projectPath", "managedMigrationStartId", "preAdoptionArtifacts"]) ||
      typeof value.sourceRepository !== "string" || !REPOSITORY_PATTERN.test(value.sourceRepository) || value.sourceRepository.split("/").some(part => part === "." || part === "..") ||
      typeof value.sourceRevision !== "string" || !SOURCE_REVISION_PATTERN.test(value.sourceRevision) ||
      !validPath(value.projectPath) || !value.projectPath.endsWith(".csproj") ||
      typeof value.managedMigrationStartId !== "string" || !MIGRATION_ID_PATTERN.test(value.managedMigrationStartId) ||
      !Array.isArray(value.preAdoptionArtifacts)) return false;
  const projectDirectory = value.projectPath.slice(0, value.projectPath.lastIndexOf("/"));
  const seen = new Set();
  for (const artifact of value.preAdoptionArtifacts) {
    if (!exactKeys(artifact, ["path", "sha256", "disposition", "reason"]) || !validPath(artifact.path) ||
        (projectDirectory && !artifact.path.startsWith(`${projectDirectory}/`)) || artifact.path === value.projectPath ||
        typeof artifact.sha256 !== "string" || !SHA256_PATTERN.test(artifact.sha256) ||
        artifact.disposition !== "EXCLUDE_FROM_MANAGED_LINEAGE" ||
        typeof artifact.reason !== "string" || artifact.reason.trim().length === 0 || artifact.reason.length > 256 || /[\u0000-\u001f\u007f]/u.test(artifact.reason) || seen.has(artifact.path)) return false;
    seen.add(artifact.path);
  }
  return true;
}

function blocked(rawEvidence, issues) {
  return { status: "INVALID", adoption: { status: "BLOCKED", issues: [...new Set(issues)] }, rawEvidence };
}

export function projectManagedRepository(rawEvidence, efSource, actualSourceRevision, callerSource) {
  const issues = [];
  if (!validEfSource(efSource)) return blocked(rawEvidence, ["ADOPTION_CONTRACT_INVALID"]);
  if (typeof actualSourceRevision !== "string" || !SOURCE_REVISION_PATTERN.test(actualSourceRevision)) issues.push("ACTUAL_SOURCE_REVISION_INVALID");
  else if (callerSource !== undefined) {
    if (!exactKeys(callerSource, ["sourceRepository", "sourceRevision"], ["sourceRepository", "sourceRevision"]) ||
        callerSource.sourceRepository !== efSource.sourceRepository ||
        typeof callerSource.sourceRevision !== "string" || !SOURCE_REVISION_PATTERN.test(callerSource.sourceRevision) ||
        callerSource.sourceRevision.toLowerCase() !== actualSourceRevision.toLowerCase()) issues.push("CALLER_SOURCE_MISMATCH");
  }
  else if (actualSourceRevision.toLowerCase() !== efSource.sourceRevision.toLowerCase()) issues.push("SOURCE_REVISION_MISMATCH");
  const projectMatches = rawEvidence.projects.filter(item => item.path === efSource.projectPath);
  if (projectMatches.length === 0) issues.push("PROJECT_NOT_FOUND");
  else if (projectMatches.length !== 1) issues.push("PROJECT_AMBIGUOUS");

  const artifactsByPath = new Map(rawEvidence.migrationArtifacts.map(item => [item.path, item]));
  const excluded = new Set();
  for (const declaration of efSource.preAdoptionArtifacts) {
    const artifact = artifactsByPath.get(declaration.path);
    if (!artifact) { issues.push("PRE_ADOPTION_ARTIFACT_NOT_FOUND"); continue; }
    if (artifact.projectPath !== efSource.projectPath) issues.push("PRE_ADOPTION_ARTIFACT_PROJECT_MISMATCH");
    if (artifact.sha256.toLowerCase() !== declaration.sha256.toLowerCase()) issues.push("PRE_ADOPTION_ARTIFACT_HASH_MISMATCH");
    if (MIGRATION_ID_PATTERN.test(artifact.stem) && artifact.stem >= efSource.managedMigrationStartId) issues.push("PRE_ADOPTION_ARTIFACT_NOT_BEFORE_BOUNDARY");
    excluded.add(declaration.path);
  }

  const scopedMigrations = rawEvidence.validMigrations.filter(item => item.projectPath === efSource.projectPath);
  const startMatches = scopedMigrations.filter(item => item.id === efSource.managedMigrationStartId);
  if (startMatches.length === 0) issues.push("MANAGED_START_ID_NOT_FOUND");
  else if (startMatches.length !== 1) issues.push("MANAGED_START_ID_DUPLICATE");
  if (startMatches.some(item => excluded.has(item.bodyPath) || excluded.has(item.designerPath))) issues.push("MANAGED_START_ID_EXCLUDED");

  const scopedArtifacts = rawEvidence.migrationArtifacts.filter(item => item.projectPath === efSource.projectPath);
  const managedMigrations = scopedMigrations.filter(item => item.id >= efSource.managedMigrationStartId);
  const managedPaths = new Set(managedMigrations.flatMap(item => [item.bodyPath, item.designerPath]));
  if (new Set(managedMigrations.map(item => path.posix.dirname(item.bodyPath))).size > 1) issues.push("MANAGED_LINEAGE_AMBIGUOUS");
  for (const artifact of scopedArtifacts) {
    if (!managedPaths.has(artifact.path) && !excluded.has(artifact.path)) issues.push("UNAUTHORIZED_PRE_ADOPTION_ARTIFACT");
    if (managedPaths.has(artifact.path) && excluded.has(artifact.path)) issues.push("MANAGED_ARTIFACT_EXCLUDED");
  }
  for (const anomaly of rawEvidence.anomalies) {
    const relevant = anomaly.paths.filter(item => artifactsByPath.get(item)?.projectPath === efSource.projectPath);
    if (relevant.some(item => !excluded.has(item))) issues.push(anomaly.code);
  }
  const scopedSnapshots = rawEvidence.snapshots.filter(item => item.projectPath === efSource.projectPath);
  if (scopedSnapshots.length > 1) issues.push("SNAPSHOT_AMBIGUOUS");
  if (rawEvidence.snapshots.some(item => item.projectPath === "AMBIGUOUS")) issues.push("SNAPSHOT_AMBIGUOUS");
  const ids = managedMigrations.map(item => item.id).sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
  if (new Set(ids).size !== ids.length) issues.push("MIGRATION_ID_DUPLICATE");
  if (ids.length === 0) issues.push("MANAGED_LINEAGE_EMPTY");
  if (issues.length > 0) return blocked(rawEvidence, issues);
  return {
    status: "PRESENT_VALID",
    migrations: { count: ids.length, ids },
    adoption: { status: "VALID", ...(callerSource === undefined ? {} : { acquisitionMode: "CALLER_SOURCE", adoptionSourceRevision: efSource.sourceRevision.toLowerCase(), sourceRepository: callerSource.sourceRepository }), sourceRevision: actualSourceRevision.toLowerCase(), projectPath: efSource.projectPath, managedMigrationStartId: efSource.managedMigrationStartId, excludedArtifacts: [...excluded].sort() },
    rawEvidence
  };
}

export function inspectRepository(workspace, io = fs) {
  try {
    return legacyProjection(inspectRawRepository(workspace, io));
  } catch {
    return source("ERROR");
  }
}

export function discoverRepository(request, io = fs) {
  if (!exactKeys(request, ["repositoryDiscoveryContractVersion", "inspectionStatus"], ["repositoryDiscoveryContractVersion", "inspectionStatus", "workspace", "efSource", "actualSourceRevision", "callerSource"])) {
    return invalid("REQUEST_INVALID");
  }
  if (request.repositoryDiscoveryContractVersion !== 1) return invalid("CONTRACT_VERSION_INVALID");
  if (!["READY", "UNKNOWN", "NOT_ATTEMPTED"].includes(request.inspectionStatus)) return invalid("INSPECTION_STATUS_INVALID");
  if (request.inspectionStatus !== "READY") {
    if (Object.hasOwn(request, "workspace") || Object.hasOwn(request, "efSource") || Object.hasOwn(request, "actualSourceRevision") || Object.hasOwn(request, "callerSource")) return invalid("WORKSPACE_NOT_ALLOWED");
    return { exitCode: 0, repositorySource: source(request.inspectionStatus) };
  }
  if (typeof request.workspace !== "string" || request.workspace.length === 0) return invalid("WORKSPACE_REQUIRED");
  const hasEfSource = Object.hasOwn(request, "efSource");
  if ((!hasEfSource && Object.hasOwn(request, "callerSource")) || hasEfSource !== Object.hasOwn(request, "actualSourceRevision")) return invalid("ADOPTION_INPUT_PARTIAL");
  let repositorySource;
  try {
    const rawEvidence = inspectRawRepository(request.workspace, io);
    repositorySource = hasEfSource ? projectManagedRepository(rawEvidence, request.efSource, request.actualSourceRevision, request.callerSource) : legacyProjection(rawEvidence);
  } catch {
    repositorySource = source("ERROR");
  }
  return { exitCode: repositorySource.status === "ERROR" ? 75 : 0, repositorySource };
}

export function parseInput(text, io = fs) {
  if (text.length === 0) return { exitCode: 64, code: "STDIN_REQUIRED" };
  try { return discoverRepository(JSON.parse(text), io); }
  catch { return invalid("JSON_INVALID"); }
}

export function runCli({ argv = process.argv, readInput = () => fs.readFileSync(0, "utf8"), writeStdout = value => fs.writeSync(1, value), writeStderr = value => fs.writeSync(2, value), io = fs } = {}) {
  let outcome;
  try { outcome = argv.length === 2 ? parseInput(readInput(), io) : { exitCode: 64, code: "ARGUMENTS_NOT_ALLOWED" }; }
  catch { outcome = { exitCode: 70, code: "DISCOVERY_INTERNAL_ERROR" }; }
  if (outcome.repositorySource) {
    try { writeStdout(`${JSON.stringify(outcome.repositorySource)}\n`); }
    catch { outcome = { exitCode: 70, code: "DISCOVERY_INTERNAL_ERROR" }; }
  }
  if (outcome.exitCode !== 0) {
    const code = outcome.code ?? (outcome.repositorySource?.status === "ERROR" ? "REPOSITORY_INSPECTION_FAILED" : "DISCOVERY_INTERNAL_ERROR");
    try { writeStderr(`repository-discovery-v2: ${code}\n`); } catch { /* Diagnostic output unavailable. */ }
  }
  return outcome.exitCode;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) process.exitCode = runCli();
