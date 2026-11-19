namespace Reglet;

/// <summary>A single OPC relationship.</summary>
/// <param name="Id">Unique ID within the source part.</param>
/// <param name="Type">Beziehungstyp (URI).</param>
/// <param name="Target">Target part or external address.</param>
/// <param name="TargetMode">Optional <c>External</c> for external targets.</param>
internal sealed record OpcRelationship(string Id, string Type, string Target, string? TargetMode = null);
