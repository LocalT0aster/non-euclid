using System;

/// Deterministic PCG32 random source used by procedural generation.
public sealed class DeterministicRandom
{
	private ulong _state;
	private readonly ulong _increment;

	/// Creates a reproducible stream from an explicit seed and stream selector.
	public DeterministicRandom(ulong seed, ulong stream = 0xDA3E39CB94B95BDBUL)
	{
		_increment = (stream << 1) | 1UL;
		_state = 0UL;
		NextUInt();
		_state += seed;
		NextUInt();
	}

	/// Returns the next uniformly distributed 32-bit value.
	public uint NextUInt()
	{
		ulong oldState = _state;
		_state =
			unchecked(oldState * 6364136223846793005UL + _increment);

		uint xorShifted = (uint)(((oldState >> 18) ^ oldState) >> 27);
		int rotation = (int)(oldState >> 59);

		return
			(xorShifted >> rotation) |
			(xorShifted << ((-rotation) & 31));
	}

	/// Returns an integer in [0, maxExclusive) without modulo bias.
	public int NextInt(int maxExclusive)
	{
		if (maxExclusive <= 0)
			throw new ArgumentOutOfRangeException(nameof(maxExclusive));

		uint bound = (uint)maxExclusive;
		uint threshold = unchecked(0u - bound) % bound;

		while (true)
		{
			uint value = NextUInt();
			if (value >= threshold)
				return (int)(value % bound);
		}
	}

	/// Returns an integer in [minInclusive, maxExclusive).
	public int NextInt(int minInclusive, int maxExclusive)
	{
		if (maxExclusive <= minInclusive)
			throw new ArgumentOutOfRangeException(nameof(maxExclusive));

		return minInclusive + NextInt(maxExclusive - minInclusive);
	}

	/// Returns a double in [0, 1).
	public double NextDouble()
		=> NextUInt() / ((double)uint.MaxValue + 1.0);

	/// Returns true with the requested probability.
	public bool Chance(double probability)
	{
		if (probability <= 0.0)
			return false;
		if (probability >= 1.0)
			return true;

		return NextDouble() < probability;
	}

	/// Shuffles values in place using this deterministic stream.
	public void Shuffle<T>(Span<T> values)
	{
		for (int i = values.Length - 1; i > 0; i--)
		{
			int j = NextInt(i + 1);
			(values[i], values[j]) = (values[j], values[i]);
		}
	}
}

/// Derives stable independent seeds without relying on runtime hash codes.
public static class SeedMixer
{
	/// Mixes a base seed with deterministic domain-specific values.
	public static ulong Mix(ulong seed, params ulong[] values)
	{
		ulong result = Avalanche(seed ^ 0x9E3779B97F4A7C15UL);

		foreach (ulong value in values)
		{
			result ^= Avalanche(value + 0x9E3779B97F4A7C15UL);
			result = Avalanche(result);
		}

		return result;
	}

	private static ulong Avalanche(ulong value)
	{
		value ^= value >> 30;
		value = unchecked(value * 0xBF58476D1CE4E5B9UL);
		value ^= value >> 27;
		value = unchecked(value * 0x94D049BB133111EBUL);
		value ^= value >> 31;
		return value;
	}
}
