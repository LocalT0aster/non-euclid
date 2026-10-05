#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.11"
# dependencies = [
#   "matplotlib>=3.9",
#   "networkx>=3.4",
# ]
# ///

"""Visualize DungeonGraph log blocks emitted by the Godot prototype."""

from __future__ import annotations

import argparse
import csv
import io
import math
import re
import sys
from pathlib import Path
from typing import Iterable

import matplotlib.pyplot as plt
import networkx as nx


GRAPH_BLOCK = "DungeonGraph"
SWEEP_BLOCK = "DungeonProbabilitySweep"


def extract_last_block(text: str, name: str) -> str | None:
    """Return the body of the last named BEGIN/END log block."""
    pattern = re.compile(
        rf"\[{re.escape(name)}\] BEGIN\s*(.*?)\s*"
        rf"\[{re.escape(name)}\] END",
        re.DOTALL,
    )
    matches = list(pattern.finditer(text))
    return matches[-1].group(1) if matches else None


def parse_graph_block(block: str) -> tuple[dict[str, str], list[tuple[int, int]]]:
    """Parse key/value metadata and room edges from a DungeonGraph block."""
    metadata: dict[str, str] = {}
    edges: list[tuple[int, int]] = []

    for raw_line in block.splitlines():
        line = raw_line.strip()
        if not line or "=" not in line:
            continue

        key, value = line.split("=", 1)
        if key == "edge":
            parts = [int(part) for part in value.split(",")]
            if len(parts) < 2:
                raise ValueError(f"Malformed edge line: {line}")
            edges.append((parts[0], parts[1]))
        else:
            metadata[key] = value

    if "rooms" not in metadata:
        raise ValueError("DungeonGraph block does not contain rooms=...")

    return metadata, edges


def parse_sweep_block(block: str) -> list[dict[str, float]]:
    """Parse the probability-sweep CSV block."""
    rows: list[dict[str, float]] = []
    reader = csv.DictReader(io.StringIO(block))

    for row in reader:
        if not row:
            continue

        parsed: dict[str, float] = {}
        for key, value in row.items():
            if key is None or value is None:
                continue
            parsed[key.strip()] = float(value.strip())
        rows.append(parsed)

    return rows


def build_graph(
    metadata: dict[str, str],
    edges: Iterable[tuple[int, int]],
) -> nx.Graph:
    """Construct the logical room graph, including isolated numbered rooms."""
    graph = nx.Graph()
    room_count = int(metadata["rooms"])
    graph.add_nodes_from(range(room_count))
    graph.add_edges_from(edges)
    return graph


def calculate_graph_metrics(graph: nx.Graph) -> dict[str, object]:
    """Recalculate density and diameter from the copied edge list."""
    if graph.number_of_nodes() == 0:
        raise ValueError("Graph has no rooms.")
    if not nx.is_connected(graph):
        raise ValueError("Graph is disconnected; DungeonBlueprint should forbid this.")

    diameter_edges = nx.diameter(graph)

    endpoints = (0, 0)
    path: list[int] = [0]
    for source, lengths in nx.all_pairs_shortest_path_length(graph):
        for destination, distance in lengths.items():
            if distance <= len(path) - 1:
                continue
            endpoints = (source, destination)
            path = nx.shortest_path(graph, source, destination)

    return {
        "density": nx.density(graph),
        "diameter_edges": diameter_edges,
        "rooms_between": max(0, diameter_edges - 1),
        "diameter_endpoints": endpoints,
        "diameter_path": path,
    }


