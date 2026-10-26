namespace DatabaseReleaseQualification;

// Default is the unchanged shared/EF contract. LEGACY must opt in at both
// analysis and observation boundaries; this never changes an EF baseline hash.
public enum LegacySqlProfile { Default, ModulesV1 }
