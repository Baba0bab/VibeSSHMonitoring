// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace VibeSSHMonitoring.Core;

/// <summary>
/// Coffre des mots de passe (%APPDATA%\VibeSSHMonitoring\secrets.json), séparé de la config.
/// Trois modes au choix de l'utilisateur :
/// <list type="bullet">
/// <item>DPAPI : chiffré par Windows pour la session courante, rien à saisir ;</item>
/// <item>mot de passe maître : clé dérivée par Argon2id, contenu chiffré en AES-256-GCM ;</item>
/// <item>en clair.</item>
/// </list>
/// </summary>
public sealed class SecretVault
{
    private const int KeySize = 32;
    private const int Argon2MemoryKb = 64 * 1024;
    private const int Argon2Iterations = 3;
    private const int Argon2Parallelism = 4;
    // Ancien nom de l'application, conservé tel quel : le changer rendrait illisibles les coffres DPAPI existants
    private static readonly byte[] DpapiEntropy = "SshVibeMonitoring.v1"u8.ToArray();

    private Dictionary<Guid, ServerSecrets> _secrets = new();
    private byte[]? _key;      // clé dérivée du mot de passe maître, gardée tant que le coffre est ouvert
    private byte[]? _salt;
    private VaultFile? _lockedFile;

    public StorageMode Mode { get; private set; }
    public bool IsLocked { get; private set; }

    /// <summary>Mode mot de passe maître sans fichier : il faut d'abord choisir le mot de passe.</summary>
    public bool NeedsMasterPasswordSetup => Mode == StorageMode.MasterPassword && _lockedFile is null && _key is null;

    public event Action? LockStateChanged;

    private static string VaultPath => Path.Combine(AppConfig.ConfigDirectory, "secrets.json");

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public StorageMode Mode { get; set; }
        public Dictionary<Guid, ServerSecrets>? Secrets { get; set; }   // en clair
        public string? Data { get; set; }                               // DPAPI ou AES-GCM
        public string? Salt { get; set; }
        public string? Nonce { get; set; }
        public string? Tag { get; set; }
        public int MemoryKb { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
    }

    private SecretVault() { }

    /// <summary>
    /// Ouvre le coffre. Le mode du fichier fait foi ; sans fichier, on prend celui de la config.
    /// En mode mot de passe maître, le coffre reste verrouillé jusqu'à <see cref="Unlock"/>.
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
            try { File.Copy(VaultPath, VaultPath + ".corrompu", overwrite: true); } catch { }
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
                    // Autre utilisateur Windows ou profil réinstallé : les secrets sont perdus
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

    /// <summary>Déverrouille avec le mot de passe maître (≈ 0,5 s : à appeler hors du thread UI).</summary>
    public bool Unlock(string password)
    {
        if (!IsLocked)
            return true;
        if (_lockedFile is not { } file)
        {
            // Premier mot de passe : coffre neuf
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
            return false;   // mauvais mot de passe (tag GCM invalide)
        }

        _key = key;
        _salt = salt;
        // Paramètres d'un ancien fichier : on ré-enregistre avec les actuels au prochain Save
        if (file.MemoryKb != Argon2MemoryKb || file.Iterations != Argon2Iterations || file.Parallelism != Argon2Parallelism)
            _key = DeriveKey(password, _salt, Argon2MemoryKb, Argon2Iterations, Argon2Parallelism);
        _lockedFile = null;
        SetLocked(false);
        return true;
    }

    /// <summary>Oublie les secrets et la clé (mode mot de passe maître uniquement).</summary>
    public void Lock()
    {
        if (Mode != StorageMode.MasterPassword || IsLocked)
            return;
        Save();   // garantit que le fichier est à jour avant d'oublier la clé
        try
        {
            _lockedFile = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(VaultPath), AppConfig.JsonOptions);
        }
        catch
        {
            return;   // sans fichier relisible, verrouiller ferait perdre les secrets
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
            throw new InvalidOperationException("Le coffre est verrouillé.");
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

    /// <summary>Change le mode de stockage (coffre ouvert) et réécrit le fichier.</summary>
    public void ChangeMode(StorageMode mode, string? masterPassword = null)
    {
        if (IsLocked)
            throw new InvalidOperationException("Déverrouillez le coffre avant de changer de mode.");
        if (mode == StorageMode.MasterPassword)
        {
            if (string.IsNullOrEmpty(masterPassword))
                throw new ArgumentException("Mot de passe maître requis.", nameof(masterPassword));
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
