using System.Runtime.CompilerServices;

// Internal PDF building blocks (token, registry, ...) are visible to the tests.
[assembly: InternalsVisibleTo("Reglet.CompatibilityTests")]

// The validator reads the cross-reference data without the reader's repair.
[assembly: InternalsVisibleTo("Reglet.Validation")]
