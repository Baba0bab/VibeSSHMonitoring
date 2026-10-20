// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Password vault (%APPDATA%\VibeSSHMonitoring\secrets.json), separate from the configuration.
/// Three modes, chosen by the user:
/// <list type="bullet">
/// <item>DPAPI: encrypted by Windows for the current session, nothing to type;</item>
/// <item>master password: key derived with Argon2id, content encrypted with AES-256-GCM;</item>
/// <item>plain text.</item>
/// </list>
/// </summary>
public sealed class SecretVault
{
    private const int KeySize = 32;
    private const int Argon2MemoryKb = 64 * 1024;
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;
    // Former application name, kept as is: changing it would make existing DPAPI vaults unreadable
    private static readonly byte[] DpapiEntropy = "SshVibeMonitoring.v1"u8.ToArray();

    private Dictionary<Guid, ServerSecrets> _secrets = new();
    private byte[]? _key;      // key derived from the master password, kept while the vault is open
    private byte[]? _salt;
    private VaultFile? _lockedFile;

    public StorageMode Mode { get; private set; }
    public bool IsLocked { get; private set; }

    /// <summary>Master password mode without a file: the password must be chosen first.</summary>
    public bool NeedsMasterPasswordSetup => Mode == StorageMode.MasterPassword && _lockedFile is null && _key is null;

    public event Action? LockStateChanged;

    private static string VaultPath => Path.Combine(AppConfig.ConfigDirectory, "secrets.json");

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public StorageMode Mode { get; set; }
        public Dictionary<Guid, ServerSecrets>? Secrets { get; set; }   // plain text
        public string? Data { get; set; }                               // DPAPI or AES-GCM
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public int MemoryKb { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
    }

    private SecretVault() { }

    /// <summary>
    /// Opens the vault. The file's mode wins; without a file, the configured mode is used.
    /// In master password mode, the vault stays locked until <see cref="Unlock"/>.
    /// </summary>
    public static SecretVault Open(StorageMode configuredMode)
    {
        var vault = new SecretVault { Mode = configuredMode };
        VaultFile? file = null;
        try
        {
            if (File.Exists(VaultPath))
                file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(VaultPath), AppConfig.JsonOptions);
        }
        catch
        {
            try { File.Copy(VaultPath, VaultPath + ".corrupt", overwrite: true); } catch { }
        }

        if (file is null)
        {
            vault.IsLocked = configuredMode == StorageMode.MasterPassword;
            return vault;
        }

