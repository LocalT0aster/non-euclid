using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

/// Projects the active room graph neighborhood into the current 2D canvas chart.
public partial class RoomPresenter : Node2D
{
	private readonly Dictionary<ProjectionKey, RoomView> _views = new();

	private DungeonBlueprint _blueprint;
	private DungeonRuntimeState _runtime;
	private TileSet _tileSet;
	private Material _roomMaterial;
	private int _corridorLengthCells;

	public IReadOnlyCollection<RoomId> LoadedRooms
		=> _views.Keys
			.Select(key => key.Room)
			.Distinct()
			.ToArray();

	/// Binds immutable topology, mutable runtime state, and shared rendering assets.
	public void Initialize(
		DungeonBlueprint blueprint,
		DungeonRuntimeState runtime,
		TileSet tileSet,
		Material roomMaterial,
		int corridorLengthCells)
	{
		_blueprint = blueprint;
		_runtime = runtime;
		_tileSet = tileSet;
		_roomMaterial = roomMaterial;
		_corridorLengthCells = corridorLengthCells;
	}

	/// Returns the authoritative projection for a simulated room.
	public RoomView GetPrimaryView(RoomId roomId)
		=> _views[new ProjectionKey(roomId, null)];

	/// Returns the projection of a room as seen through a specific connection.
	public RoomView GetNeighborView(RoomId sourceRoom, RoomConnection connection)
	{
		RoomId neighbor = connection.OtherRoom(sourceRoom);
		ConnectorId incoming = connection.OtherConnector(sourceRoom);
		return _views[new ProjectionKey(neighbor, incoming)];
	}

	/// Finds any currently loaded projection of a logical room.
	public bool TryGetAnyView(RoomId roomId, out RoomView view)
	{
		foreach ((ProjectionKey key, RoomView candidate) in _views
			.OrderBy(pair => pair.Key.IncomingConnector.HasValue ? 1 : 0))
		{
			if (key.Room != roomId)
				continue;

			view = candidate;
			return true;
		}

		view = null;
		return false;
	}

	/// Presents one authoritative room and its one-hop neighbors.
	public void ShowNeighborhood(RoomId root, GridTransform rootTransform)
	{
		var rootKey = new ProjectionKey(root, null);
		var projections = new Dictionary<ProjectionKey, GridTransform>
		{
			[rootKey] = rootTransform
		};

		AddNeighborProjections(
			root,
			rootTransform,
			excludedNeighbor: null,
			projections
		);

		ApplyProjectionSet(projections);

		var openConnectors = NewOpenConnectorMap();
		var suppressedCells = NewSuppressedCellMap();

		// The authoritative room shows all of its real graph-backed exits.
		GetOrCreateOpenConnectors(openConnectors, rootKey)
			.UnionWith(
				_blueprint.GetRoom(root).Connectors.Select(connector => connector.Id)
			);

		HashSet<Vector2I> rootWorldFootprint =
			BuildWorldFootprint(root, rootTransform);

		foreach (RoomConnection connection in _blueprint.ConnectionsFor(root))
		{
			RoomId neighbor = connection.OtherRoom(root);
			ConnectorId incoming = connection.OtherConnector(root);
			var neighborKey = new ProjectionKey(neighbor, incoming);

			// Adjacent rooms expose only the connector through which this local
			// chart sees them. Their further exits stay sealed until that room
			// becomes authoritative.
			GetOrCreateOpenConnectors(openConnectors, neighborKey)
				.Add(incoming);

			// A background projection may geometrically overlap another doorway
			// or even part of the active room. The active room owns its whole
			// footprint, so clip every overlapping background tile.
			SuppressAgainstWorldFootprint(
				neighbor,
				_views[neighborKey].Projection,
				rootWorldFootprint,
				GetOrCreateSuppressedCells(suppressedCells, neighborKey),
				perimeterOnly: false
			);
		}

		ApplyVisualStates(openConnectors, suppressedCells);
		ApplySimulationSet(new HashSet<RoomId> { root });
	}

