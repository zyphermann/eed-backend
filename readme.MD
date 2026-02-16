# Architecture

Goal: a C# backend that can receive multiple WebSocket audio streams (PCM or ADPCM), plus a small Python client that reads WAV files from the `data/` folder and streams them to the server.

## Components

1. **C# WebSocket Server**
   - Runs as a standalone service.
   - Accepts multiple WebSocket connections in parallel.
   - Per connection: receives PCM or ADPCM frames in a defined frame container.
   - Initial handshake frame with stream metadata.
   - Writes received frames into rotating files (e.g. every 10 seconds) for later processing.

2. **Python Streaming Client**
   - Loads a WAV file from `data/`.
   - Optional: streams PCM or ADPCM (IMA ADPCM).
   - Sends PCM or ADPCM frames over WebSocket to the server.
   - Supports multiple parallel streams:
     - via CLI parameter `--streams N` (starts N parallel connections),
     - or by starting the client multiple times.

3. **Shared Protocol**
   - WebSocket + binary frames.
   - Initial handshake frame (binary, 32 bytes, little-endian):
     - `uint32 magic` = `0x41445043` ("ADPC")
     - `uint16 version` = `1`
     - `uint16 header_len` = `32`
     - `uint32 stream_id`
     - `uint32 sample_rate`
     - `uint16 channels`
     - `uint16 codec` (0 = PCM16, 1 = IMA ADPCM)
     - `uint16 frame_samples`
     - `uint16 reserved` = 0
     - `uint64 timestamp_ms`
   - Audio frame (binary, per WebSocket message):
     - `uint32 magic` = `0x41445046` ("ADPF") for ADPCM
     - `uint32 magic` = `0x464D4350` ("PCMF") for PCM
     - `uint32 length` (payload length in bytes)
     - `uint32 seq` (sequence number)
     - `bytes payload` (PCM16 or ADPCM)
   - Backpressure: client waits for send buffer; server reads asynchronously.
   - Persistence: frames (magic/length/seq + payload) are written to files, rotating ~every 10 seconds.
   - Additionally, a PCM WAV file is produced per rotation (PCM direct, ADPCM decoded).

## Data Flow

`WAV (PCM) -> (optional ADPCM) -> WebSocket -> Server Receiver -> file rotation + WAV`

## Layout

- `src/` C# server code
- `client/` Python client
- `data/` Test WAV files
- `data/received/{hwid}/` Rotated stream outputs (ignored by git)

## Local Setup

### Server

```bash
cd src/EetBackend
dotnet run
```

WebSocket endpoints:

- `ws://localhost:5096/ws` (audio stream)
- `ws://localhost:5096/ws/{hwid}` (audio stream with hardware ID)
- `ws://localhost:5096/ws/echo` (echo)

HTTP:

- `POST http://localhost:5096/echo`

### S3 Uploads (optional)

The server can upload rotated files to S3. Configure via `appsettings.json` or environment variables:

- `S3:Enabled` = `true`
- `S3:Provider` = `aws` or `scaleway`
- `S3:Bucket` = `eet.zentronic.at`
- `S3:Region` = `eu-west-1`
- `S3:ServiceUrl` = `https://s3.fr-par.scw.cloud` (Scaleway example)
- `S3:ForcePathStyle` = `true` (often required for S3-compatible providers)
- `S3:Prefix` = `received`
- `S3:UploadBin` / `S3:UploadWav`

Credentials are read from environment variables:

- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`
- `AWS_SESSION_TOKEN` (optional)
- `AWS_PROFILE` (optional, to select a specific AWS CLI profile)
- `AWS_BUCKET`
- `AWS_REGION`

You can place these in a `.env` file (loaded on startup via DotNetEnv), for example:

```
AWS_ACCESS_KEY_ID=...
AWS_SECRET_ACCESS_KEY=...
AWS_SESSION_TOKEN=...
AWS_PROFILE=eet-demo
AWS_BUCKET=eet.zentronic.at
AWS_REGION=eu-west-1
S3_PROVIDER=aws
S3_ENABLED=true
S3_PREFIX=received
S3_UPLOADBIN=true
S3_UPLOADWAV=true
```

For Scaleway you can keep AWS credentials untouched and use Scaleway keys instead:

```
SCW_ACCESS_KEY=...
SCW_SECRET_KEY=...
SCW_DEFAULT_ORGANIZATION_ID=...
SCW_DEFAULT_PROJECT_ID=...
SCW_BUCKET=<your-scaleway-bucket>
SCW_REGION=fr-par
SCW_SERVICE_URL=https://s3.fr-par.scw.cloud
SCW_FORCE_PATH_STYLE=true
S3_PROVIDER=scaleway
S3_ENABLED=true
S3_PREFIX=received
```

If you already use AWS CLI for another project, configure a dedicated profile and reference it from `.env`:

```bash
aws configure --profile eet-demo
```

Then set in `.env`:

```
AWS_PROFILE=eet-demo
```

### Client

```bash
python3 -m venv .venv
source .venv/bin/activate
python3 -m pip install -r client/requirements.txt
```

Start (ADPCM, default):

```bash
.venv/bin/python client/stream_client.py --wav data/sine_pcm16.wav --codec adpcm
```

Example with hardware ID:

```bash
.venv/bin/python client/stream_client.py --wav data/sine_pcm16.wav --codec pcm --url ws://localhost:5096/ws/ABC123-DEVICE
```

Start (PCM, lossless):

```bash
.venv/bin/python client/stream_client.py --wav data/sine_pcm16.wav --codec pcm
```
