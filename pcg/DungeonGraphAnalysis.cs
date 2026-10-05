using System;
using System.Collections.Generic;
using System.Linq;

/// Structural metrics for one generated dungeon graph.
public sealed record DungeonGraphStatistics(
	int RoomCount,
	int EdgeCount,
	double Density,
	int LoopEdgeCount,
	int BranchRoomCount,
	int DiameterEdges,
	int RoomsBetweenFurthest,
	RoomId DiameterStart,
	RoomId DiameterEnd,
	IReadOnlyList<RoomId> DiameterPath
);

/// Averaged graph metrics for one branch/loop probability pair.
public sealed record ProbabilitySweepRow(
	double BranchProbability,
	double LoopProbability,
	int Samples,
	double AverageRoomCount,
	double AverageEdgeCount,
	double AverageDensity,
	double AverageLoopEdgeCount,
	double AverageBranchRoomCount,
	double AverageDiameterEdges,
	double AverageRoomsBetweenFurthest
);

/// Computes graph metrics without depending on Godot Nodes or scene state.
public static class DungeonGraphAnalyzer
{
	/// Measures density, surplus loop edges, branching, and graph diameter.
	public static DungeonGraphStatistics Analyze(DungeonBlueprint blueprint)
	{
		int roomCount = blueprint.RoomCount;
		int edgeCount = blueprint.Connections.Count;
		double possibleEdges =
			roomCount < 2
				? 0.0
				: roomCount * (roomCount - 1) / 2.0;
		double density =
			possibleEdges <= 0.0
				? 0.0
				: edgeCount / possibleEdges;

		int loopEdges = Math.Max(0, edgeCount - Math.Max(0, roomCount - 1));
		int branchRooms = blueprint.Rooms.Count(room =>
			blueprint.ConnectionsFor(room.Id).Count >= 3
		);

		if (roomCount == 0)
			throw new InvalidOperationException("Cannot analyze an empty dungeon.");

		RoomId diameterStart = blueprint.Rooms[0].Id;
		RoomId diameterEnd = diameterStart;
		int diameterEdges = 0;
		IReadOnlyList<RoomId> diameterPath = new[] { diameterStart };

		foreach (RoomDefinition source in blueprint.Rooms)
		{
			BreadthFirstSearchResult bfs =
				BreadthFirstSearch(blueprint, source.Id);

			foreach ((RoomId destination, int distance) in bfs.Distances)
			{
				if (distance <= diameterEdges)
					continue;

				diameterEdges = distance;
				diameterStart = source.Id;
				diameterEnd = destination;
				diameterPath = ReconstructPath(
					source.Id,
					destination,
					bfs.Previous
				);
			}
		}

		return new DungeonGraphStatistics(
			roomCount,
			edgeCount,
			density,
			loopEdges,
			branchRooms,
			diameterEdges,
			Math.Max(0, diameterEdges - 1),
			diameterStart,
			diameterEnd,
			diameterPath
		);
	}

	private static BreadthFirstSearchResult BreadthFirstSearch(
		DungeonBlueprint blueprint,
		RoomId start)
	{
		var distances = new Dictionary<RoomId, int>
		{
			[start] = 0
		};
		var previous = new Dictionary<RoomId, RoomId>();
		var pending = new Queue<RoomId>();
		pending.Enqueue(start);

		while (pending.Count > 0)
		{
			RoomId room = pending.Dequeue();
			int nextDistance = distances[room] + 1;

			foreach (RoomConnection connection in blueprint.ConnectionsFor(room))
			{
				RoomId neighbor = connection.OtherRoom(room);
				if (distances.ContainsKey(neighbor))
					continue;

				distances.Add(neighbor, nextDistance);
				previous.Add(neighbor, room);
				pending.Enqueue(neighbor);
			}
		}

		return new BreadthFirstSearchResult(distances, previous);
	}

	private static IReadOnlyList<RoomId> ReconstructPath(
		RoomId start,
		RoomId end,
		IReadOnlyDictionary<RoomId, RoomId> previous)
	{
		var path = new List<RoomId> { end };
		RoomId current = end;

		while (current != start)
		{
			current = previous[current];
			path.Add(current);
		}

		path.Reverse();
		return path;
	}

	private sealed record BreadthFirstSearchResult(
		IReadOnlyDictionary<RoomId, int> Distances,
		IReadOnlyDictionary<RoomId, RoomId> Previous
	);
}

/// Samples generator settings to show how branch and loop probabilities affect topology.
public static class DungeonProbabilityAnalyzer
{
	private const ulong SweepSeedTag = 0x5357454550UL;

	/// Runs the same deterministic sample seeds for every probability pair.
	public static IReadOnlyList<ProbabilitySweepRow> Sweep(
		GenerationConfig baseConfig,
		ulong baseSeed,
		IReadOnlyList<double> branchProbabilities,
		IReadOnlyList<double> loopProbabilities,
		int samplesPerPoint)
	{
		if (samplesPerPoint < 1)
			throw new ArgumentOutOfRangeException(nameof(samplesPerPoint));

		var rows = new List<ProbabilitySweepRow>(
			branchProbabilities.Count * loopProbabilities.Count
		);

		foreach (double branchProbability in branchProbabilities)
		{
			foreach (double loopProbability in loopProbabilities)
			{
				var config = baseConfig with
				{
					BranchProbability = branchProbability,
					LoopProbability = loopProbability
				};
				config.Validate();

				double roomCount = 0.0;
				double edgeCount = 0.0;
				double density = 0.0;
				double loopEdges = 0.0;
				double branchRooms = 0.0;
				double diameterEdges = 0.0;
				double roomsBetween = 0.0;

				for (int sample = 0; sample < samplesPerPoint; sample++)
				{
					// Reusing each sample seed across settings makes differences
					// more attributable to the probability changes themselves.
					ulong sampleSeed = SeedMixer.Mix(
						baseSeed,
						SweepSeedTag,
						(ulong)sample
					);
					DungeonBlueprint blueprint =
						new DungeonGenerator(config).Generate(sampleSeed);
					DungeonGraphStatistics stats =
						DungeonGraphAnalyzer.Analyze(blueprint);

					roomCount += stats.RoomCount;
					edgeCount += stats.EdgeCount;
					density += stats.Density;
					loopEdges += stats.LoopEdgeCount;
					branchRooms += stats.BranchRoomCount;
					diameterEdges += stats.DiameterEdges;
					roomsBetween += stats.RoomsBetweenFurthest;
				}

				double divisor = samplesPerPoint;
				rows.Add(new ProbabilitySweepRow(
					branchProbability,
					loopProbability,
					samplesPerPoint,
					roomCount / divisor,
					edgeCount / divisor,
					density / divisor,
					loopEdges / divisor,
					branchRooms / divisor,
					diameterEdges / divisor,
					roomsBetween / divisor
				));
			}
		}

		return rows;
	}
}
