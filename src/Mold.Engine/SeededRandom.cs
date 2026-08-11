using System.Globalization;

namespace Mold.Engine;

public static class SeededRandom
{
    public static ulong FromHexSeed(string seed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);

        if (seed.Length != 8 ||
            !uint.TryParse(seed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException("Seed must contain exactly eight hexadecimal characters.", nameof(seed));
        }

        return value == 0 ? 0x9E3779B97F4A7C15UL : value;
    }

    public static (ulong State, int Value) Next(ulong state, int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMax);

        var next = state;
        next ^= next >> 12;
        next ^= next << 25;
        next ^= next >> 27;
        var output = next * 2685821657736338717UL;
        return (next, (int)(output % (ulong)exclusiveMax));
    }
}