def draw_graph(
    graph: nx.Graph,
    metadata: dict[str, str],
    metrics: dict[str, object],
    layout_seed: int,
) -> plt.Figure:
    """Draw a numbered spring-layout view of the non-Euclidean room graph."""
    figure, axis = plt.subplots(figsize=(10, 8))

    positions = nx.spring_layout(
        graph,
        seed=layout_seed,
        k=1.2 / math.sqrt(max(1, graph.number_of_nodes())),
        iterations=250,
    )

    degrees = dict(graph.degree())
    node_sizes = [
        900 if degrees[node] >= 3 else 650
        for node in graph.nodes
    ]

    nx.draw_networkx_edges(
        graph,
        positions,
        ax=axis,
        width=1.4,
        alpha=0.7,
    )
    nx.draw_networkx_nodes(
        graph,
        positions,
        ax=axis,
        node_size=node_sizes,
    )
    nx.draw_networkx_labels(
        graph,
        positions,
        labels={node: f"R{node}" for node in graph.nodes},
        ax=axis,
        font_size=8,
    )

    start_room = int(metadata.get("start_room", "0"))
    if start_room in graph:
        axis.annotate(
            "start",
            xy=positions[start_room],
            xytext=(8, 8),
            textcoords="offset points",
            fontsize=8,
        )

    branch_probability = metadata.get("branch_probability", "?")
    loop_probability = metadata.get("loop_probability", "?")
    density = float(metrics["density"])
    rooms_between = int(metrics["rooms_between"])
    diameter_edges = int(metrics["diameter_edges"])

    axis.set_title(
        "Dungeon room graph\n"
        f"branch={branch_probability}, loop={loop_probability}, "
        f"density={density:.4f}, "
        f"diameter={diameter_edges} edges "
        f"({rooms_between} rooms between endpoints)"
    )
    axis.set_axis_off()
    figure.tight_layout()
    return figure


def pivot_sweep(
    rows: list[dict[str, float]],
    metric: str,
) -> tuple[list[float], list[float], list[list[float]]]:
    """Pivot sweep rows into branch-by-loop heatmap data."""
    branch_values = sorted({row["branch_probability"] for row in rows})
    loop_values = sorted({row["loop_probability"] for row in rows})
    lookup = {
        (row["branch_probability"], row["loop_probability"]): row[metric]
        for row in rows
    }

    matrix = [
        [lookup[(branch, loop)] for loop in loop_values]
        for branch in branch_values
    ]
    return branch_values, loop_values, matrix


def draw_probability_sweep(rows: list[dict[str, float]]) -> plt.Figure:
    """Draw density and furthest-room separation over the probability sweep."""
    figure, axes = plt.subplots(1, 2, figsize=(13, 5.5))

    panels = [
        ("avg_density", "Average graph density"),
        (
            "avg_rooms_between",
            "Average rooms between furthest rooms",
        ),
    ]

    for axis, (metric, title) in zip(axes, panels, strict=True):
        branch_values, loop_values, matrix = pivot_sweep(rows, metric)
        image = axis.imshow(
            matrix,
            origin="lower",
            aspect="auto",
        )

        axis.set_xticks(range(len(loop_values)))
        axis.set_xticklabels(f"{value:.2f}" for value in loop_values)
        axis.set_yticks(range(len(branch_values)))
        axis.set_yticklabels(f"{value:.2f}" for value in branch_values)
        axis.set_xlabel("Loop probability")
        axis.set_ylabel("Branch probability")
        axis.set_title(title)

        for y, row in enumerate(matrix):
            for x, value in enumerate(row):
                label = f"{value:.3f}" if metric == "avg_density" else f"{value:.1f}"
                axis.text(
                    x,
                    y,
                    label,
                    ha="center",
                    va="center",
                    fontsize=7,
                )

        figure.colorbar(image, ax=axis, shrink=0.82)

    figure.suptitle(
        "Effect of generation probabilities "
        "(averaged over deterministic sample seeds)"
    )
    figure.tight_layout()
    return figure


def average_edge_effect(
    rows: list[dict[str, float]],
    varying_key: str,
    metric: str,
) -> tuple[float, float, float]:
    """Compare a metric at the lowest and highest sampled probability."""
    values = sorted({row[varying_key] for row in rows})
    low = values[0]
    high = values[-1]

    low_values = [
        row[metric] for row in rows if row[varying_key] == low
    ]
    high_values = [
        row[metric] for row in rows if row[varying_key] == high
    ]

    low_mean = sum(low_values) / len(low_values)
    high_mean = sum(high_values) / len(high_values)
    return low_mean, high_mean, high_mean - low_mean


