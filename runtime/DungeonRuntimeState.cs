using System.Collections.Generic;
using System.Linq;

/// Mutable per-room state that survives RoomView creation and destruction.
public sealed class RoomRuntimeState
{
	public RoomRuntimeState(RoomDefinition room)
	{
		RoomId = room.Id;
		ContentSeed = room.ContentSeed;
	}

	public RoomId RoomId { get; }
	public ulong ContentSeed { get; }
	public bool Visited { get; internal set; }
	public bool Cleared { get; set; }
	public bool IsLoaded { get; internal set; }
	public bool IsVisible { get; internal set; }
	public bool IsSimulated { get; internal set; }
}

/// Mutable dungeon state independent of the generated blueprint and scene tree.
public sealed class DungeonRuntimeState
{
	private readonly Dictionary<RoomId, RoomRuntimeState> _rooms;

	public DungeonRuntimeState(DungeonBlueprint blueprint)
	{
		_rooms = blueprint.Rooms.ToDictionary(
			room => room.Id,
			room => new RoomRuntimeState(room)
		);

		ActiveRoom = blueprint.StartRoom;
		_rooms[ActiveRoom].Visited = true;
	}

	public RoomId ActiveRoom { get; private set; }
	public RoomId? DoorwayFrom { get; private set; }
	public RoomId? DoorwayTo { get; private set; }

	public bool IsInDoorway => DoorwayFrom.HasValue && DoorwayTo.HasValue;

	public RoomRuntimeState this[RoomId roomId] => _rooms[roomId];

	public IReadOnlyCollection<RoomRuntimeState> Rooms => _rooms.Values;

	/// Marks both endpoint rooms active while the player occupies a doorway.
	public void BeginDoorway(RoomId from, RoomId to)
	{
		DoorwayFrom = from;
		DoorwayTo = to;
	}

	/// Leaves doorway mode without changing the authoritative room.
	public void CancelDoorway()
	{
		DoorwayFrom = null;
		DoorwayTo = null;
	}

	/// Promotes the destination room after crossing the far doorway edge.
	public void CommitDoorway(RoomId destination)
	{
		ActiveRoom = destination;
		_rooms[destination].Visited = true;
		DoorwayFrom = null;
		DoorwayTo = null;
	}

	/// Mirrors the currently loaded and simulated presentation set into runtime state.
	public void ApplyPresentation(
		IEnumerable<RoomId> loadedRooms,
		IEnumerable<RoomId> simulatedRooms)
	{
		var loaded = loadedRooms.ToHashSet();
		var simulated = simulatedRooms.ToHashSet();

		foreach (RoomRuntimeState state in _rooms.Values)
		{
			state.IsLoaded = loaded.Contains(state.RoomId);
			state.IsVisible = state.IsLoaded;
			state.IsSimulated = simulated.Contains(state.RoomId);
		}
	}
}
