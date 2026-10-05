/// Immutable settings used by the worker-thread dungeon generator.
public sealed record GenerationConfig
{
	public int MinRoomWidth { get; init; } = 5;
	public int MaxRoomWidth { get; init; } = 14;
	public int MinRoomHeight { get; init; } = 5;
	public int MaxRoomHeight { get; init; } = 12;

	public int MinConnectorsPerRoom { get; init; } = 2;
	public int MaxConnectorsPerRoom { get; init; } = 4;
	public int MinimumConnectorSpacing { get; init; } = 1;

	public int MaximumRooms { get; init; } = 24;
	public int CorridorLengthCells { get; init; } = 1;

	public double BranchProbability { get; init; } = 0.35;
	public double LoopProbability { get; init; } = 0.20;

	/// Rejects configurations that would violate generator invariants.
	public void Validate()
	{
		// RoomDefinition.Size includes the one-tile perimeter wall. A 5x5
		// definition therefore guarantees at least a 3x3 walkable interior.
		if (MinRoomWidth < 5 || MinRoomHeight < 5)
			throw new System.InvalidOperationException(
				"Rooms must have at least a 3x3 interior (5x5 including walls).");

		if (MaxRoomWidth < MinRoomWidth || MaxRoomHeight < MinRoomHeight)
			throw new System.InvalidOperationException("Room size ranges are invalid.");

		if (MinConnectorsPerRoom < 1)
			throw new System.InvalidOperationException("Rooms need at least one connector.");

		if (MaxConnectorsPerRoom < MinConnectorsPerRoom)
			throw new System.InvalidOperationException("Connector count range is invalid.");

		if (MaxConnectorsPerRoom < 2)
			throw new System.InvalidOperationException(
				"The start room requires at least two possible connectors.");

		if (MinimumConnectorSpacing < 1)
			throw new System.InvalidOperationException(
				"Minimum connector spacing must reject adjacent connectors.");

		if (MaximumRooms < 1)
			throw new System.InvalidOperationException("MaximumRooms must be positive.");

		if (CorridorLengthCells < 1)
			throw new System.InvalidOperationException(
				"Connector length must be at least one tile.");

		if (BranchProbability is < 0.0 or > 1.0)
			throw new System.InvalidOperationException("BranchProbability must be in [0, 1].");

		if (LoopProbability is < 0.0 or > 1.0)
			throw new System.InvalidOperationException("LoopProbability must be in [0, 1].");
	}
}
