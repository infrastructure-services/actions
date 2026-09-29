// Pure physical evidence validation. This module neither discovers nor classifies.
export const TECHNICAL_CATEGORIES = Object.freeze([
  "customSchemas", "userDefinedTypes", "databaseTriggers", "partitionFunctions",
  "partitionSchemes", "userAssemblies", "xmlSchemaCollections", "fullTextCatalogs"
]);
const plain = value => value !== null && typeof value === "object" && !Array.isArray(value) && Object.getPrototypeOf(value) === Object.prototype;
const exact = (value, keys) => plain(value) && Object.keys(value).length === keys.length && keys.every(key => Object.hasOwn(value, key));
const count = value => Number.isSafeInteger(value) && value >= 0;

export function validateTaxonomy(physical) {
  if (!Object.hasOwn(physical, "taxonomy")) return null; // Historical evidence stays readable.
  const taxonomy = physical.taxonomy;
  if (physical.status !== "OBSERVED" || !plain(taxonomy) || taxonomy.version !== 1) return "PHYSICAL_TAXONOMY_INVALID";
  if (taxonomy.coverage === "PARTIAL") {
    return exact(taxonomy, ["version", "coverage"]) && !Object.hasOwn(physical, "technicalObjectCount")
      ? null : "PHYSICAL_TAXONOMY_INVALID";
  }
  if (taxonomy.coverage !== "COMPLETE" || !exact(taxonomy, ["version", "coverage", "counts"]) ||
      !exact(taxonomy.counts, TECHNICAL_CATEGORIES) || !Object.values(taxonomy.counts).every(count) ||
      !count(physical.businessObjectCount) || !count(physical.technicalObjectCount)) return "PHYSICAL_TAXONOMY_INVALID";
  const sum = Object.values(taxonomy.counts).reduce((total, value) => total + value, 0);
  return count(sum) && sum === physical.technicalObjectCount ? null : "PHYSICAL_TAXONOMY_COUNT_MISMATCH";
}

export function physicalState(physical) {
  if (physical.status !== "OBSERVED") return "UNKNOWN";
  if (physical.businessObjectCount > 0) return "POPULATED";
  if (physical.technicalObjectCount > 0) return "TECHNICAL_ONLY";
  return validateTaxonomy(physical) === null && physical.taxonomy?.coverage === "COMPLETE"
    && physical.businessObjectCount === 0 && physical.technicalObjectCount === 0 ? "EMPTY" : "UNKNOWN";
}