	/// Presents both doorway endpoints as authoritative until the transition resolves.
	public void ShowDoorwayNeighborhood(
		RoomId from,
		RoomId to,
		GridTransform fromTransform,
		GridTransform toTransform)
	{
		var fromKey = new ProjectionKey(from, null);
		var toKey = new ProjectionKey(to, null);

		var projections = new Dictionary<ProjectionKey, GridTransform>
		{
			[fromKey] = fromTransform,
			[toKey] = toTransform
		};

		AddNeighborProjections(
			from,
			fromTransform,
			excludedNeighbor: to,
			projections
		);
		AddNeighborProjections(
			to,
			toTransform,
			excludedNeighbor: from,
			projections
		);

		ApplyProjectionSet(projections);

		var openConnectors = NewOpenConnectorMap();
		var suppressedCells = NewSuppressedCellMap();

		// Both endpoint rooms are simulated and expose all their exits.
		GetOrCreateOpenConnectors(openConnectors, fromKey)
			.UnionWith(
				_blueprint.GetRoom(from).Connectors.Select(connector => connector.Id)
			);
		GetOrCreateOpenConnectors(openConnectors, toKey)
			.UnionWith(
				_blueprint.GetRoom(to).Connectors.Select(connector => connector.Id)
			);

		HashSet<Vector2I> fromWorldFootprint =
			BuildWorldFootprint(from, fromTransform);
		HashSet<Vector2I> toWorldFootprint =
			BuildWorldFootprint(to, toTransform);

		// While the player is inside the doorway, the source remains the visual
		// owner of the source/target seam. Target collision is still enabled on
		// all non-overlapping cells; the suppressed seam is backed by the source
		// room's own TileMap collision.
		SuppressAgainstWorldFootprint(
			to,
			toTransform,
			fromWorldFootprint,
			GetOrCreateSuppressedCells(suppressedCells, toKey),
			perimeterOnly: true
		);

		ConfigureDoorwayBackgrounds(
			from,
			to,
			fromWorldFootprint,
			toWorldFootprint,
			openConnectors,
			suppressedCells
		);

		ConfigureDoorwayBackgrounds(
			to,
			from,
			fromWorldFootprint,
			toWorldFootprint,
			openConnectors,
			suppressedCells
		);

		ApplyVisualStates(openConnectors, suppressedCells);
		ApplySimulationSet(new HashSet<RoomId> { from, to });
	}

	/// Keeps authoritative rooms in front and orders background projections by connector proximity.
	public void UpdateZOrdering(
		Vector2 playerGlobalPosition,
		IReadOnlyCollection<RoomId> foregroundRooms)
	{
		var foreground = foregroundRooms.ToHashSet();

		foreach ((ProjectionKey key, RoomView view) in _views)
		{
			if (key.IncomingConnector is null && foreground.Contains(key.Room))
				view.ZIndex = 0;
		}

		var background = _views
			.Where(pair => pair.Key.IncomingConnector is not null)
			.Select(pair => new
			{
				Key = pair.Key,
				View = pair.Value,
				Distance = DistanceToIncomingConnector(
					pair.Key,
					playerGlobalPosition
				)
			})
			.OrderBy(item => item.Distance)
			.ThenBy(item => item.Key.Room.Value)
			.ThenBy(item => item.Key.IncomingConnector!.Value.Index)
			.ToArray();

		for (int i = 0; i < background.Length; i++)
			background[i].View.ZIndex = -(i + 1);
	}

	// Background projections are clipped against both endpoint charts so they
	// cannot draw over either authoritative room while the player straddles a door.
	private void ConfigureDoorwayBackgrounds(
		RoomId endpoint,
		RoomId otherEndpoint,
		IReadOnlySet<Vector2I> fromWorldFootprint,
		IReadOnlySet<Vector2I> toWorldFootprint,
		Dictionary<ProjectionKey, HashSet<ConnectorId>> openConnectors,
		Dictionary<ProjectionKey, HashSet<Vector2I>> suppressedCells)
	{
		foreach (RoomConnection connection in _blueprint.ConnectionsFor(endpoint))
		{
			RoomId neighbor = connection.OtherRoom(endpoint);
			if (neighbor == otherEndpoint)
				continue;

			ConnectorId incoming = connection.OtherConnector(endpoint);
			var neighborKey = new ProjectionKey(neighbor, incoming);

			if (!_views.TryGetValue(neighborKey, out RoomView neighborView))
				continue;

			GetOrCreateOpenConnectors(openConnectors, neighborKey)
				.Add(incoming);

			HashSet<Vector2I> suppressed =
				GetOrCreateSuppressedCells(suppressedCells, neighborKey);

			SuppressAgainstWorldFootprint(
				neighbor,
				neighborView.Projection,
				fromWorldFootprint,
				suppressed,
				perimeterOnly: false
			);
			SuppressAgainstWorldFootprint(
				neighbor,
				neighborView.Projection,
				toWorldFootprint,
				suppressed,
				perimeterOnly: false
			);
		}
	}

