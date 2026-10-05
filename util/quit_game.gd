extends Control

@export_group("Nodes")
@export var panel: Control
@export var quit_label: Label
@export var quit_progress: ProgressBar

@export_group("Timings")
@export var quit_hold_seconds: float = 2.0
@export var reveal_time: float = 0.15
@export var hide_time: float = 0.12

@export_group("Animation")
@export_subgroup("Modulation", "modulate_")
@export var modulate_reveal: float = 1.0
@export var modulate_hide: float = 0.0
@export_subgroup("Scale", "scale_")
@export var scale_enable: bool = true
@export var scale_reveal: Vector2 = Vector2.ONE
@export var scale_hide: Vector2 = Vector2.ZERO
@export_subgroup("Position", "position_")
@export var position_type: PositioningType = PositioningType.ZERO
@export var position_reveal: Vector2 = Vector2.ZERO
@export var position_hide: Vector2 = Vector2.ZERO


var _hold_time: float = 0.0
var _is_holding_quit: bool = false
var _reveal_tween: Tween
var _hide_tween: Tween

enum PositioningType {ZERO, CENTER, MANUAL}

func _ready() -> void:
	panel.visible = false
	quit_progress.min_value = 0.0
	quit_progress.max_value = quit_hold_seconds
	quit_progress.value = 0.0


func _process(delta: float) -> void:
	if Input.is_action_pressed("quit_game"):
		if not _is_holding_quit:
			_start_quit_hold()

		_hold_time = min(_hold_time + delta, quit_hold_seconds)
		quit_progress.value = _hold_time
		#quit_label.text = "Hold ESC to quit... %.3f" % (quit_hold_seconds - _hold_time)

		if _hold_time >= quit_hold_seconds:
			get_tree().quit()
	else:
		if _is_holding_quit:
			_cancel_quit_hold()


func _start_quit_hold() -> void:
	_is_holding_quit = true
	_hold_time = 0.0

	panel.visible = true
	panel.modulate.a = modulate_hide
	if scale_enable:
		panel.scale = scale_hide
	if position_type != PositioningType.ZERO:
		match position_type:
			PositioningType.CENTER:
				panel.position = panel.size / 2
			PositioningType.MANUAL:
				panel.position = position_hide

	#quit_label.text = "Hold Escape to quit"
	quit_progress.value = 0.0

	if _reveal_tween and _reveal_tween.is_valid():
		_reveal_tween.kill()
	if _hide_tween and _hide_tween.is_valid():
		_hide_tween.kill()

	_reveal_tween = create_tween()
	_reveal_tween.tween_property(panel, "modulate:a", modulate_reveal, reveal_time)
	if scale_enable:
		_reveal_tween.parallel().tween_property(panel, "scale", scale_reveal, reveal_time)
	match position_type:
		PositioningType.CENTER:
			_reveal_tween.parallel().tween_property(panel, "position", Vector2.ZERO, reveal_time)
		PositioningType.MANUAL:
			_reveal_tween.parallel().tween_property(panel, "position", position_reveal, reveal_time)



func _cancel_quit_hold() -> void:
	_is_holding_quit = false
	_hold_time = 0.0
	quit_progress.value = 0.0

	if _reveal_tween and _reveal_tween.is_valid():
		_reveal_tween.kill()
	if _hide_tween and _hide_tween.is_valid():
		_hide_tween.kill()

	_hide_tween = create_tween()
	_hide_tween.tween_property(panel, "modulate:a", modulate_hide, hide_time)
	if scale_enable:
		_hide_tween.parallel().tween_property(panel, "scale", scale_hide, hide_time)
	match position_type:
		PositioningType.CENTER:
			_hide_tween.parallel().tween_property(panel, "position", panel.size / 2, hide_time)
		PositioningType.MANUAL:
			_hide_tween.parallel().tween_property(panel, "position", position_hide, hide_time)

	_hide_tween.finished.connect(func() -> void:
		panel.visible=false
	)
