using Godot;
using System;
using System.Linq;

/// Guides the player toward a distant exit by placing room-local entities.
public partial class AIDirector : Node
{
	private const ulong DirectorSeedTag = 0x4149444952454354UL;

	[Export] public PackedScene EnemyScene { get; set; }
	[Export] public PackedScene HeartScene { get; set; }
	[Export] public PackedScene ExitScene { get; set; }

	[Export(PropertyHint.Range, "0.05,0.45,0.05")]
	public float SpawnInsetFraction { get; set; } = 0.25f;

	private DungeonBlueprint _blueprint;
	private RoomPresenter _presenter;
	private PlayerController _player;
	private DungeonDijkstraMap _distanceToExit;
	private DeterministicRandom _rng;

	private Node2D _exitInstance;
	private int _wrongDirectionSteps;
	private int _spawnSerial;

	public RoomId ExitRoom { get; private set; }

	/// Selects the exit, builds the Dijkstra map, and places the first guide entity.
	public void Initialize(
		DungeonBlueprint blueprint,
		RoomPresenter presenter,
		PlayerController player)
	{
		_blueprint = blueprint;
		_presenter = presenter;
		_player = player;

		ValidateScenes();

		DungeonDijkstraMap fromPlayer =
			DungeonDijkstraMap.Build(blueprint, player.CurrentRoom);
		ExitRoom = fromPlayer.FurthestRoom();
		_distanceToExit = DungeonDijkstraMap.Build(blueprint, ExitRoom);

		_rng = new DeterministicRandom(
			SeedMixer.Mix(blueprint.Seed, DirectorSeedTag)
		);

		LogDijkstraMap();
		RefreshExitMarker();
		PlaceGuideEntity(player.CurrentRoom, "initial");
	}

	/// Reattaches deferred content when room projections change.
	public void OnPresentationChanged()
	{
		if (_blueprint is null)
			return;

		RefreshExitMarker();
	}

	/// Updates guidance after the player commits a room transition.
	public void OnPlayerEnteredRoom(RoomId previousRoom, RoomId currentRoom)
	{
		if (_distanceToExit is null)
			return;

		RefreshExitMarker();

		if (currentRoom == ExitRoom)
		{
			_wrongDirectionSteps = 0;
			GD.Print($"[AIDirector] Player reached exit room {ExitRoom}.");
			return;
		}

		int previousDistance = _distanceToExit.Distance(previousRoom);
		int currentDistance = _distanceToExit.Distance(currentRoom);

		if (currentDistance < previousDistance)
		{
			// Following the gradient earns the next breadcrumb immediately.
			_wrongDirectionSteps = 0;
			PlaceGuideEntity(currentRoom, "progress");
			return;
		}

		if (currentDistance > previousDistance)
		{
			_wrongDirectionSteps++;
			GD.Print(
				$"[AIDirector] Wrong-direction step " +
				$"{_wrongDirectionSteps}/2: {previousRoom}({previousDistance}) " +
				$"-> {currentRoom}({currentDistance})."
			);

			if (_wrongDirectionSteps >= 2)
			{
				_wrongDirectionSteps = 0;
				PlaceGuideEntity(currentRoom, "second wrong step");
			}
		}
	}

	private void PlaceGuideEntity(RoomId currentRoom, string reason)
	{
		if (currentRoom == ExitRoom)
			return;

		RoomConnection connection = _blueprint
			.ConnectionsFor(currentRoom)
			.OrderBy(candidate =>
				_distanceToExit.Distance(candidate.OtherRoom(currentRoom)))
			.ThenBy(candidate => candidate.OtherRoom(currentRoom).Value)
			.First();

		RoomId targetRoom = connection.OtherRoom(currentRoom);
		RoomView targetView =
			_presenter.GetNeighborView(currentRoom, connection);

		(bool spawnHeart, PackedScene scene) = ChooseGuideScene();

		Node2D instance = scene.Instantiate<Node2D>();
		instance.Name =
			$"Director_{(spawnHeart ? "Heart" : "Enemy")}_{++_spawnSerial}";
		instance.Position = PickInteriorLocalPoint(targetView);

		targetView.Contents.AddChild(instance);

		GD.Print(
			$"[AIDirector] {reason}: placed " +
			$"{(spawnHeart ? "heart" : "enemy")} in {targetRoom}; " +
			$"distance_to_exit={_distanceToExit.Distance(targetRoom)}, " +
			$"player_hp={_player.Hp}."
		);
	}

	private (bool Heart, PackedScene Scene) ChooseGuideScene()
	{
		// Three equal slots implement the requested 1:2 / 2:1 ratios exactly.
		int roll = _rng.NextInt(3);
		bool heart = _player.Hp > 5
			? roll == 0
			: roll < 2;

		return heart
			? (true, HeartScene)
			: (false, EnemyScene);
	}

	private Vector2 PickInteriorLocalPoint(RoomView view)
	{
		Rect2 interior = view.InteriorRectLocal;
		float inset = Mathf.Clamp(SpawnInsetFraction, 0.0f, 0.45f);
		float span = 1.0f - inset * 2.0f;

		float x = inset + (float)_rng.NextDouble() * span;
		float y = inset + (float)_rng.NextDouble() * span;

		return interior.Position +
			new Vector2(interior.Size.X * x, interior.Size.Y * y);
	}

	private void RefreshExitMarker()
	{
		if (!_presenter.TryGetAnyView(ExitRoom, out RoomView exitView))
			return;

		if (
			_exitInstance is not null &&
			GodotObject.IsInstanceValid(_exitInstance) &&
			_exitInstance.GetParent() == exitView.Contents
		)
		{
			return;
		}

		if (
			_exitInstance is not null &&
			GodotObject.IsInstanceValid(_exitInstance)
		)
		{
			_exitInstance.QueueFree();
		}

		_exitInstance = ExitScene.Instantiate<Node2D>();
		_exitInstance.Name = "DungeonExit";
		_exitInstance.Position =
			exitView.InteriorRectLocal.Position +
			exitView.InteriorRectLocal.Size * 0.5f;
		exitView.Contents.AddChild(_exitInstance);

		GD.Print($"[AIDirector] Exit visible in {ExitRoom}.");
	}

	private void ValidateScenes()
	{
		if (EnemyScene is null)
			throw new InvalidOperationException("AIDirector requires EnemyScene.");
		if (HeartScene is null)
			throw new InvalidOperationException("AIDirector requires HeartScene.");
		if (ExitScene is null)
			throw new InvalidOperationException("AIDirector requires ExitScene.");
	}

	private void LogDijkstraMap()
	{
		string distances = string.Join(
			", ",
			_distanceToExit.Distances
				.OrderBy(pair => pair.Key.Value)
				.Select(pair => $"{pair.Key}:{pair.Value}")
		);

		GD.Print(
			$"[AIDirector] exit={ExitRoom}, " +
			$"start_distance={_distanceToExit.Distance(_player.CurrentRoom)}, " +
			$"dijkstra=[{distances}]"
		);
	}
}
