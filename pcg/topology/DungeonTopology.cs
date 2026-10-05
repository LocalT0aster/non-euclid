using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// Stable logical room identifier. It is not a world-space position.
public readonly record struct RoomId(int Value)
{
	public override string ToString() => $"R{Value}";
}

/// Stable connector identifier scoped to a room.
public readonly record struct ConnectorId(RoomId Room, int Index)
{
	public override string ToString() => $"{Room}:C{Index}";
}

/// Cardinal direction in room-local grid space.
public enum GridDirection
{
	North = 0,
	East = 1,
	South = 2,
	West = 3
}

/// Grid-direction helpers used by connector alignment.
public static class GridDirectionExtensions
{
	public static Vector2I ToVector(this GridDirection direction) => direction switch
	{
		GridDirection.North => Vector2I.Up,
		GridDirection.East => Vector2I.Right,
		GridDirection.South => Vector2I.Down,
		GridDirection.West => Vector2I.Left,
		_ => throw new ArgumentOutOfRangeException(nameof(direction))
	};

	public static GridDirection Rotate(this GridDirection direction, int quarterTurns)
	{
		int normalizedTurns = GridTransform.NormalizeQuarterTurns(quarterTurns);
		return (GridDirection)(((int)direction + normalizedTurns) % 4);
	}

	public static GridDirection Opposite(this GridDirection direction) => direction.Rotate(2);

	public static int QuarterTurnsTo(this GridDirection from, GridDirection to)
		=> GridTransform.NormalizeQuarterTurns((int)to - (int)from);
}

/// Exact local-grid transform using integer translation and quarter turns.
public readonly record struct GridTransform(Vector2I Translation, int QuarterTurns)
{
	public static GridTransform Identity => new(Vector2I.Zero, 0);

	public GridTransform Normalized => new(Translation, NormalizeQuarterTurns(QuarterTurns));

	public Vector2I ApplyCell(Vector2I cell)
		=> RotateCell(cell, QuarterTurns) + Translation;

	public GridDirection ApplyDirection(GridDirection direction)
		=> direction.Rotate(QuarterTurns);

	public float CanvasRotation
		=> NormalizeQuarterTurns(QuarterTurns) * Mathf.Pi / 2.0f;

	/// Places a target room so the two connector cells and facings align.
	public static GridTransform AlignConnectedRoom(
		GridTransform sourceTransform,
		ConnectorDefinition sourceConnector,
		ConnectorDefinition targetConnector,
		int corridorLengthCells)
	{
		if (corridorLengthCells < 1)
			throw new ArgumentOutOfRangeException(nameof(corridorLengthCells));

		GridDirection sourceWorldFacing = sourceTransform.ApplyDirection(sourceConnector.Facing);
		GridDirection desiredTargetFacing = sourceWorldFacing.Opposite();
		int targetQuarterTurns = targetConnector.Facing.QuarterTurnsTo(desiredTargetFacing);

		Vector2I sourceDoorWorld = sourceTransform.ApplyCell(sourceConnector.Cell);
		Vector2I facing = sourceWorldFacing.ToVector();

		// Connector length counts the total number of doorway/corridor cells,
		// including both endpoint door cells. Therefore a one-cell connector
		// is one shared tile: source and target connector cells coincide.
		int doorCenterDistance = Math.Max(0, corridorLengthCells - 1);
		Vector2I targetDoorWorld = sourceDoorWorld + facing * doorCenterDistance;

		Vector2I rotatedTargetDoor = RotateCell(targetConnector.Cell, targetQuarterTurns);
		return new GridTransform(
			targetDoorWorld - rotatedTargetDoor,
			targetQuarterTurns
		);
	}

	/// Rotates a grid cell around the local origin by quarter turns.
	public static Vector2I RotateCell(Vector2I cell, int quarterTurns)
	{
		return NormalizeQuarterTurns(quarterTurns) switch
		{
			0 => cell,
			1 => new Vector2I(-cell.Y, cell.X),
			2 => new Vector2I(-cell.X, -cell.Y),
			3 => new Vector2I(cell.Y, -cell.X),
			_ => throw new InvalidOperationException()
		};
	}

	public static int NormalizeQuarterTurns(int quarterTurns)
	{
		int normalized = quarterTurns % 4;
		return normalized < 0 ? normalized + 4 : normalized;
	}
}

/// Graph-backed doorway on a room perimeter.
public sealed record ConnectorDefinition(
	ConnectorId Id,
	Vector2I Cell,
	GridDirection Facing
);

/// Immutable room-local geometry; Size includes the one-tile wall perimeter.
public sealed record RoomDefinition(
	RoomId Id,
	string Name,
	Vector2I Size,
	IReadOnlyList<ConnectorDefinition> Connectors,
	ulong ContentSeed = 0
);

/// Undirected graph edge joining exactly two room connectors.
public sealed record RoomConnection(ConnectorId A, ConnectorId B)
{
	/// Returns the room at the opposite endpoint.
	public RoomId OtherRoom(RoomId room)
	{
		if (A.Room == room)
			return B.Room;
		if (B.Room == room)
			return A.Room;

		throw new ArgumentException($"Room {room} is not part of this connection.");
	}

	/// Returns this edge's connector that belongs to the given room.
	public ConnectorId ConnectorFor(RoomId room)
	{
		if (A.Room == room)
			return A;
		if (B.Room == room)
			return B;

		throw new ArgumentException($"Room {room} is not part of this connection.");
	}

	/// Returns the connector at the opposite endpoint.
	public ConnectorId OtherConnector(RoomId room)
	{
		if (A.Room == room)
			return B;
		if (B.Room == room)
			return A;

		throw new ArgumentException($"Room {room} is not part of this connection.");
	}

