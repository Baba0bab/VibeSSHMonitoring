// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Baba0bab — Vibe SSH Monitoring

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using VibeSSHMonitoring.Core;
using VibeSSHMonitoring.UI;
using Xunit;

namespace VibeSSHMonitoring.Tests;

/// <summary>Throwaway configuration folder, set before the first access to AppConfig.</summary>
public static class TestHome
{
    public static readonly string Path = CreateHome();

    private static string CreateHome()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "svm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("VIBESSHMONITORING_HOME", dir);
        return dir;
    }

    public static void Reset()
    {
        _ = Path;
        foreach (var file in Directory.GetFiles(Path))
            File.Delete(file);
    }
}

[Collection("vault")]
public class VaultTests
{
    private static readonly Guid Server = Guid.NewGuid();

    public VaultTests() => TestHome.Reset();

    private static string VaultFile => Path.Combine(TestHome.Path, "secrets.json");

    [Fact]
    public void MasterPassword_RoundTrip_WrongPassword_And_Lock()
    {
        var vault = SecretVault.Open(StorageMode.MasterPassword);
        Assert.True(vault.IsLocked);
        Assert.True(vault.NeedsMasterPasswordSetup);
        Assert.True(vault.Unlock("correct horse"));
        vault.Set(Server, new ServerSecrets { Password = "s3cret-pw", ElevationPassword = "root-pw" });

        Assert.DoesNotContain("s3cret-pw", File.ReadAllText(VaultFile));
        vault.Lock();
        Assert.True(vault.IsLocked);
        Assert.Null(vault.Get(Server).Password);

        var reopened = SecretVault.Open(StorageMode.Dpapi);   // the file's mode wins
        Assert.Equal(StorageMode.MasterPassword, reopened.Mode);
        Assert.True(reopened.IsLocked);
        Assert.False(reopened.Unlock("wrong"));
        Assert.True(reopened.IsLocked);
        Assert.True(reopened.Unlock("correct horse"));
        Assert.Equal("s3cret-pw", reopened.Get(Server).Password);
        Assert.Equal("root-pw", reopened.Get(Server).ElevationPassword);
    }

    [Fact]
    public void ChangeMode_ReencryptsAndKeepsSecrets()
    {
        var vault = SecretVault.Open(StorageMode.Dpapi);
        Assert.False(vault.IsLocked);
        vault.Set(Server, new ServerSecrets { Password = "dpapi-pw" });
        Assert.DoesNotContain("dpapi-pw", File.ReadAllText(VaultFile));
        Assert.Equal("dpapi-pw", SecretVault.Open(StorageMode.Dpapi).Get(Server).Password);

        vault.ChangeMode(StorageMode.PlainText);
        Assert.Contains("dpapi-pw", File.ReadAllText(VaultFile));
        Assert.Equal("dpapi-pw", SecretVault.Open(StorageMode.Dpapi).Get(Server).Password);

        vault.ChangeMode(StorageMode.MasterPassword, "another pass");
        Assert.DoesNotContain("dpapi-pw", File.ReadAllText(VaultFile));
        var locked = SecretVault.Open(StorageMode.Dpapi);
        Assert.True(locked.Unlock("another pass"));
        Assert.Equal("dpapi-pw", locked.Get(Server).Password);

        locked.ChangeMode(StorageMode.Dpapi);
        Assert.Equal("dpapi-pw", SecretVault.Open(StorageMode.Dpapi).Get(Server).Password);
    }

    [Fact]
    public void EmptySecretsAreRemoved()
    {
        var vault = SecretVault.Open(StorageMode.PlainText);
        vault.Set(Server, new ServerSecrets { Password = "x" });
        vault.Set(Server, new ServerSecrets());
        Assert.Null(SecretVault.Open(StorageMode.PlainText).Get(Server).Password);
    }
}

public class ParserTests
{
    [Fact]
    public void ParsePackage_ReadsAptListLine()
    {
        var p = RemoteCommands.ParsePackage("openssl/jammy-updates,jammy-security 3.0.2-0ubuntu1.15 amd64 [upgradable from: 3.0.2-0ubuntu1.14]");
        Assert.NotNull(p);
        Assert.Equal("openssl", p!.Name);
        Assert.Equal("3.0.2-0ubuntu1.15", p.NewVersion);
        Assert.Equal("3.0.2-0ubuntu1.14", p.OldVersion);
        Assert.Equal("amd64", p.Arch);
        Assert.True(p.IsSecurity);

        var debian = RemoteCommands.ParsePackage("tzdata/stable-updates 2024a-0+deb12u1 all [upgradable from: 2023c-5+deb12u1]");
        Assert.False(debian!.IsSecurity);
        Assert.Null(RemoteCommands.ParsePackage("Listing... Done"));
    }

