// Where the profile's 32-byte master key lives. The master key encrypts private keys,
// ratchet state, and message history in the local database (see StateCipher).

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace E2ee.Messaging;

public interface IMasterKeyStore
{
    /// <summary>Returns the profile's master key, creating and storing one on first use.</summary>
    byte[] GetOrCreate();

    /// <summary>Forgets the key (on log out). Keys kept in the profile directory go with it.</summary>
    void Delete() { }
}

public static class MasterKeyStore
{
    /// <summary>macOS Keychain, Windows DPAPI, or (elsewhere) a key file readable only by the user.</summary>
    public static IMasterKeyStore ForCurrentPlatform(string dataDirectory)
    {
        if (OperatingSystem.IsMacOS()) return new MacKeychainMasterKeyStore(dataDirectory);
        if (OperatingSystem.IsWindows()) return new DpapiMasterKeyStore(dataDirectory);
        return new FileMasterKeyStore(dataDirectory);
    }

    internal static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);
}

/// <summary>
/// Stores the key as a generic password in the user's login keychain, one entry per
/// profile directory. Uses /usr/bin/security; note that the key is passed to it as a
/// command-line argument once, when it is first created.
/// </summary>
public sealed class MacKeychainMasterKeyStore(string dataDirectory) : IMasterKeyStore
{
    private const string Service = "E2ee Messenger";
    private readonly string _account = "profile-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))))[..16].ToLowerInvariant();

    public byte[] GetOrCreate()
    {
        var (code, output) = Run("find-generic-password", "-s", Service, "-a", _account, "-w");
        if (code == 0) return Convert.FromHexString(output.Trim());
        if (code != 44) throw new InvalidOperationException($"Keychain lookup failed (security exit code {code}).");
        var key = MasterKeyStore.NewKey();
        var (addCode, _) = Run("add-generic-password", "-s", Service, "-a", _account, "-w", Convert.ToHexString(key));
        if (addCode != 0) throw new InvalidOperationException($"Could not save the master key to the Keychain (security exit code {addCode}).");
        return key;
    }

    public void Delete()
    {
        var (code, _) = Run("delete-generic-password", "-s", Service, "-a", _account);
        if (code is not (0 or 44)) throw new InvalidOperationException($"Could not remove the master key from the Keychain (security exit code {code}).");
    }

    private static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }
}

/// <summary>Stores the key in a file encrypted with Windows DPAPI for the current user.</summary>
public sealed class DpapiMasterKeyStore(string dataDirectory) : IMasterKeyStore
{
    private static readonly byte[] Entropy = "COSC483-E2EE-master-key"u8.ToArray();
    private readonly string _path = Path.Combine(dataDirectory, "master.key.dpapi");

    public byte[] GetOrCreate()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is Windows-only.");
        if (File.Exists(_path)) return ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
        var key = MasterKeyStore.NewKey();
        File.WriteAllBytes(_path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }
}

/// <summary>
/// Weakest option: a plain key file with owner-only permissions next to the database.
/// It protects against copying the database alone, not against someone who can read
/// the user's files. Used on Linux and when requested explicitly.
/// </summary>
public sealed class FileMasterKeyStore(string dataDirectory) : IMasterKeyStore
{
    private readonly string _path = Path.Combine(dataDirectory, "master.key");

    public byte[] GetOrCreate()
    {
        if (File.Exists(_path)) return File.ReadAllBytes(_path);
        var key = MasterKeyStore.NewKey();
        using (var f = new FileStream(_path, FileMode.CreateNew, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            f.Write(key);
        }
        return key;
    }
}

/// <summary>For tests: keeps keys in memory, shared across instances by key name.</summary>
public sealed class InMemoryMasterKeyStore(string name) : IMasterKeyStore
{
    private static readonly Dictionary<string, byte[]> Keys = new();

    public byte[] GetOrCreate()
    {
        lock (Keys)
        {
            if (!Keys.TryGetValue(name, out var key)) Keys[name] = key = MasterKeyStore.NewKey();
            return key;
        }
    }

    public void Delete()
    {
        lock (Keys) Keys.Remove(name);
    }
}
