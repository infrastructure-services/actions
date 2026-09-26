#!/usr/bin/env node

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const MIGRATION_ID_PATTERN = /^[0-9]{14}_[A-Za-z0-9_]+$/;
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

export function inspectRepository(workspace, io = fs) {
  try {
    const root = path.resolve(workspace);
    if (!io.statSync(root).isDirectory()) return source("ERROR");
    const files = listFiles(root, io);
    const projects = files.filter(file => file.endsWith(".csproj"));
    const csFiles = files.filter(file => file.endsWith(".cs"));
    const snapshots = csFiles.filter(file => file.endsWith("ModelSnapshot.cs"));
    const records = [];
    let invalidEvidence = false;

    for (const file of csFiles) {
      const name = path.basename(file);
      const isDesigner = name.endsWith(".Designer.cs");
      const stem = name.replace(/\.Designer\.cs$|\.cs$/u, "");
      const text = io.readFileSync(file, "utf8");
      const attributes = [...text.matchAll(/\[\s*(?:[A-Za-z0-9_.]+\.)?Migration(?:Attribute)?\s*\(\s*"([^"]*)"/gu)].map(match => match[1]);
      const looksLikeMigration = attributes.length > 0 || /^\d{14}_/u.test(stem);
      if (!looksLikeMigration) continue;
      records.push({ file, stem, isDesigner, attributes, project: nearestProject(file, root, projects) });
    }

    if (records.length === 0) {
      if (snapshots.length === 0) return source("ABSENT");
      const snapshotProjects = new Set(snapshots.map(file => nearestProject(file, root, projects)));
      if (snapshotProjects.has(PROJECT_AMBIGUOUS) || snapshotProjects.size > 1) return source("AMBIGUOUS");
      return source("INVALID");
    }

    const recordProjects = new Set(records.map(record => record.project));
    if (recordProjects.has(PROJECT_AMBIGUOUS)) return source("AMBIGUOUS");
    if (recordProjects.has(null)) return source("INVALID");
    if (recordProjects.size > 1) return source("AMBIGUOUS");
    if (new Set(records.map(record => path.dirname(record.file))).size > 1) return source("AMBIGUOUS");
    if (snapshots.length > 1) return source("AMBIGUOUS");
    if (snapshots.length === 1 && nearestProject(snapshots[0], root, projects) !== records[0].project) return source("AMBIGUOUS");

    const byStem = new Map();
    for (const record of records) {
      const key = path.join(path.dirname(record.file), record.stem);
      const pair = byStem.get(key) ?? { bodies: [], designers: [] };
      (record.isDesigner ? pair.designers : pair.bodies).push(record);
      byStem.set(key, pair);
    }

    const ids = [];
    for (const pair of byStem.values()) {
      if (pair.bodies.length !== 1 || pair.designers.length !== 1) { invalidEvidence = true; continue; }
      const body = pair.bodies[0];
      const designer = pair.designers[0];
      const attributes = [...body.attributes, ...designer.attributes];
      if (!MIGRATION_ID_PATTERN.test(body.stem) || attributes.length !== 1 || attributes[0] !== body.stem) {
        invalidEvidence = true;
        continue;
      }
      ids.push(body.stem);
    }

    if (invalidEvidence || ids.length === 0 || new Set(ids).size !== ids.length) {
      return source("INVALID");
    }
    ids.sort((left, right) => left < right ? -1 : left > right ? 1 : 0);
    return { status: "PRESENT_VALID", migrations: { count: ids.length, ids } };
  } catch {
    return source("ERROR");
  }
}

export function discoverRepository(request, io = fs) {
  if (!exactKeys(request, ["repositoryDiscoveryContractVersion", "inspectionStatus"], ["repositoryDiscoveryContractVersion", "inspectionStatus", "workspace"])) {
    return invalid("REQUEST_INVALID");
  }
  if (request.repositoryDiscoveryContractVersion !== 1) return invalid("CONTRACT_VERSION_INVALID");
  if (!["READY", "UNKNOWN", "NOT_ATTEMPTED"].includes(request.inspectionStatus)) return invalid("INSPECTION_STATUS_INVALID");
  if (request.inspectionStatus !== "READY") {
    if (Object.hasOwn(request, "workspace")) return invalid("WORKSPACE_NOT_ALLOWED");
    return { exitCode: 0, repositorySource: source(request.inspectionStatus) };
  }
  if (typeof request.workspace !== "string" || request.workspace.length === 0) return invalid("WORKSPACE_REQUIRED");
  const repositorySource = inspectRepository(request.workspace, io);
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
