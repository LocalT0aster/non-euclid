using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// Deterministic graph generator that never touches the Godot scene tree.
public sealed class DungeonGenerator
{
	private const ulong LayoutStreamTag = 0x4C41594F5554UL;
	private const ulong ContentStreamTag = 0x434F4E54454E54UL;

	private readonly GenerationConfig _config;

	/// Creates a generator for one validated configuration.
	public DungeonGenerator(GenerationConfig config)
	{
		_config = config;
		_config.Validate();
	}

	/// Generates an immutable room graph from the supplied seed.
	public DungeonBlueprint Generate(ulong seed)
	{
		var rooms = new List<MutableRoom>();
		var connections = new List<RoomConnection>();
		var diggers = new Queue<DiggerState>();
		int nextDiggerId = 1;

		MutableRoom start = CreateRoom(
			new RoomId(0),
			seed,
			forceAtLeastTwoConnectors: true
		);
		rooms.Add(start);
		diggers.Enqueue(new DiggerState(0, start.Id, 0));

		// Diggers walk graph connectors rather than global map coordinates.
		while (diggers.Count > 0)
		{
			DiggerState digger = diggers.Dequeue();
			MutableRoom current = rooms[digger.CurrentRoom.Value];

			var rng = new DeterministicRandom(
				SeedMixer.Mix(
					seed,
					LayoutStreamTag,
					(ulong)digger.Id,
					(ulong)digger.Step
				)
			);

			ConnectorDefinition? sourceConnector = PickFreeConnector(current, rng);
			if (sourceConnector is null)
				continue;

			MutableRoom target;
			ConnectorDefinition? targetConnector;

			bool canCreateRoom = rooms.Count < _config.MaximumRooms;
			// Loop attempts reuse a free connector on an existing non-adjacent room.
			bool tryLoop = rng.Chance(_config.LoopProbability);

			if (tryLoop)
			{
				(target, targetConnector) = PickLoopTarget(
					current,
					rooms,
					connections,
					rng
				);

				if (targetConnector is null && canCreateRoom)
				{
					target = CreateAndAppendRoom(rooms, seed);
					targetConnector = PickFreeConnector(target, rng);
				}
			}
			else if (canCreateRoom)
			{
				target = CreateAndAppendRoom(rooms, seed);
				targetConnector = PickFreeConnector(target, rng);
			}
			else
			{
				continue;
			}

			if (targetConnector is null)
				continue;

			current.UsedConnectors.Add(sourceConnector.Id);
			target.UsedConnectors.Add(targetConnector.Id);
			connections.Add(new RoomConnection(sourceConnector.Id, targetConnector.Id));

			int remainingExits = target.FreeConnectorCount;
			if (remainingExits <= 0)
				continue;

			diggers.Enqueue(digger with
			{
				CurrentRoom = target.Id,
				Step = digger.Step + 1
			});

			// A branch is another digger stream starting from the same target room.
			if (remainingExits >= 2 && rng.Chance(_config.BranchProbability))
			{
				diggers.Enqueue(new DiggerState(
					nextDiggerId++,
					target.Id,
					0
				));
			}
		}

		var connectedConnectorIds = connections
			.SelectMany(connection => new[] { connection.A, connection.B })
			.ToHashSet();

		var definitions = rooms
			.Select(room => room.ToDefinition(connectedConnectorIds))
			.ToArray();

		return new DungeonBlueprint(
			definitions,
			connections,
			start.Id,
			seed
		);
	}

	private MutableRoom CreateAndAppendRoom(List<MutableRoom> rooms, ulong seed)
	{
		var id = new RoomId(rooms.Count);
		MutableRoom room = CreateRoom(id, seed, forceAtLeastTwoConnectors: false);
		rooms.Add(room);
		return room;
	}

