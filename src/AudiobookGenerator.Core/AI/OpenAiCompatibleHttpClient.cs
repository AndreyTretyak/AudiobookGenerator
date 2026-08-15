using Microsoft.Extensions.Logging;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace YewCone.AudiobookGenerator.Core;

internal sealed record OpenAiEndpointRequestOptions(
    string Id,
    string DisplayName,
    string BaseUrl,
    int TimeoutSeconds,
    string? ApiKeyEnvironmentVariable);

internal sealed class OpenAiCompatibleHttpClient(
    IHttpClientFactory httpClientFactory,
    ILogger<OpenAiCompatibleHttpClient> logger)
{
    private const int MaximumAttempts = 3;

    public async Task<byte[]> PostJsonForBytesAsync<T>(
        OpenAiEndpointRequestOptions options,
        string relativePath,
        T payload,
        CancellationToken cancellationToken,
        bool includeErrorBody = true,
        int maximumResponseBytes = 256 * 1024 * 1024)
    {
        var endpoint = OpenAiEndpointValidation.BuildEndpoint(options.BaseUrl, relativePath);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                using var request = CreateRequest(options, endpoint, payload);
                using var response = await httpClientFactory
                    .CreateClient(nameof(OpenAiCompatibleHttpClient))
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

                if (IsTransient(response.StatusCode) && attempt < MaximumAttempts)
                {
                    logger.LogWarning(
                        "OpenAI-compatible provider {ProviderId} returned {StatusCode}; retrying attempt {Attempt}.",
                        options.Id,
                        (int)response.StatusCode,
                        attempt + 1);
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = includeErrorBody
                        ? await response.Content.ReadAsStringAsync(timeout.Token)
                        : string.Empty;
                    if (responseBody.Length > 2000)
                    {
                        responseBody = responseBody[..2000];
                    }

                    throw new HttpRequestException(
                        $"Provider '{options.DisplayName}' returned {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}".Trim(),
                        inner: null,
                        response.StatusCode);
                }

                if (response.Content.Headers.ContentLength > maximumResponseBytes)
                {
                    throw new InvalidDataException(
                        $"Provider '{options.DisplayName}' returned more than {maximumResponseBytes} bytes.");
                }

                await using var responseStream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var output = new MemoryStream(
                    response.Content.Headers.ContentLength is > 0 and <= int.MaxValue
                        ? (int)response.Content.Headers.ContentLength.Value
                        : 0);
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await responseStream.ReadAsync(buffer, timeout.Token);
                    if (read == 0)
                    {
                        break;
                    }
                    if (output.Length + read > maximumResponseBytes)
                    {
                        throw new InvalidDataException(
                            $"Provider '{options.DisplayName}' returned more than {maximumResponseBytes} bytes.");
                    }
                    output.Write(buffer, 0, read);
                }

                return output.ToArray();
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastException = new TimeoutException(
                    $"Provider '{options.DisplayName}' did not respond within {options.TimeoutSeconds} seconds.",
                    ex);
            }
            catch (HttpRequestException ex) when (attempt < MaximumAttempts && IsTransient(ex.StatusCode))
            {
                lastException = ex;
            }

            if (attempt < MaximumAttempts)
            {
                logger.LogWarning(
                    lastException,
                    "Request to OpenAI-compatible provider {ProviderId} failed; retrying attempt {Attempt}.",
                    options.Id,
                    attempt + 1);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }

        throw new HttpRequestException(
            $"Request to provider '{options.DisplayName}' failed after {MaximumAttempts} attempts.",
            lastException);
    }

    private static HttpRequestMessage CreateRequest<T>(
        OpenAiEndpointRequestOptions options,
        Uri endpoint,
        T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(options.ApiKeyEnvironmentVariable))
        {
            var apiKey = Environment.GetEnvironmentVariable(options.ApiKeyEnvironmentVariable)
                ?? throw new InvalidOperationException(
                    $"Provider '{options.DisplayName}' requires environment variable '{options.ApiKeyEnvironmentVariable}'.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return request;
    }

    private static bool IsTransient(HttpStatusCode? statusCode) =>
        statusCode is null
        || statusCode == HttpStatusCode.RequestTimeout
        || statusCode == HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
}
