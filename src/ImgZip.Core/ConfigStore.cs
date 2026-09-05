namespace ImgZip.Core;

public sealed class ConfigStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string FilePath => Path.Combine(DirectoryPath, "local-config.json");
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new();
        return Wire.Decode<AppConfig>(await File.ReadAllTextAsync(FilePath)).Normalize();
    }
    public async Task SaveAsync(AppConfig config)
    {
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, Wire.Encode(config.Normalize()));
                File.Move(temporary, FilePath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
    public static async Task<AppConfig> ImportAsync(string source) => Wire.Decode<AppConfig>(await File.ReadAllTextAsync(source)).Normalize();
}
