namespace Reglet;

/// <summary>Progress of a write operation (phase AE, AE01).</summary>
/// <param name="Step">The step that is being progressed (currently <c>section</c>).</param>
/// <param name="Completed">Number of completed units.</param>
/// <param name="Total">Total number of units.</param>
public readonly record struct WriteProgress(string Step, int Completed, int Total);
