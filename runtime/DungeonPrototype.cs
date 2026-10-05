using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

/// Godot-facing controller that generates a dungeon and manages room transitions.
public partial class DungeonPrototype : Node2D
{
	private const float DoorwayHalfLengthPx = RoomView.TileSize * 0.5f;
	private const float DoorwayHalfWidthPx = RoomView.TileSize * 0.5f + 2.0f;
	private const float DoorCommitDistancePx = DoorwayHalfLengthPx;

	/// Zero selects a fresh seed each run; any non-zero value is reproducible.
	[Export] public long GenerationSeed { get; set; } = 0;
	[Export] public int MaximumRooms { get; set; } = 24;
	[Export] public int MinRoomWidth { get; set; } = 5;
	[Export] public int MaxRoomWidth { get; set; } = 14;
	[Export] public int MinRoomHeight { get; set; } = 5;
	[Export] public int MaxRoomHeight { get; set; } = 12;
	[Export] public int MinConnectorsPerRoom { get; set; } = 2;
	[Export] public int MaxConnectorsPerRoom { get; set; } = 4;
	[Export] public int MinimumConnectorSpacing { get; set; } = 1;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float BranchProbability { get; set; } = 0.35f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float LoopProbability { get; set; } = 0.20f;
	[Export] public bool LogProbabilitySweep { get; set; } = false;
	[Export(PropertyHint.Range, "1,64,1")]
	public int ProbabilitySweepSamples { get; set; } = 16;
	[Export] public NodePath AIDirectorPath { get; set; } =
		new("AIDirector");

	private PlayerController _player;
	private AIDirector _director;
	private TileMapLayer _template;
	private TileSet _tileSet;
	private Material _roomMaterial;

	private long _resolvedGenerationSeed;
	private GenerationConfig _generationConfig;
	private Task<DungeonBlueprint> _generationTask;
	private Task<IReadOnlyList<ProbabilitySweepRow>> _probabilitySweepTask;
	private bool _generationResultHandled;
	private bool _probabilitySweepResultHandled;

	private DungeonBlueprint _blueprint;
	private DungeonRuntimeState _runtime;
	private RoomPresenter _presenter;
	private TransitionState _transition;

	/// Starts deterministic generation on a worker thread and pauses player physics.
	public override void _Ready()
	{
		_player = GetNode<PlayerController>("Player");
		_director = GetNode<AIDirector>(AIDirectorPath);
		_template = GetNode<TileMapLayer>("RoomTemplate");

		_tileSet = _template.TileSet
			?? throw new InvalidOperationException("RoomTemplate requires a TileSet.");
		_roomMaterial = _template.Material
			?? throw new InvalidOperationException("RoomTemplate requires a material.");

		_template.Visible = false;
		_template.Enabled = false;
		_player.SetPhysicsProcess(false);

		_generationConfig = BuildGenerationConfig();
		_resolvedGenerationSeed = GenerationSeed == 0
			? Random.Shared.NextInt64(1, long.MaxValue)
			: GenerationSeed;
		ulong seed = unchecked((ulong)_resolvedGenerationSeed);

		GD.Print(
			$"[Dungeon] Generating seed={_resolvedGenerationSeed}, " +
			$"max_rooms={_generationConfig.MaximumRooms} on worker thread."
		);

		// Generation intentionally only creates plain C# data. No Godot Node,
		// Resource or scene-tree mutation happens on the worker thread.
		_generationTask = Task.Run(
			() => new DungeonGenerator(_generationConfig).Generate(seed)
		);

		if (LogProbabilitySweep)
		{
			double[] branchAxis = BuildProbabilityAxis(
				_generationConfig.BranchProbability
			);
			double[] loopAxis = BuildProbabilityAxis(
				_generationConfig.LoopProbability
			);

			_probabilitySweepTask = Task.Run(
				() => DungeonProbabilityAnalyzer.Sweep(
					_generationConfig,
					seed,
					branchAxis,
					loopAxis,
					ProbabilitySweepSamples
				)
			);
		}
	}

	/// Polls generation, doorway transitions, and presentation depth ordering.
	public override void _Process(double delta)
	{
		if (_blueprint is null)
		{
			TryFinishGeneration();
			TryFinishProbabilitySweep();
			return;
		}

		TryFinishProbabilitySweep();

		if (_transition is null)
			TryBeginTransition();
		else
			UpdateTransition();

		RoomId[] foreground = _runtime.IsInDoorway
			? new[] { _runtime.DoorwayFrom!.Value, _runtime.DoorwayTo!.Value }
			: new[] { _runtime.ActiveRoom };

		_presenter.UpdateZOrdering(_player.GlobalPosition, foreground);
	}

