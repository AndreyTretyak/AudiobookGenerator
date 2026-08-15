using System.Text.Json;
using System.Text.Json.Serialization;

namespace YewCone.AudiobookGenerator.Core;

internal static class AtomicJsonSettingsFile
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static AtomicJsonSettingsFile()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static async Task<T> LoadAsync<T>(
        string path,
        string settingsName,
        Func<T> createDefault,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return createDefault();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                ?? throw new InvalidDataException($"{settingsName} settings file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{settingsName} settings file '{path}' contains invalid JSON.", ex);
        }
    }

    public static async Task SaveAsync<T>(string path, T settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Unable to determine the directory for '{path}'.");
        Directory.CreateDirectory(directory);

        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