	// Room candidates are deterministic per RoomId, independent of traversal order.
	private MutableRoom CreateRoom(
		RoomId id,
		ulong worldSeed,
		bool forceAtLeastTwoConnectors)
	{
		var rng = new DeterministicRandom(
			SeedMixer.Mix(worldSeed, LayoutStreamTag, (ulong)id.Value)
		);

		int width = rng.NextInt(
			_config.MinRoomWidth,
			_config.MaxRoomWidth + 1
		);
		int height = rng.NextInt(
			_config.MinRoomHeight,
			_config.MaxRoomHeight + 1
		);

		var size = new Vector2I(width, height);
		int minimumConnectors = forceAtLeastTwoConnectors
			? Math.Max(2, _config.MinConnectorsPerRoom)
			: _config.MinConnectorsPerRoom;

		int desiredConnectorCount = rng.NextInt(
			minimumConnectors,
			_config.MaxConnectorsPerRoom + 1
		);

		ConnectorCandidate[] candidates = EnumerateConnectorCandidates(size).ToArray();
		rng.Shuffle(candidates.AsSpan());

		var selected = new List<ConnectorCandidate>(desiredConnectorCount);
		int perimeterLength = 2 * width + 2 * height - 4;

		foreach (ConnectorCandidate candidate in candidates)
		{
			bool spaced = selected.All(existing =>
				CircularDistance(
					existing.PerimeterIndex,
					candidate.PerimeterIndex,
					perimeterLength
				) > _config.MinimumConnectorSpacing
			);

			if (!spaced)
				continue;

			selected.Add(candidate);
			if (selected.Count == desiredConnectorCount)
				break;
		}

		if (selected.Count < minimumConnectors)
		{
			throw new InvalidOperationException(
				$"Room {id} could only place {selected.Count} connectors " +
				$"with spacing {_config.MinimumConnectorSpacing}."
			);
		}

		// Connector indices are stable and independent of the temporary shuffle.
		selected.Sort((a, b) => a.PerimeterIndex.CompareTo(b.PerimeterIndex));

		var connectors = selected
			.Select((candidate, index) => new ConnectorDefinition(
				new ConnectorId(id, index),
				candidate.Cell,
				candidate.Facing
			))
			.ToArray();

		ulong contentSeed = SeedMixer.Mix(
			worldSeed,
			ContentStreamTag,
			(ulong)id.Value
		);

		return new MutableRoom(
			id,
			$"R{id.Value:000}",
			size,
			connectors,
			contentSeed
		);
	}

	private static ConnectorDefinition? PickFreeConnector(
		MutableRoom room,
		DeterministicRandom rng)
	{
		ConnectorDefinition[] free = room.Connectors
			.Where(connector => !room.UsedConnectors.Contains(connector.Id))
			.ToArray();

		if (free.Length == 0)
			return null;

		return free[rng.NextInt(free.Length)];
	}

	// Excludes self-links and duplicate room-to-room edges; each connector is single-use.
	private static (MutableRoom Room, ConnectorDefinition? Connector) PickLoopTarget(
		MutableRoom source,
		IReadOnlyList<MutableRoom> rooms,
		IReadOnlyList<RoomConnection> connections,
		DeterministicRandom rng)
	{
		var candidates = new List<(MutableRoom Room, ConnectorDefinition Connector)>();

		foreach (MutableRoom room in rooms.OrderBy(room => room.Id.Value))
		{
			if (room.Id == source.Id)
				continue;

			if (connections.Any(connection => connection.Connects(source.Id, room.Id)))
				continue;

			foreach (ConnectorDefinition connector in room.Connectors)
			{
				if (!room.UsedConnectors.Contains(connector.Id))
					candidates.Add((room, connector));
			}
		}

		if (candidates.Count == 0)
			return (source, null);

		return candidates[rng.NextInt(candidates.Count)];
	}

