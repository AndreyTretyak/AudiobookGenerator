using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace YewCone.AudiobookGenerator.Core;

internal static class AtomicJsonSettingsFile
{
    public static async Task<T> LoadAsync<T>(
        string path,
        string settingsName,
        Func<T> createDefault,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
        {
            return createDefault();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync(stream, jsonTypeInfo, cancellationToken);
            return value
                ?? throw new InvalidDataException($"{settingsName} settings file '{path}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{settingsName} settings file '{path}' contains invalid JSON.", ex);
        }
    }

    public static async Task SaveAsync<T>(
        string path,
        T settings,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
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
                await JsonSerializer.SerializeAsync(stream, settings, jsonTypeInfo, cancellationToken);
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
