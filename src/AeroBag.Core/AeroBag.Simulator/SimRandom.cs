namespace AeroBag.Simulator;

/// <summary>Tohumlu rastgele sayı üreteci; aynı tohum her zaman aynı veriyi üretir.</summary>
public sealed class SimRandom(int seed)
{
    private readonly Random _r = new(seed);

    public double Uniform(double min, double max) => min + (max - min) * _r.NextDouble();

    public int Int(int minInclusive, int maxExclusive) => _r.Next(minInclusive, maxExclusive);

    public bool Chance(double probability) => _r.NextDouble() < probability;

    public double Exp(double mean) => -mean * Math.Log(1.0 - _r.NextDouble());

    public double Normal(double mean, double sd)
    {
        var u1 = 1.0 - _r.NextDouble();
        var u2 = _r.NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Ortalaması <paramref name="mean"/> olan log-normal dağılımdan örnek.</summary>
    public double LogNormal(double mean, double sigma)
    {
        var mu = Math.Log(mean) - sigma * sigma / 2.0;
        return Math.Exp(Normal(mu, sigma));
    }

    public T Pick<T>(IReadOnlyList<T> items) => items[_r.Next(items.Count)];
}