        vault.Mode = file.Mode;
        switch (file.Mode)
        {
            case StorageMode.PlainText:
                vault._secrets = file.Secrets ?? new();
                break;
            case StorageMode.Dpapi:
                try
                {
                    byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(file.Data ?? ""),
                        DpapiEntropy, DataProtectionScope.CurrentUser);
                    vault._secrets = Deserialize(plain);
                    CryptographicOperations.ZeroMemory(plain);
                }
                catch (CryptographicException)
                {
                    // Another Windows user or a reinstalled profile: the secrets are lost
                    vault._secrets = new();
                }
                break;
            case StorageMode.MasterPassword:
                vault._lockedFile = file;
                vault.IsLocked = true;
                break;
        }
        return vault;
    }

    /// <summary>Unlocks with the master password (≈ 0.5 s: call it off the UI thread).</summary>
    public bool Unlock(string password)
    {
        if (!IsLocked)
            return true;
        if (_lockedFile is not { } file)
        {
            // First password: brand new vault
            _salt = RandomNumberGenerator.GetBytes(16);
            _key = DeriveKey(password, _salt, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism);
            _secrets = new();
            SetLocked(false);
            Save();
            return true;
        }

        byte[] salt = Convert.FromBase64String(file.Salt ?? "");
        byte[] key = DeriveKey(password, salt, file.MemoryKb, file.Iterations, file.Parallelism);
        try
        {
            byte[] cipher = Convert.FromBase64String(file.Data ?? "");
            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(Convert.FromBase64String(file.Nonce ?? ""), cipher,
                    Convert.FromBase64String(file.Tag ?? ""), plain);
            _secrets = Deserialize(plain);
            CryptographicOperations.ZeroMemory(plain);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(key);
            return false;   // wrong password (invalid GCM tag)
        }

        _key = key;
        _salt = salt;
        // File written with older parameters: saved again with the current ones on the next Save
        if (file.MemoryKb != Argon2MemoryKb || file.Iterations != Argon2Iterations || file.Parallelism != Argon2Parallelism)
            _key = DeriveKey(password, _salt, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism);
        _lockedFile = null;
        SetLocked(false);
        return true;
    }

    /// <summary>Forgets the secrets and the key (master password mode only).</summary>
    public void Lock()
    {
        if (Mode != StorageMode.MasterPassword || IsLocked)
            return;
        Save();   // make sure the file is up to date before forgetting the key
        try
        {
            _lockedFile = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(VaultPath), AppConfig.JsonOptions);
        }
        catch
        {
            return;   // without a readable file, locking would lose the secrets
        }
        _secrets = new();
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _key = null;
        SetLocked(true);
    }

    private void SetLocked(bool locked)
    {
        IsLocked = locked;
        LockStateChanged?.Invoke();
    }

    public ServerSecrets Get(Guid serverId) =>
        !IsLocked && _secrets.TryGetValue(serverId, out var s) ? s.Clone() : new ServerSecrets();

    public void Set(Guid serverId, ServerSecrets secrets)
    {
        if (IsLocked)
            throw new InvalidOperationException(Loc.T("Vault.IsLocked"));
        if (secrets.IsEmpty)
            _secrets.Remove(serverId);
        else
            _secrets[serverId] = secrets.Clone();
        Save();
    }

    public void Remove(Guid serverId)
    {
        if (!IsLocked && _secrets.Remove(serverId))
            Save();
    }

    /// <summary>Changes the storage mode (vault open) and rewrites the file.</summary>
    public void ChangeMode(StorageMode mode, string? masterPassword = null)
    {
        if (IsLocked)
            throw new InvalidOperationException(Loc.T("Vault.UnlockBeforeModeChange"));
        if (mode == StorageMode.MasterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword))
                throw new ArgumentException(Loc.T("Vault.MasterPasswordRequired"), nameof(masterPassword));
            _salt = RandomNumberGenerator.GetBytes(16);
            _key = DeriveKey(masterPassword, _salt, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism);
        }
        else
        {
            if (_key is not null)
                CryptographicOperations.ZeroMemory(_key);
            _key = null;
            _salt = null;
        }
        Mode = mode;
        Save();
        LockStateChanged?.Invoke();
    }

    public void Save()
    {
        if (IsLocked)
            return;
        var file = new VaultFile { Mode = Mode };
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(_secrets, AppConfig.JsonOptions);
        try
        {
            switch (Mode)
            {
                case StorageMode.PlainText:
                    file.Secrets = _secrets;
                    break;
                case StorageMode.Dpapi:
                    file.Data = Convert.ToBase64String(
                        ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser));
                    break;
                case StorageMode.MasterPassword:
                    if (_key is null || _salt is null)
                        return;
                    byte[] nonce = RandomNumberGenerator.GetBytes(12);
                    byte[] cipher = new byte[plain.Length];
                    byte[] tag = new byte[16];
                    using (var aes = new AesGcm(_key, 16))
                        aes.Encrypt(nonce, plain, cipher, tag);
                    file.Data = Convert.ToBase64String(cipher);
                    file.Nonce = Convert.ToBase64String(nonce);
                    file.Tag = Convert.ToBase64String(tag);
                    file.Salt = Convert.ToBase64String(_salt);
                    file.MemoryKb = Argon2MemoryKb;
                    file.Iterations = Argon2Iterations;
                    file.Parallelism = Argon2Parallelism;
                    break;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        string temp = VaultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file, AppConfig.JsonOptions));
        File.Move(temp, VaultPath, overwrite: true);
    }

    private static Dictionary<Guid, ServerSecrets> Deserialize(byte[] utf8Json) =>
        JsonSerializer.Deserialize<Dictionary<Guid, ServerSecrets>>(utf8Json, AppConfig.JsonOptions) ?? new();

    private static byte[] DeriveKey(string password, byte[] salt, int memoryKb, int iterations, int parallelism)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memoryKb,
                Iterations = iterations,
                DegreeOfParallelism = parallelism
            };
            return argon.GetBytes(KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