	// Copy exported values before Task.Run so the worker reads plain immutable data.
	private GenerationConfig BuildGenerationConfig()
	{
		var config = new GenerationConfig
		{
			MinRoomWidth = MinRoomWidth,
			MaxRoomWidth = MaxRoomWidth,
			MinRoomHeight = MinRoomHeight,
			MaxRoomHeight = MaxRoomHeight,
			MinConnectorsPerRoom = MinConnectorsPerRoom,
			MaxConnectorsPerRoom = MaxConnectorsPerRoom,
			MinimumConnectorSpacing = MinimumConnectorSpacing,
			MaximumRooms = MaximumRooms,
			CorridorLengthCells = 1,
			BranchProbability = BranchProbability,
			LoopProbability = LoopProbability
		};

		config.Validate();
		return config;
	}

	// Scene-tree objects are created only after the worker returns its plain C# blueprint.
	private void TryFinishGeneration()
	{
		if (_generationResultHandled || _generationTask is null)
			return;

		if (!_generationTask.IsCompleted)
			return;

		_generationResultHandled = true;

		if (_generationTask.IsFaulted)
		{
			Exception exception =
				_generationTask.Exception?.GetBaseException() ??
				new InvalidOperationException("Dungeon generation failed.");

			GD.PushError($"[Dungeon] Generation failed: {exception}");
			return;
		}

		if (_generationTask.IsCanceled)
		{
			GD.PushError("[Dungeon] Generation was canceled.");
			return;
		}

		_blueprint = _generationTask.Result;
		_runtime = new DungeonRuntimeState(_blueprint);

		_presenter = new RoomPresenter
		{
			Name = "RoomPresenter"
		};
		AddChild(_presenter);
		MoveChild(_presenter, 0);

		_presenter.Initialize(
			_blueprint,
			_runtime,
			_tileSet,
			_roomMaterial,
			_generationConfig.CorridorLengthCells
		);

		_presenter.ShowNeighborhood(
			_runtime.ActiveRoom,
			GridTransform.Identity
		);

		PlacePlayerAtRoomCenter(_runtime.ActiveRoom);
		_player.SetCurrentRoom(_runtime.ActiveRoom);
		_director.Initialize(_blueprint, _presenter, _player);
		_presenter.UpdateZOrdering(
			_player.GlobalPosition,
			new[] { _runtime.ActiveRoom }
		);
		_player.SetPhysicsProcess(true);

		ulong fingerprint = DungeonBlueprintFingerprint.Compute(_blueprint);
		DungeonGraphStatistics stats =
			DungeonGraphAnalyzer.Analyze(_blueprint);

		GD.Print(
			$"[Dungeon] Generated rooms={stats.RoomCount}, " +
			$"edges={stats.EdgeCount}, density={stats.Density:F6}, " +
			$"loop_edges={stats.LoopEdgeCount}, " +
			$"branch_rooms={stats.BranchRoomCount}, " +
			$"diameter_edges={stats.DiameterEdges}, " +
			$"rooms_between={stats.RoomsBetweenFurthest}, " +
			$"fingerprint=0x{fingerprint:X16}."
		);
		LogGraphBlock(_blueprint, stats, fingerprint);
		LogRuntimeState("initial");
	}

	// The sweep runs independently so graph analysis never stalls scene startup.
	private void TryFinishProbabilitySweep()
	{
		if (
			_probabilitySweepResultHandled ||
			_probabilitySweepTask is null ||
			!_probabilitySweepTask.IsCompleted
		)
		{
			return;
		}

		_probabilitySweepResultHandled = true;

		if (_probabilitySweepTask.IsFaulted)
		{
			Exception exception =
				_probabilitySweepTask.Exception?.GetBaseException() ??
				new InvalidOperationException("Probability sweep failed.");

			GD.PushError($"[Dungeon] Probability sweep failed: {exception}");
			return;
		}

		if (_probabilitySweepTask.IsCanceled)
		{
			GD.PushError("[Dungeon] Probability sweep was canceled.");
			return;
		}

		LogProbabilitySweepBlock(_probabilitySweepTask.Result);
	}

