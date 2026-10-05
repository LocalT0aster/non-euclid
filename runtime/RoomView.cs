using Godot;
using System.Collections.Generic;

/// Disposable visual/physics projection of one logical room.
public partial class RoomView : Node2D
{
	public const int TileSize = 32;

	private const int SourceId = 0;
	private const int TerrainSet = 0;
	private const int WallTerrain = 0;
	private static readonly Vector2I FloorAtlasCoords = new(10, 0);

	private int _corridorLengthCells;
	private IReadOnlySet<ConnectorId> _openConnectors =
		new HashSet<ConnectorId>();
	private IReadOnlySet<Vector2I> _suppressedCells =
		new HashSet<Vector2I>();
	private Rid _navigationMap;
	private NavigationRegion2D _navigationRegion;
	private bool _navigationMapCreated;

	public RoomDefinition Definition { get; private set; }
	public GridTransform Projection { get; private set; }
	public TileMapLayer Tiles { get; private set; }
	public Node2D Contents { get; private set; }
	public bool IsSimulated { get; private set; }

	/// Isolated navigation map for actors that belong to this room projection.
	public Rid NavigationMap => _navigationMap;

	/// Walkable room interior in RoomView-local pixel coordinates.
	public Rect2 InteriorRectLocal { get; private set; }

	/// Creates the room TileMap and its room-local content container.
	public void Initialize(
		RoomDefinition definition,
		TileSet tileSet,
		Material material,
		int corridorLengthCells)
	{
		Definition = definition;
		_corridorLengthCells = corridorLengthCells;
		Name = $"Room_{definition.Name}";

		Tiles = new TileMapLayer
		{
			Name = "Tiles",
			TileSet = tileSet,
			Material = material,
			LightMask = 1,
			CollisionEnabled = false,
			OcclusionEnabled = true
		};

		Contents = new Node2D
		{
			Name = "Contents",
			ProcessMode = Node.ProcessModeEnum.Disabled
		};

		AddChild(Tiles);
		AddChild(Contents);

		BuildNavigation();
		RebuildTiles();
	}

	/// Applies a chart-local grid transform without changing the room definition.
	public void SetProjection(GridTransform projection)
	{
		Projection = projection.Normalized;
		Rotation = Projection.CanvasRotation;

		Vector2 localCellZeroCenter = Tiles.MapToLocal(Vector2I.Zero);
		Vector2 desiredWorldCellZeroCenter =
			localCellZeroCenter +
			new Vector2(
				Projection.Translation.X * TileSize,
				Projection.Translation.Y * TileSize
			);

		Position =
			desiredWorldCellZeroCenter -
			localCellZeroCenter.Rotated(Rotation);
	}

	/// Rebuilds tiles for the connectors and cells visible in the current chart.
	public void SetVisualState(
		IReadOnlySet<ConnectorId> openConnectors,
		IReadOnlySet<Vector2I> suppressedCells)
	{
		_openConnectors = openConnectors;
		_suppressedCells = suppressedCells;
		RebuildTiles();
	}

	/// Returns the global center of a room-local tile cell.
	public Vector2 CellCenterToGlobal(Vector2I cell)
		=> ToGlobal(Tiles.MapToLocal(cell));

	/// Converts a room-local cardinal direction to the current canvas orientation.
	public Vector2 DirectionToGlobal(GridDirection direction)
	{
		Vector2I localDirection = direction.ToVector();
		return new Vector2(localDirection.X, localDirection.Y).Rotated(Rotation);
	}

	/// Enables collision, navigation, and room-local processing for authoritative views.
	public void SetSimulationEnabled(bool enabled)
	{
		IsSimulated = enabled;
		Tiles.CollisionEnabled = enabled;

		Contents.ProcessMode = enabled
			? Node.ProcessModeEnum.Inherit
			: Node.ProcessModeEnum.Disabled;

		foreach (Node child in Contents.GetChildren())
		{
			if (child is IRoomSimulationParticipant participant)
				participant.SetRoomSimulationEnabled(enabled);
		}

		if (_navigationMapCreated)
			NavigationServer2D.MapSetActive(_navigationMap, enabled);
	}

	/// Returns true while a global point is inside this room, excluding its wall ring.
	public bool ContainsInteriorGlobalPoint(Vector2 globalPoint, float margin = 0.0f)
		=> GetInteriorRect(margin).HasPoint(ToLocal(globalPoint));

	/// Clamps a global point to this room's walkable interior.
	public Vector2 ClampGlobalPointToInterior(Vector2 globalPoint, float margin = 0.0f)
	{
		Rect2 interior = GetInteriorRect(margin);
		Vector2 local = ToLocal(globalPoint);
		Vector2 minimum = interior.Position;
		Vector2 maximum = interior.End;

		local.X = Mathf.Clamp(local.X, minimum.X, maximum.X);
		local.Y = Mathf.Clamp(local.Y, minimum.Y, maximum.Y);
		return ToGlobal(local);
	}