	private void AddNeighborProjections(
		RoomId root,
		GridTransform rootTransform,
		RoomId? excludedNeighbor,
		Dictionary<ProjectionKey, GridTransform> projections)
	{
		foreach (RoomConnection connection in _blueprint.ConnectionsFor(root))
		{
			RoomId neighbor = connection.OtherRoom(root);
			if (excludedNeighbor.HasValue && neighbor == excludedNeighbor.Value)
				continue;

			ConnectorDefinition sourceConnector =
				_blueprint.GetConnector(connection.ConnectorFor(root));
			ConnectorDefinition targetConnector =
				_blueprint.GetConnector(connection.OtherConnector(root));

			GridTransform targetTransform = GridTransform.AlignConnectedRoom(
				rootTransform,
				sourceConnector,
				targetConnector,
				_corridorLengthCells
			);

			var key = new ProjectionKey(
				neighbor,
				targetConnector.Id
			);

			projections[key] = targetTransform;
		}
	}

	private HashSet<Vector2I> BuildWorldFootprint(
		RoomId roomId,
		GridTransform transform)
	{
		RoomDefinition room = _blueprint.GetRoom(roomId);
		var cells = new HashSet<Vector2I>();

		foreach (Vector2I localCell in EnumerateRoomCells(room.Size))
			cells.Add(transform.ApplyCell(localCell));

		return cells;
	}

	// Converts chart-space overlap back into room-local cells that should not render.
	private void SuppressAgainstWorldFootprint(
		RoomId roomId,
		GridTransform transform,
		IReadOnlySet<Vector2I> worldFootprint,
		HashSet<Vector2I> suppressed,
		bool perimeterOnly)
	{
		RoomDefinition room = _blueprint.GetRoom(roomId);
		IEnumerable<Vector2I> localCells = perimeterOnly
			? EnumeratePerimeterCells(room.Size)
			: EnumerateRoomCells(room.Size);

		foreach (Vector2I localCell in localCells)
		{
			if (worldFootprint.Contains(transform.ApplyCell(localCell)))
				suppressed.Add(localCell);
		}
	}

	private static IEnumerable<Vector2I> EnumerateRoomCells(Vector2I size)
	{
		for (int y = 0; y < size.Y; y++)
		{
			for (int x = 0; x < size.X; x++)
				yield return new Vector2I(x, y);
		}
	}

	private static IEnumerable<Vector2I> EnumeratePerimeterCells(Vector2I size)
	{
		for (int x = 0; x < size.X; x++)
		{
			yield return new Vector2I(x, 0);

			if (size.Y > 1)
				yield return new Vector2I(x, size.Y - 1);
		}

		for (int y = 1; y < size.Y - 1; y++)
		{
			yield return new Vector2I(0, y);

			if (size.X > 1)
				yield return new Vector2I(size.X - 1, y);
		}
	}

	// Reuse projections when possible so crossing a connector does not visibly
	// destroy and recreate the room that was already on screen.
	private void ApplyProjectionSet(
		IReadOnlyDictionary<ProjectionKey, GridTransform> projections)
	{
		var next = new Dictionary<ProjectionKey, RoomView>();
		var spareViews = new Dictionary<ProjectionKey, RoomView>();

		foreach ((ProjectionKey key, RoomView view) in _views)
		{
			if (!projections.ContainsKey(key))
				spareViews.Add(key, view);
		}

		foreach ((ProjectionKey key, GridTransform projection) in projections)
		{
			if (!_views.TryGetValue(key, out RoomView view))
				continue;

			view.SetProjection(projection);
			next.Add(key, view);
		}

		foreach ((ProjectionKey key, GridTransform projection) in projections)
		{
			if (next.ContainsKey(key))
				continue;

			ProjectionKey? reusableKey = spareViews.Keys
				.Where(candidate => candidate.Room == key.Room)
				.OrderBy(candidate => candidate.IncomingConnector.HasValue ? 1 : 0)
				.ThenBy(candidate => candidate.IncomingConnector?.Index ?? -1)
				.Select(candidate => (ProjectionKey?)candidate)
				.FirstOrDefault();

			RoomView view;
			if (reusableKey.HasValue)
			{
				view = spareViews[reusableKey.Value];
				spareViews.Remove(reusableKey.Value);
			}
			else
			{
				view = CreateView(key.Room);
			}

			view.SetProjection(projection);
			next.Add(key, view);
		}

		foreach (RoomView unusedView in spareViews.Values)
			unusedView.QueueFree();

		_views.Clear();
		foreach ((ProjectionKey key, RoomView view) in next)
			_views.Add(key, view);
	}

