# Architektur

Ziel: C#-Backend, das mehrere WebSocket-Streams mit Audio-Daten (PCM oder ADPCM) empfangen kann, plus ein kleiner Python-Client, der WAV-Dateien aus dem `data`-Ordner liest und als Stream(s) an den Server sendet.

## Komponenten

1. **C# WebSocket-Server**
   - Läuft als eigenständiger Service.
   - Nimmt mehrere WebSocket-Verbindungen parallel an.
   - Pro Verbindung: Empfang von PCM- oder ADPCM-Frames in einem definierten Frame-Container.
   - Initiales Handshake-Frame mit Stream-Metadaten.
   - Schreibt empfangene Frames in rotierende Dateien (z. B. alle 10 Sekunden) für spätere Verarbeitung.

2. **Python-Streaming-Client**
   - Lädt eine WAV-Datei aus `data/`.
   - Optional: PCM oder ADPCM (IMA ADPCM) streaming.
   - Sendet PCM- oder ADPCM-Frames über WebSocket an den Server.
   - Unterstützt mehrere parallele Streams:
     - Per CLI-Parameter `--streams N` (startet N parallele Verbindungen).
     - Alternativ mehrfach starten.

3. **Gemeinsames Protokoll**
   - WebSocket + binäre Frames.
   - Initiales Handshake-Frame (binär, 32 Bytes, little-endian):
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
   - Audio-Frame (binär, pro WebSocket-Message):
     - `uint32 magic` = `0x41445046` ("ADPF") für ADPCM
     - `uint32 magic` = `0x464D4350` ("PCMF") für PCM
     - `uint32 length` (Payload-Länge in Bytes)
     - `uint32 seq` (laufende Nummer)
     - `bytes payload` (PCM16 oder ADPCM)
   - Backpressure: Client wartet auf Sende-Puffer; Server liest asynchron.
   - Persistenz: Frames werden inklusive Magic/Length/Seq in Dateien geschrieben, Rotation ca. alle 10 Sekunden.
   - Zusätzlich wird eine PCM-WAV-Datei pro Rotation erzeugt (PCM direkt, ADPCM wird decodiert).

## Datenfluss

`WAV (PCM) -> (optional ADPCM) -> WebSocket -> Server Receiver -> Datei-Rotation + WAV`

## Verzeichnisstruktur

- `src/` C# Server-Code
- `client/` Python-Client
- `data/` Test-WAV-Dateien

## Lokales Setup

### Server

```bash
cd src/EetBackend
dotnet run
```

WebSocket-Endpunkte:
- `ws://localhost:5096/ws` (Audio-Stream)
- `ws://localhost:5096/ws/echo` (Echo)

HTTP:
- `POST http://localhost:5096/echo`

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

Start (PCM, verlustfrei):
```bash
.venv/bin/python client/stream_client.py --wav data/sine_pcm16.wav --codec pcm
```
