using System.Security.Cryptography;
using System.Text;

namespace Priory.Game;

public sealed class SaveCodec
{
    private readonly byte[] _secret;

    public SaveCodec(string secret)
    {
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public string MakeCode(string saveId)
    {
        using var hmac = new HMACSHA256(_secret);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(saveId));
        var shortSig = Convert.ToHexString(sig)[..8];
        var raw = saveId + shortSig;
        var groups = Enumerable.Range(0, (int)Math.Ceiling(raw.Length / 4d))
            .Select(i => raw.Substring(i * 4, Math.Min(4, raw.Length - i * 4)));
        return "BP-" + string.Join('-', groups);
    }

    public string VerifyCode(string code)
    {
        var compact = code.Trim().ToUpperInvariant().Replace("BP-", "").Replace("-", "");
        if (compact.Length < 18) throw new InvalidOperationException("Bad code");
        var saveId = compact[..10];
        var sig = compact[10..18];
        using var hmac = new HMACSHA256(_secret);
        var expect = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(saveId)))[..8];
        if (!sig.Equals(expect, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Signature mismatch");
        return saveId;
    }

    public string StateFingerprint(string stateJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stateJson));
        return Convert.ToHexString(hash)[..8];
    }
}
