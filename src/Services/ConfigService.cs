#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SVNFileBox.Models;
using Serilog;

namespace SVNFileBox.Services;

public class ConfigService
{
    public static ConfigService Instance { get; } = new();
    public static ConfigService? TryInstance => _instance;
    private static ConfigService? _instance;

    public AppConfig Config { get; private set; } = new();
    public string ConfigDir { get; }
    public Repository? CurrentRepository { get; set; }
    private readonly string _configPath;

    public ConfigService()
    {
        _instance = this;
        ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVNFileBox");

        Directory.CreateDirectory(ConfigDir);
        _configPath = Path.Combine(ConfigDir, "config.json");
        Log.Information("ConfigService initialized, config path: {Path}", _configPath);
    }

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = await File.ReadAllTextAsync(_configPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null)
                {
                    // Decrypt passwords from encrypted storage fields.
                    // On first load with a plaintext Password (legacy/initial state),
                    // encrypt it and store in EncryptedPassword, then clear Password.
                    foreach (var repo in config.Repositories)
                    {
                        if (!string.IsNullOrEmpty(repo.EncryptedPassword))
                        {
                            // Normal case: decrypt from encrypted storage
                            repo.Password = DpapiService.Decrypt(repo.EncryptedPassword);
                        }
                        else if (!string.IsNullOrEmpty(repo.Password))
                        {
                            // Legacy/initial state: Password is plaintext, encrypt it now
                            repo.EncryptedPassword = DpapiService.Encrypt(repo.Password);
                            repo.Password = ""; // clear plaintext
                        }
                        // else: both empty, nothing to do
                    }
                    Config = config;
                    Log.Information("Config loaded: {RepoCount} repositories", Config.Repositories.Count);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load config");
        }
        Config = new AppConfig();
        Log.Information("Using default config");
    }

    public async Task SaveAsync()
    {
        try
        {
            // Encrypt Password into EncryptedPassword field, then clear plaintext Password
            foreach (var repo in Config.Repositories)
            {
                if (!string.IsNullOrEmpty(repo.Password))
                {
                    repo.EncryptedPassword = DpapiService.Encrypt(repo.Password ?? "");
                    repo.Password = ""; // clear plaintext after encrypting
                }
                else if (string.IsNullOrEmpty(repo.EncryptedPassword))
                    repo.EncryptedPassword = ""; // both empty, keep consistent
            }

            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configPath, json);
            Log.Debug("Config saved");

            // Restore decrypted passwords back to Password field for in-memory use
            foreach (var repo in Config.Repositories)
            {
                repo.Password = DpapiService.Decrypt(repo.EncryptedPassword);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save config");
        }
    }
}
