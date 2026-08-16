param(
    [Parameter(Mandatory = $true)]
    [int]$Port
)

$ErrorActionPreference = 'Stop'

$serverCode = @'
using System;
using System.IO;
using System.Net;
using System.Text;

public static class FakeOpenAiTtsServer
{
    public static void Run(int port)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        while (true)
        {
            HttpListenerContext context = null;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(context.Request.Url?.AbsolutePath, "/v1/audio/speech", StringComparison.Ordinal))
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    _ = reader.ReadToEnd();
                    var payload = CreateWave();
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "audio/wav";
                    context.Response.ContentLength64 = payload.Length;
                    context.Response.OutputStream.Write(payload, 0, payload.Length);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }
    }

    private static byte[] CreateWave()
    {
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;
        const int durationMilliseconds = 250;
        const double frequency = 440d;

        var sampleCount = sampleRate * durationMilliseconds / 1000;
        var blockAlign = channels * bitsPerSample / 8;
        var byteRate = sampleRate * blockAlign;
        var dataLength = sampleCount * blockAlign;

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var sample = 0; sample < sampleCount; sample++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * frequency * sample / sampleRate) * short.MaxValue * 0.2);
            writer.Write(value);
        }

        writer.Flush();
        return stream.ToArray();
    }
}
'@

Add-Type -TypeDefinition $serverCode
[FakeOpenAiTtsServer]::Run($Port)