	private static IEnumerable<ConnectorCandidate> EnumerateConnectorCandidates(
		Vector2I size)
	{
		int width = size.X;
		int height = size.Y;

		for (int x = 1; x < width - 1; x++)
		{
			yield return new ConnectorCandidate(
				new Vector2I(x, 0),
				GridDirection.North,
				x
			);
		}

		for (int y = 1; y < height - 1; y++)
		{
			yield return new ConnectorCandidate(
				new Vector2I(width - 1, y),
				GridDirection.East,
				(width - 1) + y
			);
		}

		for (int x = width - 2; x >= 1; x--)
		{
			yield return new ConnectorCandidate(
				new Vector2I(x, height - 1),
				GridDirection.South,
				(width - 1) +
				(height - 1) +
				(width - 1 - x)
			);
		}

		for (int y = height - 2; y >= 1; y--)
		{
			yield return new ConnectorCandidate(
				new Vector2I(0, y),
				GridDirection.West,
				(width - 1) +
				(height - 1) +
				(width - 1) +
				(height - 1 - y)
			);
		}
	}

	private static int CircularDistance(int a, int b, int length)
	{
		int direct = Math.Abs(a - b);
		return Math.Min(direct, length - direct);
	}

	private sealed class MutableRoom
	{
		public MutableRoom(
			RoomId id,
			string name,
			Vector2I size,
			IReadOnlyList<ConnectorDefinition> connectors,
			ulong contentSeed)
		{
			Id = id;
			Name = name;
			Size = size;
			Connectors = connectors;
			ContentSeed = contentSeed;
		}

		public RoomId Id { get; }
		public string Name { get; }
		public Vector2I Size { get; }
		public IReadOnlyList<ConnectorDefinition> Connectors { get; }
		public ulong ContentSeed { get; }
		public HashSet<ConnectorId> UsedConnectors { get; } = new();

		public int FreeConnectorCount
			=> Connectors.Count - UsedConnectors.Count;

		public RoomDefinition ToDefinition(
			IReadOnlySet<ConnectorId> connectedConnectorIds)
		{
			ConnectorDefinition[] connected = Connectors
				.Where(connector =>
					connectedConnectorIds.Contains(connector.Id))
				.ToArray();

			return new RoomDefinition(
				Id,
				Name,
				Size,
				connected,
				ContentSeed
			);
		}
	}

	private readonly record struct ConnectorCandidate(
		Vector2I Cell,
		GridDirection Facing,
		int PerimeterIndex
	);

	private sealed record DiggerState(
		int Id,
		RoomId CurrentRoom,
		int Step
	);
}

/// Produces a stable fingerprint for regression-testing generated topology.
public static class DungeonBlueprintFingerprint
{
	/// Hashes topology-relevant blueprint data in deterministic order.
	public static ulong Compute(DungeonBlueprint blueprint)
	{
		const ulong offset = 14695981039346656037UL;
		const ulong prime = 1099511628211UL;
		ulong hash = offset;

		void Add(ulong value)
		{
			for (int i = 0; i < 8; i++)
			{
				hash ^= (byte)(value & 0xFF);
				hash = unchecked(hash * prime);
				value >>= 8;
			}
		}

		Add(blueprint.Seed);
		Add((ulong)blueprint.StartRoom.Value);
		Add((ulong)blueprint.Rooms.Count);

		foreach (RoomDefinition room in blueprint.Rooms)
		{
			Add((ulong)room.Id.Value);
			Add((ulong)room.Size.X);
			Add((ulong)room.Size.Y);
			Add(room.ContentSeed);
			Add((ulong)room.Connectors.Count);

			foreach (ConnectorDefinition connector in room.Connectors)
			{
				Add((ulong)connector.Id.Index);
				Add((ulong)connector.Cell.X);
				Add((ulong)connector.Cell.Y);
				Add((ulong)connector.Facing);
			}
		}

		Add((ulong)blueprint.Connections.Count);
		foreach (RoomConnection connection in blueprint.Connections)
		{
			Add((ulong)connection.A.Room.Value);
			Add((ulong)connection.A.Index);
			Add((ulong)connection.B.Room.Value);
			Add((ulong)connection.B.Index);
		}

		return hash;
	}
}
