using Godot;
using System.Collections.Generic;

/// Controls player movement, health, aiming, and the inspector-composed knife.
public partial class PlayerController : CharacterBody2D
{
	[Export] public float Speed { get; set; } = 300.0f;
	[Export] public int MaxHp { get; set; } = 10;
	[Export] public int KnifeDamage { get; set; } = 1;
	[Export(PropertyHint.Range, "0.05,2,0.05")]
	public float KnifeCooldownSeconds { get; set; } = 0.5f;
	[Export(PropertyHint.Range, "0.05,1,0.01")]
	public float StabAnimationSeconds { get; set; } = 0.18f;
	[Export] public float KnifeRestDistance { get; set; } = 16.0f;
	[Export] public float KnifeStabDistance { get; set; } = 34.0f;

	[Export] public NodePath KnifePivotPath { get; set; } = new("KnifePivot");
	[Export] public NodePath KnifeBladePath { get; set; } =
		new("KnifePivot/KnifeBlade");
	[Export] public NodePath KnifeHitboxPath { get; set; } =
		new("KnifePivot/KnifeBlade/KnifeHitbox");
	[Export] public NodePath KnifeCooldownPath { get; set; } =
		new("KnifeCooldown");
	[Export] public NodePath HealthBarPath { get; set; } =
		new("HUD/ProgressBar");

	public int Hp { get; private set; }
	public RoomId CurrentRoom { get; private set; }

	private bool _hasCurrentRoom;

	private Node2D _knifePivot;
	private Node2D _knifeBlade;
	private Area2D _knifeHitbox;
	private Timer _knifeCooldown;
	private ProgressBar _healthBar;

	private readonly HashSet<ulong> _hitEnemies = new();
	private bool _attackActive;
	private float _attackElapsed;

	public override void _Ready()
	{
		_knifePivot = GetNode<Node2D>(KnifePivotPath);
		_knifeBlade = GetNode<Node2D>(KnifeBladePath);
		_knifeHitbox = GetNode<Area2D>(KnifeHitboxPath);
		_knifeCooldown = GetNode<Timer>(KnifeCooldownPath);
		_healthBar = GetNode<ProgressBar>(HealthBarPath);

		_knifeCooldown.WaitTime = KnifeCooldownSeconds;
		_knifeBlade.Position = Vector2.Right * KnifeRestDistance;

		Hp = Mathf.Max(1, MaxHp);
		_healthBar.MaxValue = MaxHp;
		UpdateHealthBar();
	}

	public override void _Process(double delta)
	{
		UpdateKnifeAim();
		UpdateKnifeAnimation((float)delta);
	}

	public override void _PhysicsProcess(double delta)
	{
		Vector2 direction =
			Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");

		Velocity = direction == Vector2.Zero
			? Velocity.MoveToward(Vector2.Zero, Speed)
			: direction * Speed;

		MoveAndSlide();

		if (_attackActive)
			HurtKnifeOverlaps();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (
			@event is InputEventMouseButton mouseButton &&
			mouseButton.ButtonIndex == MouseButton.Left &&
			mouseButton.Pressed
		)
		{
			TryAttack();
		}
	}

	/// Updates the logical room used by room-local AI.
	public void SetCurrentRoom(RoomId roomId)
	{
		CurrentRoom = roomId;
		_hasCurrentRoom = true;
	}

	/// Returns whether the player currently belongs to the given logical room.
	public bool IsInRoom(RoomId roomId)
		=> _hasCurrentRoom && CurrentRoom == roomId;

	/// Applies damage to the player and updates the inspector-authored health bar.
	public void Hurt(int damage)
	{
		if (damage <= 0 || Hp <= 0)
			return;

		Hp = Mathf.Max(0, Hp - damage);
		UpdateHealthBar();
	}

	/// Restores player HP up to MaxHp.
	public void Heal(int amount)
	{
		if (amount <= 0 || Hp <= 0)
			return;

		Hp = Mathf.Min(MaxHp, Hp + amount);
		UpdateHealthBar();
	}

	/// Restores the player directly to MaxHp.
	public void RestoreFullHealth()
	{
		Hp = Mathf.Max(1, MaxHp);
		UpdateHealthBar();
	}

	private void TryAttack()
	{
		if (
			!IsPhysicsProcessing() ||
			_attackActive ||
			!_knifeCooldown.IsStopped()
		)
		{
			return;
		}

		_attackActive = true;
		_attackElapsed = 0.0f;
		_hitEnemies.Clear();
		_knifeCooldown.Start();

		// Catch enemies already touching the knife at the first attack frame.
		HurtKnifeOverlaps();
	}

	private void UpdateKnifeAim()
	{
		Vector2 aim = GetGlobalMousePosition() - GlobalPosition;
		if (aim.LengthSquared() <= 0.0001f)
			return;

		_knifePivot.GlobalRotation = aim.Angle();
	}

	private void UpdateKnifeAnimation(float delta)
	{
		if (!_attackActive)
		{
			_knifeBlade.Position = Vector2.Right * KnifeRestDistance;
			return;
		}

		_attackElapsed += delta;
		float duration = Mathf.Max(0.01f, StabAnimationSeconds);
		float t = Mathf.Clamp(_attackElapsed / duration, 0.0f, 1.0f);

		// Fast extension, slightly slower recovery reads as a stab without
		// requiring an AnimationPlayer or dynamically created scene elements.
		const float apex = 0.42f;
		float thrust = t <= apex
			? t / apex
			: (1.0f - t) / (1.0f - apex);

		float distance = Mathf.Lerp(
			KnifeRestDistance,
			KnifeStabDistance,
			Mathf.Clamp(thrust, 0.0f, 1.0f)
		);
		_knifeBlade.Position = Vector2.Right * distance;

		if (t >= 1.0f)
		{
			_attackActive = false;
			_knifeBlade.Position = Vector2.Right * KnifeRestDistance;
		}
	}

	private void HurtKnifeOverlaps()
	{
		foreach (Node2D body in _knifeHitbox.GetOverlappingBodies())
		{
			if (body is not Enemy enemy)
				continue;

			ulong instanceId = enemy.GetInstanceId();
			if (!_hitEnemies.Add(instanceId))
				continue;

			enemy.Hurt(KnifeDamage);
		}
	}

	private void UpdateHealthBar()
	{
		_healthBar.Value = Hp;
	}
}
