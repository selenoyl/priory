using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Priory.Game;

public sealed class AccountRepository
{
    private readonly string _accountsPath;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AccountRepository(string saveRoot)
    {
        Directory.CreateDirectory(saveRoot);
        _accountsPath = Path.Combine(saveRoot, "accounts.json");
    }

    public bool Register(string username, string password, string? displayName, out string message)
    {
        var normalized = Normalize(username);
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length < 3)
        {
            message = "Username must be at least 3 characters.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            message = "Password must be at least 8 characters.";
            return false;
        }

        lock (_sync)
        {
            var accounts = LoadUnsafe();
            if (accounts.ContainsKey(normalized))
            {
                message = "That username is already taken.";
                return false;
            }

            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = HashPassword(password, salt);
            accounts[normalized] = new AccountRecord
            {
                Username = normalized,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
                PasswordSalt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(hash),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            SaveUnsafe(accounts);
        }

        message = "Account registered. Friendly reminder: this system is for game access only, so please use credentials you do not reuse on everyday services.";
        return true;
    }

    public bool TryAuthenticate(string username, string password, out AccountRecord? account, out string message)
    {
        var normalized = Normalize(username);
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            account = ResolveAccount(accounts, normalized);
            if (account is null)
            {
                message = "Unknown username.";
                return false;
            }

            var salt = Convert.FromBase64String(account.PasswordSalt);
            var hash = HashPassword(password, salt);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(account.PasswordHash)))
            {
                message = "Invalid password.";
                account = null;
                return false;
            }

            message = "Authenticated.";
            return true;
        }
    }


    public bool TryGetAccount(string username, out AccountRecord? account)
    {
        var normalized = Normalize(username);
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            account = ResolveAccount(accounts, normalized);
            return account is not null;
        }
    }

    public void UpdateLastSaveCode(string username, string saveCode)
    {
        var normalized = Normalize(username);
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            if (!accounts.TryGetValue(normalized, out var account)) return;
            account.LastSaveCode = saveCode;
            account.LastSeenUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(accounts);
        }
    }

    public void UpdateLastPartyCode(string username, string? partyCode)
    {
        var normalized = Normalize(username);
        lock (_sync)
        {
            var accounts = LoadUnsafe();
            if (!accounts.TryGetValue(normalized, out var account)) return;
            account.LastPartyCode = partyCode;
            account.LastSeenUtc = DateTimeOffset.UtcNow;
            SaveUnsafe(accounts);
        }
    }


    private static AccountRecord? ResolveAccount(Dictionary<string, AccountRecord> accounts, string normalized)
    {
        if (accounts.TryGetValue(normalized, out var direct))
            return direct;

        return accounts.Values.FirstOrDefault(a => Normalize(a.DisplayName) == normalized);
    }

    private Dictionary<string, AccountRecord> LoadUnsafe()
    {
        if (!File.Exists(_accountsPath)) return new(StringComparer.OrdinalIgnoreCase);
        return JsonSerializer.Deserialize<Dictionary<string, AccountRecord>>(File.ReadAllText(_accountsPath))
            ?? new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveUnsafe(Dictionary<string, AccountRecord> accounts)
        => File.WriteAllText(_accountsPath, JsonSerializer.Serialize(accounts, _jsonOptions));

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();

    private static byte[] HashPassword(string password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);
}

public sealed class AccountRecord
{
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string? LastSaveCode { get; set; }
    public string? LastPartyCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
}
