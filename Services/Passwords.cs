using System.Security.Cryptography;

namespace Glance.Services;

public static class Passwords
{
    const string Lower = "abcdefghijklmnopqrstuvwxyz", Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Digits = "0123456789",
        Symbols = "!@#$%^&*()-_=+[]{};:,.?/~";
    const string LookAlikes = "Il1O0o";

    /// Cryptographically random; every selected class appears at least once.
    public static (string Password, double Bits) Generate(int length, bool upper, bool lower, bool digits, bool symbols, bool noLookAlikes)
    {
        var sets = new List<string>();
        if (lower) sets.Add(Lower);
        if (upper) sets.Add(Upper);
        if (digits) sets.Add(Digits);
        if (symbols) sets.Add(Symbols);
        if (sets.Count == 0) sets.Add(Lower);
        if (noLookAlikes) sets = sets.Select(s => string.Concat(s.Where(c => !LookAlikes.Contains(c)))).ToList();

        var pool = string.Concat(sets);
        var chars = RandomNumberGenerator.GetItems<char>(pool, length);
        for (var i = 0; i < sets.Count && i < length; i++)
            chars[i] = sets[i][RandomNumberGenerator.GetInt32(sets[i].Length)];
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return (new string(chars), length * Math.Log2(pool.Length));
    }
}