	// Emits a compact block that can be copied directly into tools/visualize_graph.py.
	private void LogGraphBlock(
		DungeonBlueprint blueprint,
		DungeonGraphStatistics stats,
		ulong fingerprint)
	{
		var output = new StringBuilder();
		output.AppendLine("[DungeonGraph] BEGIN");
		output.AppendLine($"seed={_resolvedGenerationSeed}");
		output.AppendLine(
			$"branch_probability={_generationConfig.BranchProbability:F4}"
		);
		output.AppendLine(
			$"loop_probability={_generationConfig.LoopProbability:F4}"
		);
		output.AppendLine($"fingerprint=0x{fingerprint:X16}");
		output.AppendLine($"start_room={blueprint.StartRoom.Value}");
		output.AppendLine($"rooms={stats.RoomCount}");
		output.AppendLine($"edges={stats.EdgeCount}");
		output.AppendLine($"density={stats.Density:F8}");
		output.AppendLine($"loop_edges={stats.LoopEdgeCount}");
		output.AppendLine($"branch_rooms={stats.BranchRoomCount}");
		output.AppendLine($"diameter_edges={stats.DiameterEdges}");
		output.AppendLine(
			$"rooms_between_furthest={stats.RoomsBetweenFurthest}"
		);
		output.AppendLine(
			$"diameter_endpoints=" +
			$"{stats.DiameterStart.Value},{stats.DiameterEnd.Value}"
		);
		output.AppendLine(
			$"diameter_path=" +
			string.Join(",", stats.DiameterPath.Select(room => room.Value))
		);

		foreach (RoomConnection connection in blueprint.Connections
			.OrderBy(connection =>
				Math.Min(connection.A.Room.Value, connection.B.Room.Value))
			.ThenBy(connection =>
				Math.Max(connection.A.Room.Value, connection.B.Room.Value)))
		{
			output.AppendLine(
				$"edge={connection.A.Room.Value}," +
				$"{connection.B.Room.Value}," +
				$"{connection.A.Index},{connection.B.Index}"
			);
		}

		output.Append("[DungeonGraph] END");
		GD.Print(output.ToString());
	}

	private static void LogProbabilitySweepBlock(
		IReadOnlyList<ProbabilitySweepRow> rows)
	{
		var output = new StringBuilder();
		output.AppendLine("[DungeonProbabilitySweep] BEGIN");
		output.AppendLine(
			"branch_probability,loop_probability,samples," +
			"avg_rooms,avg_edges,avg_density,avg_loop_edges," +
			"avg_branch_rooms,avg_diameter_edges,avg_rooms_between"
		);

		foreach (ProbabilitySweepRow row in rows)
		{
			output.AppendLine(
				$"{row.BranchProbability:F4}," +
				$"{row.LoopProbability:F4}," +
				$"{row.Samples}," +
				$"{row.AverageRoomCount:F4}," +
				$"{row.AverageEdgeCount:F4}," +
				$"{row.AverageDensity:F8}," +
				$"{row.AverageLoopEdgeCount:F4}," +
				$"{row.AverageBranchRoomCount:F4}," +
				$"{row.AverageDiameterEdges:F4}," +
				$"{row.AverageRoomsBetweenFurthest:F4}"
			);
		}

		output.Append("[DungeonProbabilitySweep] END");
		GD.Print(output.ToString());
	}

	private static double[] BuildProbabilityAxis(double current)
	{
		double roundedCurrent = Math.Round(
			Math.Clamp(current, 0.0, 1.0),
			4
		);

		return new[]
			{
				0.0,
				0.2,
				0.4,
				0.6,
				0.8,
				1.0,
				roundedCurrent
			}
			.Distinct()
			.OrderBy(value => value)
			.ToArray();
	}

	// A transition starts only when the player enters the active room's doorway tile.
	private void TryBeginTransition()
	{
		RoomId activeRoom = _runtime.ActiveRoom;
		RoomView activeView = _presenter.GetPrimaryView(activeRoom);

		foreach (RoomConnection connection in _blueprint.ConnectionsFor(activeRoom))
		{
			ConnectorDefinition sourceConnector =
				_blueprint.GetConnector(connection.ConnectorFor(activeRoom));

			DoorwayGeometry doorway = BuildDoorwayGeometry(
				activeView,
				sourceConnector
			);

			if (!IsInsideDoorway(_player.GlobalPosition, doorway))
				continue;

			RoomId destination = connection.OtherRoom(activeRoom);
			RoomView destinationView =
				_presenter.GetNeighborView(activeRoom, connection);

			_transition = new TransitionState(
				connection,
				activeRoom,
				destination,
				doorway.Center,
				doorway.Axis
			);
			_runtime.BeginDoorway(activeRoom, destination);

			_presenter.ShowDoorwayNeighborhood(
				activeRoom,
				destination,
				activeView.Projection,
				destinationView.Projection
			);
			_director.OnPresentationChanged();

			LogRuntimeState(
				$"entered connector {sourceConnector.Id} -> {destination}"
			);
			return;
		}
	}