def print_report(
    graph: nx.Graph,
    metadata: dict[str, str],
    metrics: dict[str, object],
    sweep_rows: list[dict[str, float]],
) -> None:
    """Print copied-log metrics plus simple sweep effect sizes."""
    endpoints = metrics["diameter_endpoints"]
    path = metrics["diameter_path"]

    print(
        f"rooms={graph.number_of_nodes()} "
        f"edges={graph.number_of_edges()} "
        f"density={float(metrics['density']):.6f}"
    )
    print(
        f"furthest=R{endpoints[0]}..R{endpoints[1]} "
        f"diameter_edges={metrics['diameter_edges']} "
        f"rooms_between={metrics['rooms_between']}"
    )
    print("diameter_path=" + " -> ".join(f"R{room}" for room in path))

    logged_density = metadata.get("density")
    if logged_density is not None:
        difference = abs(float(logged_density) - float(metrics["density"]))
        if difference > 1e-7:
            print(
                "warning: copied density differs from recalculated density "
                f"by {difference:.3g}",
                file=sys.stderr,
            )

    if not sweep_rows:
        return

    print("\nProbability sweep effect, min -> max probability:")
    for probability_key, label in [
        ("loop_probability", "loop"),
        ("branch_probability", "branch"),
    ]:
        density_low, density_high, density_delta = average_edge_effect(
            sweep_rows,
            probability_key,
            "avg_density",
        )
        distance_low, distance_high, distance_delta = average_edge_effect(
            sweep_rows,
            probability_key,
            "avg_rooms_between",
        )
        print(
            f"{label:6s}: density {density_low:.4f} -> "
            f"{density_high:.4f} ({density_delta:+.4f}); "
            f"rooms_between {distance_low:.2f} -> "
            f"{distance_high:.2f} ({distance_delta:+.2f})"
        )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Plot the numbered dungeon graph and optional probability sweep "
            "from Godot log output."
        )
    )
    parser.add_argument(
        "log",
        nargs="?",
        type=Path,
        help="Godot log file. Omit to read stdin.",
    )
    parser.add_argument(
        "--save-prefix",
        type=Path,
        help=(
            "Save <prefix>_graph.png and, when available, "
            "<prefix>_sweep.png."
        ),
    )
    parser.add_argument(
        "--layout-seed",
        type=int,
        default=7,
        help="Seed for the spring layout; does not affect dungeon data.",
    )
    parser.add_argument(
        "--no-show",
        action="store_true",
        help="Do not open matplotlib windows.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if args.log is None:
        if sys.stdin.isatty():
            print(
                "Pass a Godot log file or pipe copied log output to stdin.",
                file=sys.stderr,
            )
            return 2
        text = sys.stdin.read()
    else:
        text = args.log.read_text(encoding="utf-8")

    graph_block = extract_last_block(text, GRAPH_BLOCK)
    if graph_block is None:
        print("No [DungeonGraph] BEGIN/END block found.", file=sys.stderr)
        return 2

    metadata, edges = parse_graph_block(graph_block)
    graph = build_graph(metadata, edges)
    metrics = calculate_graph_metrics(graph)

    sweep_block = extract_last_block(text, SWEEP_BLOCK)
    sweep_rows = parse_sweep_block(sweep_block) if sweep_block else []

    print_report(graph, metadata, metrics, sweep_rows)

    graph_figure = draw_graph(
        graph,
        metadata,
        metrics,
        args.layout_seed,
    )
    sweep_figure = (
        draw_probability_sweep(sweep_rows)
        if sweep_rows
        else None
    )

    if args.save_prefix is not None:
        prefix = str(args.save_prefix)
        graph_figure.savefig(
            f"{prefix}_graph.png",
            dpi=180,
            bbox_inches="tight",
        )
        if sweep_figure is not None:
            sweep_figure.savefig(
                f"{prefix}_sweep.png",
                dpi=180,
                bbox_inches="tight",
            )

    if not args.no_show:
        plt.show()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