	/// Converts normalized interior coordinates into a global navigation target.
	public Vector2 InteriorPointToGlobal(
		float normalizedX,
		float normalizedY,
		float margin = 0.0f)
	{
		Rect2 interior = GetInteriorRect(margin);
		return ToGlobal(
			interior.Position +
			new Vector2(
				Mathf.Clamp(normalizedX, 0.0f, 1.0f) * interior.Size.X,
				Mathf.Clamp(normalizedY, 0.0f, 1.0f) * interior.Size.Y
			)
		);
	}

	public override void _ExitTree()
	{
		if (!_navigationMapCreated)
			return;

		NavigationServer2D.FreeRid(_navigationMap);
		_navigationMapCreated = false;
	}

	private void BuildNavigation()
	{
		// The navigation surface deliberately excludes the perimeter/doorway
		// cells. Enemies can see an open door but can never path through it.
		InteriorRectLocal = new Rect2(
			new Vector2(TileSize, TileSize),
			new Vector2(
				(Definition.Size.X - 2) * TileSize,
				(Definition.Size.Y - 2) * TileSize
			)
		);

		var polygon = new NavigationPolygon();
		Rect2 navRect = GetInteriorRect(4.0f);
		polygon.Vertices =
		[
			navRect.Position,
			new Vector2(navRect.End.X, navRect.Position.Y),
			navRect.End,
			new Vector2(navRect.Position.X, navRect.End.Y)
		];
		polygon.AddPolygon([0, 1, 2, 3]);

		_navigationMap = NavigationServer2D.MapCreate();
		_navigationMapCreated = true;
		NavigationServer2D.MapSetActive(_navigationMap, false);

		_navigationRegion = new NavigationRegion2D
		{
			Name = "NavigationRegion2D",
			NavigationPolygon = polygon
		};
		AddChild(_navigationRegion);
		_navigationRegion.SetNavigationMap(_navigationMap);
	}

	private Rect2 GetInteriorRect(float margin)
	{
		float clampedMargin = Mathf.Max(0.0f, margin);
		Vector2 shrink = Vector2.One * clampedMargin;
		Vector2 size = InteriorRectLocal.Size - shrink * 2.0f;

		if (size.X <= 0.0f || size.Y <= 0.0f)
			return InteriorRectLocal;

		return new Rect2(InteriorRectLocal.Position + shrink, size);
	}

	// Suppressed cells are omitted before terrain connection so background
	// projections cannot contribute tiles inside an authoritative room footprint.
	private void RebuildTiles()
	{
		Tiles.Clear();

		var wallCells = new HashSet<Vector2I>();

		for (int y = 0; y < Definition.Size.Y; y++)
		{
			for (int x = 0; x < Definition.Size.X; x++)
			{
				var cell = new Vector2I(x, y);

				if (_suppressedCells.Contains(cell))
					continue;

				bool perimeter =
					x == 0 ||
					y == 0 ||
					x == Definition.Size.X - 1 ||
					y == Definition.Size.Y - 1;

				if (perimeter)
					wallCells.Add(cell);
				else
					Tiles.SetCell(cell, SourceId, FloorAtlasCoords);
			}
		}

		foreach (ConnectorDefinition connector in Definition.Connectors)
		{
			if (!_openConnectors.Contains(connector.Id))
				continue;

			wallCells.Remove(connector.Cell);

			if (!_suppressedCells.Contains(connector.Cell))
			{
				Tiles.SetCell(
					connector.Cell,
					SourceId,
					FloorAtlasCoords
				);
			}

			AddCorridorTiles(connector, wallCells);
		}

		var terrainCells = new Godot.Collections.Array<Vector2I>();
		foreach (Vector2I cell in wallCells)
			terrainCells.Add(cell);

		Tiles.SetCellsTerrainConnect(
			terrainCells,
			TerrainSet,
			WallTerrain,
			true
		);
		Tiles.UpdateInternals();
	}

	private void AddCorridorTiles(
		ConnectorDefinition connector,
		HashSet<Vector2I> wallCells)
	{
		Vector2I facing = connector.Facing.ToVector();
		Vector2I perpendicular = new(-facing.Y, facing.X);

		for (int distance = 1; distance < _corridorLengthCells; distance++)
		{
			Vector2I corridorCell =
				connector.Cell +
				facing * distance;

			if (_suppressedCells.Contains(corridorCell))
				continue;

			Tiles.SetCell(corridorCell, SourceId, FloorAtlasCoords);

			Vector2I sideA = corridorCell + perpendicular;
			Vector2I sideB = corridorCell - perpendicular;

			if (!_suppressedCells.Contains(sideA))
				wallCells.Add(sideA);

			if (!_suppressedCells.Contains(sideB))
				wallCells.Add(sideB);
		}
	}
}