	// Signed distance along the doorway axis decides whether the player committed
	// to the destination or backed out into the source room.
	private void UpdateTransition()
	{
		Vector2 offset = _player.GlobalPosition - _transition.Center;
		float along = offset.Dot(_transition.Axis);
		Vector2 perpendicular = new(-_transition.Axis.Y, _transition.Axis.X);
		float across = Mathf.Abs(offset.Dot(perpendicular));

		if (across > DoorwayHalfWidthPx * 2.0f)
		{
			if (along <= 0.0f)
				CancelTransition();
			else
				CommitTransition();

			return;
		}

		// The doorway state now spans exactly one tile: the connector corridor
		// cell. Crossing either cell boundary commits or cancels the transition.
		if (along <= -DoorCommitDistancePx)
		{
			CancelTransition();
			return;
		}

		if (along >= DoorCommitDistancePx)
			CommitTransition();
	}

	private void CancelTransition()
	{
		RoomId sourceRoom = _transition.From;
		GridTransform sourceTransform =
			_presenter.GetPrimaryView(sourceRoom).Projection;

		_transition = null;
		_runtime.CancelDoorway();

		_presenter.ShowNeighborhood(
			sourceRoom,
			sourceTransform
		);
		_director.OnPresentationChanged();

		LogRuntimeState("connector canceled");
	}

	private void CommitTransition()
	{
		RoomId sourceRoom = _transition.From;
		RoomId targetRoom = _transition.To;
		GridTransform targetTransform =
			_presenter.GetPrimaryView(targetRoom).Projection;

		_transition = null;
		_runtime.CommitDoorway(targetRoom);
		_player.SetCurrentRoom(targetRoom);

		_presenter.ShowNeighborhood(
			targetRoom,
			targetTransform
		);
		_director.OnPlayerEnteredRoom(sourceRoom, targetRoom);

		LogRuntimeState($"entered {RoomName(targetRoom)}");
	}

	// Doorway tests use the rendered RoomView transform, not reconstructed grid math.
	private static DoorwayGeometry BuildDoorwayGeometry(
		RoomView roomView,
		ConnectorDefinition connector)
	{
		Vector2 center = roomView.CellCenterToGlobal(connector.Cell);
		Vector2 axis =
			roomView.DirectionToGlobal(connector.Facing).Normalized();

		return new DoorwayGeometry(center, axis);
	}

	private static bool IsInsideDoorway(
		Vector2 position,
		DoorwayGeometry doorway)
	{
		Vector2 offset = position - doorway.Center;
		float along = offset.Dot(doorway.Axis);
		Vector2 perpendicular = new(-doorway.Axis.Y, doorway.Axis.X);
		float across = Mathf.Abs(offset.Dot(perpendicular));

		return
			Mathf.Abs(along) < DoorwayHalfLengthPx &&
			across <= DoorwayHalfWidthPx;
	}

	private void PlacePlayerAtRoomCenter(RoomId roomId)
	{
		RoomDefinition room = _blueprint.GetRoom(roomId);
		RoomView view = _presenter.GetPrimaryView(roomId);

		var centerCell = new Vector2I(
			room.Size.X / 2,
			room.Size.Y / 2
		);

		_player.GlobalPosition = view.CellCenterToGlobal(centerCell);
	}

	private void LogRuntimeState(string reason)
	{
		string doorway = _runtime.IsInDoorway
			? $"{RoomName(_runtime.DoorwayFrom!.Value)}" +
			  $"->{RoomName(_runtime.DoorwayTo!.Value)}"
			: "none";

		string loaded = string.Join(
			", ",
			_runtime.Rooms
				.Where(room => room.IsLoaded)
				.OrderBy(room => room.RoomId.Value)
				.Select(room =>
					$"{RoomName(room.RoomId)}:" +
					$"{(room.IsSimulated ? "sim" : "view")}")
		);

		GD.Print(
			$"[Dungeon] {reason}; active={RoomName(_runtime.ActiveRoom)}, " +
			$"doorway={doorway}, loaded=[{loaded}]"
		);
	}

	private string RoomName(RoomId roomId)
		=> _blueprint.GetRoom(roomId).Name;

	private readonly record struct DoorwayGeometry(
		Vector2 Center,
		Vector2 Axis
	);

	private sealed record TransitionState(
		RoomConnection Connection,
		RoomId From,
		RoomId To,
		Vector2 Center,
		Vector2 Axis
	);
}