	private RoomView CreateView(RoomId roomId)
	{
		var view = new RoomView();
		AddChild(view);
		view.Initialize(
			_blueprint.GetRoom(roomId),
			_tileSet,
			_roomMaterial,
			_corridorLengthCells
		);
		return view;
	}

	private void ApplyVisualStates(
		IReadOnlyDictionary<ProjectionKey, HashSet<ConnectorId>>
			openConnectorsByProjection,
		IReadOnlyDictionary<ProjectionKey, HashSet<Vector2I>>
			suppressedByProjection)
	{
		foreach ((ProjectionKey key, RoomView view) in _views)
		{
			IReadOnlySet<ConnectorId> openConnectors =
				openConnectorsByProjection.TryGetValue(
					key,
					out HashSet<ConnectorId> foundOpen
				)
					? foundOpen
					: new HashSet<ConnectorId>();

			IReadOnlySet<Vector2I> suppressed =
				suppressedByProjection.TryGetValue(
					key,
					out HashSet<Vector2I> foundSuppressed
				)
					? foundSuppressed
					: new HashSet<Vector2I>();

			view.SetVisualState(openConnectors, suppressed);
		}
	}

	private static Dictionary<ProjectionKey, HashSet<ConnectorId>>
		NewOpenConnectorMap()
		=> new();

	private static Dictionary<ProjectionKey, HashSet<Vector2I>>
		NewSuppressedCellMap()
		=> new();

	private static HashSet<ConnectorId> GetOrCreateOpenConnectors(
		Dictionary<ProjectionKey, HashSet<ConnectorId>> map,
		ProjectionKey key)
	{
		if (!map.TryGetValue(key, out HashSet<ConnectorId> connectors))
		{
			connectors = new HashSet<ConnectorId>();
			map.Add(key, connectors);
		}

		return connectors;
	}

	private static HashSet<Vector2I> GetOrCreateSuppressedCells(
		Dictionary<ProjectionKey, HashSet<Vector2I>> map,
		ProjectionKey key)
	{
		if (!map.TryGetValue(key, out HashSet<Vector2I> cells))
		{
			cells = new HashSet<Vector2I>();
			map.Add(key, cells);
		}

		return cells;
	}

	private void ApplySimulationSet(IReadOnlyCollection<RoomId> simulatedRooms)
	{
		var simulated = simulatedRooms.ToHashSet();

		foreach ((ProjectionKey key, RoomView view) in _views)
		{
			bool isAuthoritative =
				key.IncomingConnector is null &&
				simulated.Contains(key.Room);

			view.SetSimulationEnabled(isAuthoritative);
		}

		_runtime.ApplyPresentation(
			_views.Keys.Select(key => key.Room).Distinct(),
			simulated
		);
	}

	private float DistanceToIncomingConnector(
		ProjectionKey key,
		Vector2 playerGlobalPosition)
	{
		if (!key.IncomingConnector.HasValue)
			return 0.0f;

		ConnectorId incoming = key.IncomingConnector.Value;

		RoomConnection connection = _blueprint
			.ConnectionsFor(key.Room)
			.Single(candidate =>
				candidate.ConnectorFor(key.Room) == incoming
			);

		RoomId anchorRoom = connection.OtherRoom(key.Room);
		var anchorKey = new ProjectionKey(anchorRoom, null);

		if (!_views.TryGetValue(anchorKey, out RoomView anchorView))
			return float.PositiveInfinity;

		ConnectorDefinition anchorConnector = _blueprint.GetConnector(
			connection.ConnectorFor(anchorRoom)
		);

		Vector2 connectorPosition =
			anchorView.CellCenterToGlobal(anchorConnector.Cell);

		return playerGlobalPosition.DistanceSquaredTo(connectorPosition);
	}

	// Background projections are keyed by incoming connector because the same
	// logical room can appear at different transforms through different edges.
	private readonly record struct ProjectionKey(
		RoomId Room,
		ConnectorId? IncomingConnector
	);
}
