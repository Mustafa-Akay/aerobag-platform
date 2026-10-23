using System.Text.RegularExpressions;
using AeroBag.Domain;

namespace AeroBag.Infrastructure;

/// <summary>Veritabanında durumlar CHECKED_IN gibi UPPER_SNAKE_CASE saklanır.</summary>
public static class StateNames
{
    public static string ToDb(BagState state) =>
        Regex.Replace(state.ToString(), "(?<!^)([A-Z])", "_$1").ToUpperInvariant();

    public static BagState? FromDb(string? value) =>
        value is null ? null : Enum.GetValues<BagState>().First(s => ToDb(s) == value);
}
