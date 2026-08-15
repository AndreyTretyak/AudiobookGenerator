# Audiobook Generator

Application for generating M4B audiobooks from EPUB files. It supports editing chapter text and metadata, embedding images, generating reviewable image descriptions with multimodal models, previewing narration, and selecting either Windows voices or an external neural text-to-speech provider.

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

## AI image descriptions

The app can use a local or hosted multimodal model to draft concise descriptions for EPUB images. Vision profiles are separate from TTS profiles and call the OpenAI-compatible:

```text
POST {base-url}/chat/completions
```

The app sends a normalized image plus bounded nearby chapter context. Original EPUB image bytes are never modified. PNG, JPEG, GIF, WebP, and SVG inputs are decoded with Skia, the first animation frame is selected, and the request image is resized to the configured dimension and byte limits.

Generated text is always a **candidate**. It is not narrated until you review and approve it. Existing EPUB alt text remains approved by default; bulk generation processes only referenced, non-decorative images that are still missing approved text. You can explicitly regenerate existing text, edit a candidate before approval, reject it, restore the EPUB alt text, or mark an image decorative so it remains silent.

If a referenced image has no approved description and is not decorative, narration uses the generic word “Image.” and continues.

### Configure a local vision model

- Console: run `audiobook vision`, or open **Manage Images** and choose **Configure vision models**.
- WPF: open the **Images** tab and select **Vision settings...**.

Settings are stored at:

```text
%LOCALAPPDATA%\YewCone\AudiobookGenerator\vision-settings.json
```

[Ollama](https://docs.ollama.com/api/openai-compatibility) provides a Windows-local OpenAI-compatible endpoint. One example:

```powershell
ollama pull qwen3-vl:8b
```

Create a vision profile with:

```text
ID: local-vision
Base URL: http://127.0.0.1:11434/v1/
Model: qwen3-vl:8b
```

LocalAI and vLLM can also be used when the selected model accepts OpenAI-style `text` and `image_url` content through `/v1/chat/completions`.

### Review and persistence

1. Open an EPUB.
2. Configure/select a vision profile.
3. Generate missing descriptions or regenerate a selected image.
4. Review, edit, approve, reject, or mark the image decorative.
5. Preview narration and generate the M4B.

Approved descriptions are substituted at stable image-reference markers immediately before preview and TTS conversion, so one approval updates every occurrence of the same image.

Generating an M4B also writes `<book>.audiobook.json` beside it. This versioned sidecar stores the source EPUB fingerprint, source-image hashes, cover selection, approved/pending descriptions, decorative flags, and non-secret generation settings. Open the sidecar in either frontend to reopen its source EPUB and restore matching annotations. The source path is relative when the EPUB and sidecar share a drive; a cross-drive sidecar must retain the absolute source path. Image bytes, API keys, and resolved environment values are not stored. Because it is annotation-only, imported images and an imported-image cover cannot be reconstructed; both frontends warn when those items are omitted.

You can also use **Save project as...** before generating an M4B.

### Vision environment overrides

```text
AUDIOBOOKGENERATOR_VISION_DEFAULT_PROFILE
AUDIOBOOKGENERATOR_VISION_PROFILE_IDS
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_DISPLAY_NAME
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_BASE_URL
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_MODEL
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_PROMPT
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_TEMPERATURE
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_MAXIMUM_OUTPUT_TOKENS
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_TIMEOUT_SECONDS
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_MAXIMUM_IMAGE_DIMENSION
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_MAXIMUM_IMAGE_BYTES
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_MAXIMUM_CONTEXT_CHARACTERS
AUDIOBOOKGENERATOR_VISION_<PROFILE_ID>_API_KEY_ENVIRONMENT_VARIABLE
```

Remote profiles must use HTTPS. The selected endpoint receives image content and limited book context, so review its privacy policy and the model's license before use. API-key values are read only from their configured environment variables and are not logged.

## Building

Run `dotnet publish` with the matching .NET SDK installed.

## Contributing

See [Contributing](https://github.com/dotnet/runtime/blob/main/CONTRIBUTING.md) for general information about coding styles, source structure, making pull requests, and more.

This project has adopted the code of conduct defined by the [Contributor Covenant](http://contributor-covenant.org/) 
to clarify expected behavior in our community. For more information, see the [.NET Foundation Code of Conduct](http://www.dotnetfoundation.org/code-of-conduct).

## License

This project is licensed with the [MIT license](LICENSE).
