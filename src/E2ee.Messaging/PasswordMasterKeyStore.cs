// A master key locked with the user's password. The password itself is never stored:
// Argon2id stretches it (with a random salt) into a key that encrypts the master key
// with XChaCha20-Poly1305. A wrong password fails the authentication tag.

using System.Security.Cryptography;
using System.Text.Json;
using NSec.Cryptography;

namespace E2ee.Messaging;

/// <summary>The password didn't unlock the profile.</summary>
public sealed class WrongPasswordException() : MessengerException("That password is incorrect.");

/// <summary>Argon2id cost. Stored with each profile, so it can be raised later without breaking old ones.</summary>
public sealed record PasswordCost(int MemoryKiB, int Passes)
{
    /// <summary>64 MiB, 3 passes: about half a second on a laptop, expensive to guess at scale.</summary>
    public static readonly PasswordCost Default = new(64 * 1024, 3);

    /// <summary>Cheap settings for tests only.</summary>
    public static readonly PasswordCost ForTests = new(8 * 1024, 1);
}

public sealed class PasswordMasterKeyStore : IMasterKeyStore
{
    public const string FileName = "master.key.locked";
    public const int MinPasswordLength = 8;
    private const string Ad = "COSC483-E2EE-password-lock-v1";

    private readonly string _path;
    private readonly string _password;
    private readonly PasswordCost _cost;

    public PasswordMasterKeyStore(string dataDirectory, string password, PasswordCost? cost = null)
    {
        _path = Path.Combine(dataDirectory, FileName);
        _password = password;
        _cost = cost ?? PasswordCost.Default;
    }

    /// <summary>True when the profile in <paramref name="dataDirectory"/> is locked with a password.</summary>
    public static bool IsLocked(string dataDirectory) => File.Exists(Path.Combine(dataDirectory, FileName));

    /// <summary>Why a new password can't be used, or null if it's fine.</summary>
    public static string? CheckNewPassword(string password, string confirmation)
    {
        if (password.Length < MinPasswordLength) return $"Use at least {MinPasswordLength} characters.";
        if (password != confirmation) return "The passwords don't match.";
        return null;
    }

    private sealed record Locked(int Version, int MemoryKiB, int Passes, byte[] Salt, byte[] Nonce, byte[] Ciphertext);

    public byte[] GetOrCreate()
    {
        if (File.Exists(_path))
        {
            var locked = JsonSerializer.Deserialize<Locked>(File.ReadAllBytes(_path))
                ?? throw new MessengerException("The profile's lock file is damaged.");
            using var key = Derive(locked.Salt, new PasswordCost(locked.MemoryKiB, locked.Passes));
            return AeadAlgorithm.XChaCha20Poly1305.Decrypt(key, locked.Nonce, System.Text.Encoding.UTF8.GetBytes(Ad), locked.Ciphertext)
                ?? throw new WrongPasswordException();
        }

        var master = MasterKeyStore.NewKey();
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AeadAlgorithm.XChaCha20Poly1305.NonceSize);
        using (var key = Derive(salt, _cost))
        {
            var ciphertext = AeadAlgorithm.XChaCha20Poly1305.Encrypt(key, nonce, System.Text.Encoding.UTF8.GetBytes(Ad), master);
            var json = JsonSerializer.SerializeToUtf8Bytes(new Locked(1, _cost.MemoryKiB, _cost.Passes, salt, nonce, ciphertext));
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, json);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tmp, _path, overwrite: false);
        }
        return master;
    }

    private Key Derive(byte[] salt, PasswordCost cost)
    {
        var kdf = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = 1,
            MemorySize = cost.MemoryKiB,
            NumberOfPasses = cost.Passes,
        });
        return kdf.DeriveKey(_password, salt, AeadAlgorithm.XChaCha20Poly1305);
    }
}
