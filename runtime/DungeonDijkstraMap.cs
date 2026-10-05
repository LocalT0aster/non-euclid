using System;
using System.Collections.Generic;
using System.Linq;

/// Unit-weight Dijkstra distances across the logical room graph.
public sealed class DungeonDijkstraMap
{
	private readonly Dictionary<RoomId, int> _distanceByRoom;

	private DungeonDijkstraMap(
		RoomId origin,
		Dictionary<RoomId, int> distanceByRoom)
	{
		Origin = origin;
		_distanceByRoom = distanceByRoom;
	}

	public RoomId Origin { get; }

	public IReadOnlyDictionary<RoomId, int> Distances => _distanceByRoom;

	/// Returns the shortest number of room-to-room transitions to the origin.
	public int Distance(RoomId roomId) => _distanceByRoom[roomId];

	/// Builds shortest-path distances from every room to one graph origin.
	public static DungeonDijkstraMap Build(
		DungeonBlueprint blueprint,
		RoomId origin)
	{
		var distances = blueprint.Rooms.ToDictionary(
			room => room.Id,
			_ => int.MaxValue
		);
		var pending = new PriorityQueue<RoomId, int>();

		distances[origin] = 0;
		pending.Enqueue(origin, 0);

		while (pending.TryDequeue(out RoomId room, out int distance))
		{
			if (distance != distances[room])
				continue;

			foreach (RoomConnection connection in blueprint.ConnectionsFor(room))
			{
				RoomId neighbor = connection.OtherRoom(room);
				int candidate = distance + 1;

				if (candidate >= distances[neighbor])
					continue;

				distances[neighbor] = candidate;
				pending.Enqueue(neighbor, candidate);
			}
		}

		if (distances.Values.Any(distance => distance == int.MaxValue))
		{
			throw new InvalidOperationException(
				"Dijkstra map cannot cover a disconnected dungeon."
			);
		}

		return new DungeonDijkstraMap(origin, distances);
	}

	/// Returns the furthest room from origin, breaking ties by RoomId.
	public RoomId FurthestRoom()
		=> _distanceByRoom
			.OrderByDescending(pair => pair.Value)
			.ThenBy(pair => pair.Key.Value)
			.First()
			.Key;
}
