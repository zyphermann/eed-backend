#!/usr/bin/env python3
import argparse
import asyncio
import os
import struct
import time
import wave

import audioop
import websockets


HANDSHAKE_MAGIC = 0x41445043  # "ADPC"
ADPCM_FRAME_MAGIC = 0x41445046  # "ADPF"
PCM_FRAME_MAGIC = 0x464D4350  # "PCMF"
HANDSHAKE_LEN = 32


def build_handshake(
    stream_id: int,
    sample_rate: int,
    channels: int,
    codec: int,
    frame_samples: int,
    timestamp_ms: int,
) -> bytes:
    return struct.pack(
        "<IHHIIHHHHQ",
        HANDSHAKE_MAGIC,
        1,
        HANDSHAKE_LEN,
        stream_id,
        sample_rate,
        channels,
        codec,
        frame_samples,
        0,
        timestamp_ms,
    )


def build_frame(seq: int, payload: bytes, magic: int) -> bytes:
    return struct.pack("<III", magic, len(payload), seq) + payload


async def stream_once(
    url: str,
    wav_path: str,
    stream_id: int,
    frame_ms: int,
    realtime: bool,
    loop: bool,
    codec: str,
) -> None:
    async with websockets.connect(url, max_size=None) as ws:
        with wave.open(wav_path, "rb") as wf:
            channels = wf.getnchannels()
            sample_rate = wf.getframerate()
            sampwidth = wf.getsampwidth()

            if channels != 1 or sampwidth != 2:
                raise ValueError(
                    f"WAV must be mono 16-bit PCM. Got channels={channels}, sampwidth={sampwidth}"
                )

            frame_samples = int(sample_rate * frame_ms / 1000)
            if frame_samples <= 0:
                raise ValueError("frame_ms too small")

            timestamp_ms = int(time.time() * 1000)
            codec_id = 1 if codec == "adpcm" else 0
            handshake = build_handshake(
                stream_id=stream_id,
                sample_rate=sample_rate,
                channels=channels,
                codec=codec_id,
                frame_samples=frame_samples,
                timestamp_ms=timestamp_ms,
            )
            await ws.send(handshake)

            state = None
            seq = 0
            while True:
                pcm = wf.readframes(frame_samples)
                if not pcm:
                    if loop:
                        wf.rewind()
                        state = None
                        continue
                    break

                state_in = state or (0, 0)
                if codec == "pcm":
                    frame = build_frame(seq, pcm, PCM_FRAME_MAGIC)
                else:
                    adpcm, state = audioop.lin2adpcm(pcm, 2, state_in)
                    predictor, index = state_in
                    header = struct.pack("<hBB", predictor, index, 0)
                    frame = build_frame(seq, header + adpcm, ADPCM_FRAME_MAGIC)
                await ws.send(frame)
                seq += 1

                if realtime:
                    await asyncio.sleep(frame_samples / sample_rate)


async def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--url",
        default="ws://localhost:5096/ws",
        help="WebSocket URL",
    )
    parser.add_argument(
        "--wav",
        default=os.path.join("data", "test.wav"),
        help="Path to WAV file",
    )
    parser.add_argument("--streams", type=int, default=1, help="Number of parallel streams")
    parser.add_argument("--stream-id-base", type=int, default=1, help="Base stream_id")
    parser.add_argument("--frame-ms", type=int, default=20, help="Frame size in ms")
    parser.add_argument("--realtime", action="store_true", help="Pace sending in real time")
    parser.add_argument("--loop", action="store_true", help="Loop WAV when finished")
    parser.add_argument(
        "--codec",
        choices=["adpcm", "pcm"],
        default="adpcm",
        help="Codec to stream",
    )
    args = parser.parse_args()

    tasks = []
    for i in range(args.streams):
        tasks.append(
            asyncio.create_task(
                stream_once(
                    url=args.url,
                    wav_path=args.wav,
                    stream_id=args.stream_id_base + i,
                    frame_ms=args.frame_ms,
                    realtime=args.realtime,
                    loop=args.loop,
                    codec=args.codec,
                )
            )
        )

    await asyncio.gather(*tasks)


if __name__ == "__main__":
    asyncio.run(main())
