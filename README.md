# Audiobook Generator

Application for generating M4B audiobooks from EPUB files. It supports editing chapter text and metadata, embedding images, previewing narration, and selecting either Windows voices or an external neural text-to-speech provider.

Audio encoding uses FFmpeg, which is installed through `winget` on first conversion when needed.

## Text-to-speech providers

Windows TTS remains the default and requires no configuration. The app also supports named providers implementing the OpenAI-compatible:

```text
POST {base-url}/audio/speech
```

The request contains `model`, `input`, `voice`, `response_format: "wav"`, and `speed`. The provider must return WAV audio. Long chapters are split at paragraph or sentence boundaries according to the profile's input limit, synthesized in order, and reassembled before the M4B is created.

These local voice engines are TTS models, not general-purpose LLMs. The app connects to a server you run separately; it does not download model weights, install Python, or manage the server process.

### Configure a provider

- Console: run `audiobook tts`, or choose **Configure TTS providers** from the interactive editor.
- WPF: select **TTS settings...** beside the provider and voice selectors.

Profiles contain a base URL, model, one or more voice IDs, speed, request limit, timeout, and an optional environment-variable name containing an API key. API key values are never saved.

Settings are stored at:

```text
%LOCALAPPDATA%\YewCone\AudiobookGenerator\tts-settings.json
```

Voice discovery is not part of the generic OpenAI speech contract, so voice IDs are configured explicitly. In the UI, enter one voice per line in this format:

```text
voice-id|Display name|culture|gender
```

Only the voice ID is required.

### Local Kokoro example

[Kokoro-FastAPI](https://github.com/remsky/Kokoro-FastAPI) exposes the required endpoint and supports WAV output. Start its CPU image with Docker:

```powershell
docker run -p 8880:8880 ghcr.io/remsky/kokoro-fastapi-cpu:latest
```

For reproducible use, replace `latest` with a pinned release tag. Then create a profile with:

```text
ID: local-kokoro
Base URL: http://127.0.0.1:8880/v1/
Model: kokoro
Voice: af_heart|Heart|en-US|Female
```

Kokoro-FastAPI lists its installed voices at `http://127.0.0.1:8880/v1/audio/voices`. Other servers, including [LocalAI](https://localai.io/features/text-to-audio/), can be used when their `/v1/audio/speech` endpoint accepts the same request fields and returns WAV.

### Console usage

```powershell
# List voices from every configured provider
audiobook voices

# List one provider
audiobook voices --provider local-kokoro

# Convert with an explicit provider and voice ID
audiobook convert book.epub --provider local-kokoro --voice af_heart --output C:\Books
```

`--voice` first matches an exact voice ID, then a unique partial ID or display-name match.

### Environment overrides

The following optional variables override persisted settings:

```text
AUDIOBOOKGENERATOR_TTS_DEFAULT_PROVIDER
AUDIOBOOKGENERATOR_TTS_PROFILE_IDS
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_DISPLAY_NAME
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_BASE_URL
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_MODEL
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_VOICES
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_SPEED
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_MAXIMUM_INPUT_CHARACTERS
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_TIMEOUT_SECONDS
AUDIOBOOKGENERATOR_TTS_<PROFILE_ID>_API_KEY_ENVIRONMENT_VARIABLE
```

Profile IDs are uppercased and non-alphanumeric characters become underscores in variable names. For example, `local-kokoro` uses `AUDIOBOOKGENERATOR_TTS_LOCAL_KOKORO_BASE_URL`. `PROFILE_IDS` is a comma- or semicolon-separated list for defining environment-only profiles; `VOICES` uses `id=Display name` entries separated by commas or semicolons.

For non-local endpoints, HTTPS is required. Review each model and server's license, voice-cloning consent requirements, and hardware needs before use.

## Building

Run `dotnet publish` with the matching .NET SDK installed.

## Contributing

See [Contributing](https://github.com/dotnet/runtime/blob/main/CONTRIBUTING.md) for general information about coding styles, source structure, making pull requests, and more.

This project has adopted the code of conduct defined by the [Contributor Covenant](http://contributor-covenant.org/) 
to clarify expected behavior in our community. For more information, see the [.NET Foundation Code of Conduct](http://www.dotnetfoundation.org/code-of-conduct).

## License

This project is licensed with the [MIT license](LICENSE).
