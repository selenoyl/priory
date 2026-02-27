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
        => MakeSignedCode("BP", saveId, 10);

    public string VerifyCode(string code)
        => VerifySignedCode("BP", code, 10);

    public string MakePartyCode(string partyId)
        => MakeSignedCode("PT", partyId, 12);

    public string VerifyPartyCode(string code)
        => VerifySignedCode("PT", code, 12);

    private string MakeSignedCode(string prefix, string value, int expectedLength)
    {
        if (value.Length != expectedLength) throw new InvalidOperationException("Unexpected id length");
        using var hmac = new HMACSHA256(_secret);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
        var shortSig = Convert.ToHexString(sig)[..8];
        var raw = value + shortSig;
        var groups = Enumerable.Range(0, (int)Math.Ceiling(raw.Length / 4d))
            .Select(i => raw.Substring(i * 4, Math.Min(4, raw.Length - i * 4)));
        return prefix + "-" + string.Join('-', groups);
    }

    private string VerifySignedCode(string prefix, string code, int expectedLength)
    {
        var compact = code.Trim().ToUpperInvariant().Replace(prefix + "-", "").Replace("-", "");
        if (compact.Length < expectedLength + 8) throw new InvalidOperationException("Bad code");
        var id = compact[..expectedLength];
        var sig = compact[expectedLength..(expectedLength + 8)];
        using var hmac = new HMACSHA256(_secret);
        var expect = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(id)))[..8];
        if (!sig.Equals(expect, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Signature mismatch");
        return id;
    }

    public string StateFingerprint(string stateJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stateJson));
        return Convert.ToHexString(hash)[..8];
    }
}
