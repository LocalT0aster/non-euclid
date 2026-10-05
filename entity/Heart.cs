using Godot;

/// Pickup that fully restores player HP when touched.
public partial class Heart : Area2D, IRoomSimulationParticipant
{
	private RoomView _room;
	private uint _activeCollisionMask;

	public override void _Ready()
	{
		_activeCollisionMask = CollisionMask;
		_room = FindOwningRoom();
		BodyEntered += OnBodyEntered;

		SetRoomSimulationEnabled(_room?.IsSimulated ?? true);
	}

	public void SetRoomSimulationEnabled(bool enabled)
	{
		Monitoring = enabled;
		Monitorable = enabled;
		CollisionMask = enabled ? _activeCollisionMask : 0;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not PlayerController player)
			return;

		if (_room is not null && !player.IsInRoom(_room.Definition.Id))
			return;

		player.RestoreFullHealth();
		QueueFree();
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
