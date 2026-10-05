# PCG dungeon architecture

The dungeon is a graph of independent local spaces. A room has local tile coordinates and
connector relationships, but it never owns a permanent global position.

## Generation

`DungeonGenerator` runs on a worker thread and only produces a `DungeonBlueprint`.
It does not create Nodes, Resources, TileMapLayers, or otherwise touch the scene tree.

The generator is deterministic for a given seed and configuration. It uses a local PCG32
implementation plus explicit seed mixing instead of `System.Random` or runtime hash codes.

A digger owns only:

- a digger id,
- its current room,
- its generation step.

Each step consumes one unused connector candidate. It either creates a rectangular room or,
according to `LoopProbability`, connects to a free candidate on an existing room. A successful
branch roll creates a second digger in the destination room. Connector capacity is finite,
connector candidates cannot be adjacent, and self-connections are rejected.

Only connector candidates that actually participate in graph edges are copied into the final
`DungeonBlueprint`. Unused candidates stay ordinary wall tiles and never appear as exits.

Room dimensions include the one-tile wall perimeter. Generation therefore uses a 5x5 minimum
definition size, which guarantees a walkable interior of at least 3x3 cells.

Final room doorways are derived from the graph edges themselves rather than from the digger's
temporary free/used connector bookkeeping. Blueprint validation requires every connector to be
the endpoint of exactly one graph edge. This keeps branch and loop topology from creating a
transition whose visual room definition lacks its doorway.

Room content gets a separate deterministic `ContentSeed`, so future changes to enemy or loot
generation do not need to perturb the topology stream.

## Runtime state

`DungeonRuntimeState` persists room-level state independently of instantiated views.

A room can be:

- unloaded: blueprint/runtime data only,
- loaded and visible: adjacent room, no collision or processing,
- simulated: active room,
- simulated together with another room: player is standing in their connector.

Interactive room-local Nodes should live below `RoomView.Contents`. That subtree is disabled
when the room is not authoritative, while its tiles remain visible.

Each `RoomView` also owns an isolated `NavigationServer2D` map containing only the walkable
interior, excluding the perimeter and connector cells. Room-local actors such as `Enemy`
bind their `NavigationAgent2D` to that map. This is intentionally one navigation map per
room projection: visually overlapping non-Euclidean rooms must never become connected
navigation surfaces.

Enemies additionally clamp movement to the room interior after physics movement. The
navigation surface prevents planned paths through doorways, while the clamp is a final guard
against physics/navigation synchronization edge cases. Enemies compare their owning
`RoomId` with the player's logical current room, so visual overlap between projected rooms
cannot make an enemy aggro the player from another logical room.

## Presentation

`RoomPresenter` gives the active room a temporary local-to-canvas transform and derives each
neighbor transform through the connecting pair of connectors. Entering another room promotes
the already-visible destination projection instead of assigning the dungeon a global embedding.

Background projections are keyed by their incoming connector, not only by RoomId. This matters
for graph loops: while two rooms are active in a doorway, a third room may be visible through
both of them at incompatible canvas transforms. The presenter may therefore render two
non-authoritative projections of the same logical room.

The active room is Z=0. While standing in a connector, both endpoint rooms are Z=0. Other loaded
projections are behind them and are ordered by player distance to the connector through which
that projection is visible.

Room projections may overlap arbitrarily in a local chart. The active room therefore owns its
entire projected footprint, not just the wall seam of the edge currently being traversed.
Adjacent projections are clipped anywhere their local cells map into the active room footprint.
This prevents one neighbor from drawing a wall across another doorway of the active room.

Each RoomView still uses one TileMapLayer for both rendering and collision. Inactive adjacent
rooms have collision disabled, so clipping their overlapping cells is presentation-only in
practice. While standing in a connector, both endpoint rooms are simulated; the source room
temporarily owns the source/target overlap until the transition commits. Only overlapping
perimeter cells are clipped from the target, so the source room supplies the shared seam
collision. The endpoint neighborhoods remain visible behind the two authoritative rooms.