    [Fact]
    public void ParseProbe_ReadsAllFields()
    {
        string output = string.Join("\n",
            "hostname=web-01",
            "os=Debian GNU/Linux 12 (bookworm)",
            "kernel=6.1.0-18-amd64",
            "arch=x86_64",
            "uptime=93784.12",
            "load=0.42 0.30 0.25",
            "cpus=4",
            "cpu1=cpu  1000 0 500 8000 100 0 0 0 0 0",
            "cpu2=cpu  1100 0 550 8300 150 0 0 0 0 0",
            "mem_MemTotal=8000000",
            "mem_MemAvailable=6000000",
            "mem_SwapTotal=0",
            "mem_SwapFree=0",
            "disk=80000000 20000000",
            "reboot=1",
            "apt=1",
            "pkg=openssl/bookworm-security 3.0.13-1~deb12u1 amd64 [upgradable from: 3.0.11-1~deb12u2]",
            "pkg=curl/stable 7.88.1-10+deb12u5 amd64 [upgradable from: 7.88.1-10+deb12u4]");
        var r = RemoteCommands.ParseProbe(output, updates: true);

        Assert.Equal("web-01", r.Hostname);
        Assert.Equal("Debian GNU/Linux 12 (bookworm)", r.Os);
        Assert.Equal(TimeSpan.FromSeconds(93784.12), r.Uptime);
        Assert.Equal(4, r.Cpus);
        // total = 500 jiffies, idle+iowait = 350 → 30% busy
        Assert.Equal(30, r.CpuPercent!.Value, 1);
        Assert.Equal(8000000, r.MemTotalKb);
        Assert.True(r.RebootRequired);
        Assert.Equal(2, r.Packages!.Count);
        Assert.Single(r.Packages, p => p.IsSecurity);
    }

    [Fact]
    public void ParseProbe_WithoutApt_HasNoPackageList()
    {
        var r = RemoteCommands.ParseProbe("hostname=x\napt=0\n", updates: true);
        Assert.False(r.HasApt);
        Assert.Null(r.Packages);
    }

    [Fact]
    public void LogBuffer_CarriageReturnRewritesLine()
    {
        var log = new LogBuffer();
        log.Append("Reading package lists... 0%\r");
        log.Append("Reading package lists... 50%\r");
        log.Append("Reading package lists... Done\r\n");
        log.Append("\u001b[1mBuilding\u001b[0m dependency tree\n");
        Assert.Equal("Reading package lists... Done\nBuilding dependency tree\n", log.Text);
    }

    [Fact]
    public void EffectiveSettings_ServerOverridesGlobal()
    {
        var global = ServerSettings.CreateDefaults();
        var server = new ServerSettings { ShowResources = false, Terminal = TerminalMode.PowerShell };
        var e = EffectiveSettings.Resolve(global, server);
        Assert.False(e.ShowResources);
        Assert.True(e.ShowSystem);
        Assert.Equal(TerminalMode.PowerShell, e.Terminal);
        Assert.Equal(global.RefreshSeconds, e.RefreshSeconds);
    }

    [Fact]
    public void ElevationSecret_DependsOnMethod()
    {
        var secrets = new ServerSecrets { Password = "login", ElevationPassword = "root" };
        Assert.Equal("login", secrets.ElevationSecretFor(new ServerConfig { Username = "bob", Elevation = ElevationMethod.Sudo }));
        Assert.Equal("root", secrets.ElevationSecretFor(new ServerConfig { Username = "bob", Elevation = ElevationMethod.Sudo, SudoSamePassword = false }));
        Assert.Equal("root", secrets.ElevationSecretFor(new ServerConfig { Username = "bob", Elevation = ElevationMethod.Su }));
        Assert.Null(secrets.ElevationSecretFor(new ServerConfig { Username = "root", Elevation = ElevationMethod.Su }));
    }

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "'it'\"'\"'s'")]
    public void Quote_EscapesSingleQuotes(string input, string expected) =>
        Assert.Equal(expected, RemoteExec.Quote(input));

    [Fact]
    public void SshArguments_ForKeyAndRootShell()
    {
        var server = new ServerConfig
        {
            Host = "203.0.113.5", Port = 2222, Username = "admin", Auth = AuthMethod.PrivateKey,
            KeyPath = @"C:\keys\id", Elevation = ElevationMethod.Su
        };
        var args = ExternalTerminal.SshArguments(server, rootShell: true);
        Assert.Equal(new[] { "-p", "2222", "-o", "ServerAliveInterval=30", "-i", @"C:\keys\id", "-t", "admin@203.0.113.5", "su -" }, args);
    }
}

