using System.Text.Json;

namespace Priory.Game;

public sealed class PartyRepository
{
    private readonly string _partyRoot;
    private readonly SaveCodec _codec;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public PartyRepository(string saveRoot, SaveCodec codec)
    {
        _partyRoot = Path.Combine(saveRoot, "parties");
        Directory.CreateDirectory(_partyRoot);
        _codec = codec;
    }

    public (PartyState Party, string PartyCode) CreateParty()
    {
        var partyId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6));
        var party = new PartyState { PartyId = partyId };
        Save(party);
        return (party, _codec.MakePartyCode(partyId));
    }

    public bool TryLoadByCode(string partyCode, out PartyState? party, out string message)
    {
        try
        {
            var partyId = _codec.VerifyPartyCode(partyCode);
            if (!TryLoadById(partyId, out party))
            {
                message = "Party code verified, but no party file was found.";
                return false;
            }

            message = "Joined party.";
            return true;
        }
        catch
        {
            party = null;
            message = "Could not verify party code.";
            return false;
        }
    }

    public bool TryLoadById(string partyId, out PartyState? party)
    {
        var path = Path.Combine(_partyRoot, partyId + ".json");
        if (!File.Exists(path))
        {
            party = null;
            return false;
        }

        party = JsonSerializer.Deserialize<PartyState>(File.ReadAllText(path));
        return party is not null;
    }

    public void Save(PartyState party)
    {
        var path = Path.Combine(_partyRoot, party.PartyId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(party, _json));
    }
}