	public bool Connects(RoomId first, RoomId second)
		=> (A.Room == first && B.Room == second) ||
		   (A.Room == second && B.Room == first);
}

/// Immutable dungeon topology produced by the generator.
public sealed class DungeonBlueprint
{
	private readonly Dictionary<RoomId, RoomDefinition> _roomsById;
	private readonly Dictionary<ConnectorId, ConnectorDefinition> _connectorsById;
	private readonly Dictionary<RoomId, RoomConnection[]> _connectionsByRoom;

	/// Builds lookup tables and validates topology invariants eagerly.
	public DungeonBlueprint(
		IEnumerable<RoomDefinition> rooms,
		IEnumerable<RoomConnection> connections,
		RoomId startRoom,
		ulong seed = 0)
	{
		Rooms = rooms.OrderBy(room => room.Id.Value).ToArray();
		Connections = connections.ToArray();
		StartRoom = startRoom;
		Seed = seed;

		_roomsById = Rooms.ToDictionary(room => room.Id);
		_connectorsById = Rooms
			.SelectMany(room => room.Connectors)
			.ToDictionary(connector => connector.Id);
		_connectionsByRoom = Rooms.ToDictionary(
			room => room.Id,
			room => Connections
				.Where(connection =>
					connection.A.Room == room.Id ||
					connection.B.Room == room.Id)
				.ToArray()
		);

		Validate();
	}

	public IReadOnlyList<RoomDefinition> Rooms { get; }
	public IReadOnlyList<RoomConnection> Connections { get; }
	public RoomId StartRoom { get; }
	public ulong Seed { get; }
	public int RoomCount => Rooms.Count;

	/// Returns the immutable definition for a room.
	public RoomDefinition GetRoom(RoomId id) => _roomsById[id];

	/// Returns a connector by its stable identifier.
	public ConnectorDefinition GetConnector(ConnectorId id) => _connectorsById[id];

	/// Returns all graph edges incident to a room.
	public IReadOnlyList<RoomConnection> ConnectionsFor(RoomId room)
		=> _connectionsByRoom[room];

	public bool HasConnectionBetween(RoomId first, RoomId second)
		=> _connectionsByRoom[first].Any(connection => connection.Connects(first, second));

	// Keep invalid graph data from reaching presentation, where failures are
	// much harder to distinguish from non-Euclidean projection artifacts.
	private void Validate()
	{
		if (!_roomsById.ContainsKey(StartRoom))
			throw new InvalidOperationException($"Start room {StartRoom} does not exist.");

		foreach (RoomDefinition room in Rooms)
		{
			if (room.Size.X < 3 || room.Size.Y < 3)
				throw new InvalidOperationException($"Room {room.Id} is too small.");

			foreach (ConnectorDefinition connector in room.Connectors)
			{
				if (connector.Id.Room != room.Id)
					throw new InvalidOperationException(
						$"Connector {connector.Id} is owned by the wrong room.");

				if (!IsPerimeterCell(room.Size, connector.Cell))
					throw new InvalidOperationException(
						$"Connector {connector.Id} is not on room {room.Id}'s perimeter.");

				if (!FacingMatchesPerimeter(room.Size, connector))
					throw new InvalidOperationException(
						$"Connector {connector.Id} does not face out of room {room.Id}.");
			}
		}

		var usedConnectors = new HashSet<ConnectorId>();
		foreach (RoomConnection connection in Connections)
		{
			if (connection.A.Room == connection.B.Room)
				throw new InvalidOperationException(
					$"Self-connection is not allowed: {connection.A} <-> {connection.B}.");

			if (!_connectorsById.ContainsKey(connection.A) ||
				!_connectorsById.ContainsKey(connection.B))
			{
				throw new InvalidOperationException(
					"Connection references an unknown connector.");
			}

			if (!usedConnectors.Add(connection.A) ||
				!usedConnectors.Add(connection.B))
			{
				throw new InvalidOperationException(
					"A connector may only participate in one connection.");
			}
		}

		foreach (ConnectorId connectorId in _connectorsById.Keys)
		{
			if (!usedConnectors.Contains(connectorId))
			{
				throw new InvalidOperationException(
					$"Connector {connectorId} has no graph edge.");
			}
		}

		if (usedConnectors.Count != _connectorsById.Count)
		{
			throw new InvalidOperationException(
				"Blueprint connector and graph-edge endpoint counts diverged.");
		}

		ValidateReachability();
	}

	private void ValidateReachability()
	{
		var visited = new HashSet<RoomId> { StartRoom };
		var pending = new Queue<RoomId>();
		pending.Enqueue(StartRoom);

		while (pending.Count > 0)
		{
			RoomId room = pending.Dequeue();
			foreach (RoomConnection connection in _connectionsByRoom[room])
			{
				RoomId neighbor = connection.OtherRoom(room);
				if (visited.Add(neighbor))
					pending.Enqueue(neighbor);
			}
		}

		if (visited.Count != Rooms.Count)
		{
			throw new InvalidOperationException(
				$"Dungeon graph is disconnected: reached {visited.Count} of {Rooms.Count} rooms.");
		}
	}

	private static bool IsPerimeterCell(Vector2I size, Vector2I cell)
		=> cell.X == 0 ||
		   cell.Y == 0 ||
		   cell.X == size.X - 1 ||
		   cell.Y == size.Y - 1;

	private static bool FacingMatchesPerimeter(
		Vector2I size,
		ConnectorDefinition connector)
	{
		return connector.Facing switch
		{
			GridDirection.North => connector.Cell.Y == 0,
			GridDirection.East => connector.Cell.X == size.X - 1,
			GridDirection.South => connector.Cell.Y == size.Y - 1,
			GridDirection.West => connector.Cell.X == 0,
			_ => false
		};
	}
}
