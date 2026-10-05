using Godot;

/// Room-local enemy that wanders and pursues the player only in its own room.
public partial class Enemy : CharacterBody2D, IRoomSimulationParticipant
{
	[Export] public int MaxHp { get; set; } = 1;
	[Export] public float MoveSpeed { get; set; } = 70.0f;
	[Export] public int AttackDamage { get; set; } = 1;
	[Export(PropertyHint.Range, "0.1,5,0.1")]
	public float AttackCooldownSeconds { get; set; } = 1.0f;
	[Export] public float AttackRange { get; set; } = 22.0f;
	[Export(PropertyHint.Range, "0.25,10,0.25")]
	public float WanderRetargetSeconds { get; set; } = 2.0f;
	[Export] public float NavigationMargin { get; set; } = 10.0f;

	[Export] public NodePath NavigationAgentPath { get; set; } =
		new("NavigationAgent2D");
	[Export] public NodePath AttackCooldownPath { get; set; } =
		new("AttackCooldown");

	public int Hp { get; private set; }

	private NavigationAgent2D _agent;
	private Timer _attackCooldown;
	private RoomView _room;
	private PlayerController _player;
	private readonly RandomNumberGenerator _rng = new();

	private float _wanderRetargetRemaining;
	private uint _activeCollisionLayer;
	private uint _activeCollisionMask;

	public override void _Ready()
	{
		Hp = MaxHp;
		_activeCollisionLayer = CollisionLayer;
		_activeCollisionMask = CollisionMask;

		_agent = GetNode<NavigationAgent2D>(NavigationAgentPath);
		_attackCooldown = GetNode<Timer>(AttackCooldownPath);
		_attackCooldown.WaitTime = AttackCooldownSeconds;

		_room = FindOwningRoom();
		_player = GetTree().GetFirstNodeInGroup("player") as PlayerController;

		if (_room is null)
		{
			GD.PushWarning(
				$"[Enemy] {Name} is not below RoomView.Contents; AI disabled."
			);
			SetPhysicsProcess(false);
			return;
		}

		if (_player is null)
		{
			GD.PushWarning($"[Enemy] {Name} could not find the Player.");
			SetPhysicsProcess(false);
			return;
		}

		// Each RoomView owns a separate navigation map, so even coincident
		// non-Euclidean room projections can never share enemy paths.
		_agent.SetNavigationMap(_room.NavigationMap);
		_rng.Randomize();
		SetRoomSimulationEnabled(_room.IsSimulated);

		PickWanderTarget();
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_room is null || _player is null || !_room.IsSimulated)
		{
			Velocity = Vector2.Zero;
			return;
		}

		bool playerInRoom = _player.IsInRoom(_room.Definition.Id);

		if (playerInRoom)
		{
			Vector2 target = _room.ClampGlobalPointToInterior(
				_player.GlobalPosition,
				NavigationMargin
			);
			_agent.TargetPosition = target;
			TryAttackPlayer();
		}
		else
		{
			_wanderRetargetRemaining -= (float)delta;

			if (
				_wanderRetargetRemaining <= 0.0f ||
				_agent.IsNavigationFinished()
			)
			{
				PickWanderTarget();
			}
		}

		MoveAlongNavigationPath();
	}

	/// Enables physical interaction only while this room is authoritative.
	public void SetRoomSimulationEnabled(bool enabled)
	{
		CollisionLayer = enabled ? _activeCollisionLayer : 0;
		CollisionMask = enabled ? _activeCollisionMask : 0;

		if (!enabled)
			Velocity = Vector2.Zero;
	}

	/// Applies damage and removes the enemy when its HP reaches zero.
	public void Hurt(int damage)
	{
		if (damage <= 0 || Hp <= 0)
			return;

		Hp = Mathf.Max(0, Hp - damage);

		if (Hp == 0)
			QueueFree();
	}

	private void MoveAlongNavigationPath()
	{
		if (_agent.IsNavigationFinished())
		{
			Velocity = Vector2.Zero;
			return;
		}

		Vector2 next = _agent.GetNextPathPosition();
		Vector2 direction = GlobalPosition.DirectionTo(next);
		Velocity = direction * MoveSpeed;
		MoveAndSlide();

		// Physics walls normally provide this boundary. The clamp also protects
		// against doorway openings and navigation synchronization edge cases.
		GlobalPosition = _room.ClampGlobalPointToInterior(
			GlobalPosition,
			NavigationMargin
		);
	}

	private void TryAttackPlayer()
	{
		if (!_attackCooldown.IsStopped())
			return;

		if (GlobalPosition.DistanceTo(_player.GlobalPosition) > AttackRange)
			return;

		_player.Hurt(AttackDamage);
		_attackCooldown.Start();
	}

	private void PickWanderTarget()
	{
		_wanderRetargetRemaining = WanderRetargetSeconds;

		float x = _rng.Randf();
		float y = _rng.Randf();

		_agent.TargetPosition = _room.InteriorPointToGlobal(
			x,
			y,
			NavigationMargin
		);
	}

	private RoomView FindOwningRoom()
	{
		Node ancestor = GetParent();

		while (ancestor is not null)
		{
			if (ancestor is RoomView room)
				return room;

			ancestor = ancestor.GetParent();
		}

		return null;
	}
}