/// <summary>
/// The scripts sent to servers run through sh: their syntax (and the whole probe) is checked
/// with the sh of Git for Windows, when it is installed.
/// </summary>
public class ShellScriptTests
{
    private static readonly string? Sh = new[]
    {
        @"C:\Program Files\Git\usr\bin\sh.exe",
        @"C:\Program Files\Git\bin\sh.exe"
    }.FirstOrDefault(File.Exists);

    private static (int Code, string Output) RunSh(params string[] args)
    {
        var psi = new ProcessStartInfo(Sh!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    public static IEnumerable<object[]> Scripts() => new[]
    {
        new object[] { RemoteCommands.BuildProbe(true, true, true) },
        new object[] { RemoteCommands.AptUpdate },
        new object[] { RemoteCommands.AptUpgrade(UpgradeKind.FullUpgrade, autoRemove: true) },
        new object[] { RemoteCommands.Reboot },
        new object[] { SshKeys.InstallScript },
        // Actual nesting used for sudo and su
        new object[] { "LC_ALL=C sudo -S -p '' -- sh -c " + RemoteExec.Quote("exec </dev/null; " + RemoteCommands.AptUpgrade(UpgradeKind.Upgrade, false)) },
        new object[] { "LC_ALL=C exec su - root -s /bin/sh -c " + RemoteExec.Quote("exec </dev/null; " + RemoteCommands.BuildProbe(true, true, true) + "\nrc=$?; echo; echo \"__SVM_RC_${rc}__\"") }
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Script_HasValidSyntax(string script)
    {
        if (Sh is null)
            return;
        var (code, output) = RunSh("-n", "-c", script);
        Assert.True(code == 0, output);
    }

    [Fact]
    public void Probe_RunsAndParses()
    {
        if (Sh is null)
            return;
        // sh started outside Git Bash: /usr/bin (awk, sed…) is not in the PATH
        var (_, output) = RunSh("-c", "PATH=/usr/bin:$PATH\n" + RemoteCommands.BuildProbe(true, true, true));
        var r = RemoteCommands.ParseProbe(output, updates: true);
        Assert.NotNull(r.Hostname);
        Assert.NotNull(r.MemTotalKb);      // /proc/meminfo emulated by MSYS
        Assert.NotNull(r.MemAvailableKb);  // estimate without MemAvailable
        Assert.NotNull(r.CpuPercent);
        Assert.False(r.HasApt);
    }

    [Fact]
    public void QuotedPayload_SurvivesShell()
    {
        if (Sh is null)
            return;
        string tricky = "echo \"a b\" 'c' $HOME `x` ; done";
        var (_, output) = RunSh("-c", "printf '%s' " + RemoteExec.Quote(tricky));
        Assert.Equal(tricky, output);
    }
}

/// <summary>Every language must have exactly the English keys, with the same placeholders.</summary>
public class LocalizationTests
{
    public static IEnumerable<object[]> OtherLanguages() =>
        Loc.Languages.Where(l => l.Code != Loc.DefaultLanguage).Select(l => new object[] { l.Code });

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order().ToArray();

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_HasSameKeysAndPlaceholdersAsEnglish(string code)
    {
        var english = Loc.Load(Loc.DefaultLanguage);
        var other = Loc.Load(code);

        Assert.Empty(english.Keys.Except(other.Keys));
        Assert.Empty(other.Keys.Except(english.Keys));
        foreach (var (key, text) in english)
        {
            Assert.True(Placeholders(text).SequenceEqual(Placeholders(other[key])), $"{code}: placeholders of {key}");
            Assert.True(text.Count(c => c == '|') == other[key].Count(c => c == '|'), $"{code}: '|' separators of {key}");
            Assert.False(string.IsNullOrWhiteSpace(other[key]), $"{code}: empty {key}");
        }
    }

    [Fact]
    public void Resolve_FallsBackToEnglish()
    {
        Assert.Equal("fr", Loc.Resolve("fr"));
        Assert.Equal("en", Loc.Resolve("xx"));
        Assert.Contains(Loc.Resolve(Loc.SystemLanguage), Loc.Languages.Select(l => l.Code));
    }

    [Fact]
    public void T_ReturnsKeyWhenMissing() => Assert.Equal("No.Such.Key", Loc.T("No.Such.Key"));
}