An authoritative room opens all of its graph-backed connectors. An adjacent projection opens
only the incoming connector through which it is visible in the current chart; its other exits
stay visually sealed until that room becomes authoritative.

## Connector transition

The configured connector length is one tile. For this case the source and destination perimeter
connector cells project onto the same canvas tile; neither room adds an extra exterior corridor
cell. The doorway state spans that shared tile exactly:

1. entering the shared doorway tile activates both endpoint rooms,
2. remaining on the tile keeps both simulated,
3. crossing its destination edge commits the destination as active,
4. backing out across its source edge cancels the transition.

After commit, the previous room remains loaded because it is an adjacent room, but its collision
and `Contents` processing are disabled.

## Current boundary

Room tile geometry is intentionally simple rectangular placeholder generation. Terrain/art
generation and room contents are separate concerns and can be replaced without changing the
topology, runtime-state, or projection model.


## Graph diagnostics

After generation, the runtime logs a copyable `[DungeonGraph] BEGIN/END` block containing
the seed, current probabilities, graph metrics, diameter path, and every room-to-room edge.
The edge rows use:

```text
edge=room_a,room_b,connector_index_a,connector_index_b
```

Graph density is the undirected simple-graph density:

```text
density = edges / (rooms * (rooms - 1) / 2)
```

The furthest-room distance is the graph diameter: the largest shortest-path distance between
any two rooms. `diameter_edges` counts connector crossings; `rooms_between_furthest` subtracts
the two endpoint rooms, so a path containing five rooms reports four edges and three rooms
between its endpoints.

When `LogProbabilitySweep` is enabled, a second worker task samples branch and loop
probabilities from 0.0 through 1.0, also including the current configured value. Every
probability pair uses the same deterministic sample seeds. The logged
`[DungeonProbabilitySweep] BEGIN/END` CSV block reports averages for room count, edge count,
density, loops, branch rooms, diameter, and rooms between the furthest pair.

The sweep is empirical because branch and loop probabilities interact with finite connector
capacity and the room-count cap. In general, extra loop edges tend to increase density and
shorten shortest-path distances, while extra branching tends to reduce long chain-like paths;
the sweep shows the actual effect for the current generator rather than assuming either trend.

`tools/visualize_graph.py` is a self-contained uv script with inline dependencies for
Matplotlib and NetworkX. Save or copy the Godot output to a file and run:

```bash
uv run --script tools/visualize_graph.py dungeon.log
```

It draws the numbered logical room graph with a spring layout. The dungeon intentionally has no
single global spatial embedding, so this is a topology visualization rather than a physical map.
If the probability sweep block is present, a second figure shows heatmaps for average density
and average rooms between the furthest rooms.

To save both plots without opening windows:

```bash
uv run --script tools/visualize_graph.py dungeon.log \
  --save-prefix graph/seed-1 --no-show
```

The script also accepts piped log text on stdin.


## AI Director

After the player is placed in the start room, `AIDirector` computes a unit-weight Dijkstra
map from that room and chooses the furthest logical room as the exit. It then computes the
guidance Dijkstra map from every room back to that exit. Distances count graph edges rather
than canvas distance, preserving the non-Euclidean room model.

The exit is a logical room placement. Its visual marker is instantiated when the exit room has
a loaded projection and follows that projection if it is promoted from background to
authoritative.

Guidance uses the Dijkstra gradient. From the player's current room, the Director selects the
adjacent room with the smallest distance-to-exit and places one entity there. A new guide is
placed immediately after the player takes a transition that reduces the Dijkstra distance. If
the player instead increases their distance from the exit twice before making progress, the
Director places another guide from the new room. Equal-distance moves neither count as progress
nor as a wrong-direction step.

Guide entity choice is deterministic from the dungeon seed and uses the player's HP at placement
time:

- HP > 5: heart : enemy = 1 : 2.
- HP <= 5: heart : enemy = 2 : 1.

Hearts fully restore the player and consume themselves on touch. Enemy and heart interactions
implement `IRoomSimulationParticipant`, so their collision/monitoring is disabled in inactive
adjacent projections even though those projections remain visible.
