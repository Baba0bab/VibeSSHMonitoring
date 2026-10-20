// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.IO;

namespace VibeSSHMonitoring.Core;

/// <summary>Generation (Windows ssh-keygen) and deployment of an ed25519 SSH key.</summary>
public static class SshKeys
{
    public static string DefaultKeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");

    public static bool HasKeyPair(string path) => File.Exists(path) && File.Exists(path + ".pub");

    public static async Task GenerateAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string keygen = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");
        if (!File.Exists(keygen))
            keygen = "ssh-keygen.exe";

        var psi = new ProcessStartInfo(keygen)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-t", "ed25519", "-N", "", "-C", $"{Environment.UserName}@{Environment.MachineName}", "-f", path })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Impossible de lancer ssh-keygen.");
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !HasKeyPair(path))
            throw new InvalidOperationException(Loc.T("Keys.KeygenFailed", error.Trim()));
    }

    public static string ReadPublicKey(string path) => File.ReadAllText(path + ".pub").Trim();

    /// <summary>
    /// Adds the public key (read from standard input, never from the command line) to
    /// ~/.ssh/authorized_keys when it is not there yet.
    /// </summary>
    public const string InstallScript =
        "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && IFS= read -r K && " +
        "{ grep -qxF \"$K\" ~/.ssh/authorized_keys || printf '%s\\n' \"$K\" >> ~/.ssh/authorized_keys; } && echo OK";
}
