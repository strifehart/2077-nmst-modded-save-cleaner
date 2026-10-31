#!/usr/bin/env bash
# Encodes the rendered WAVs to OGG Vorbis for the app, with every track brought to the same loudness
# (EBU R128, -18 LUFS, true peak -1.5 dB) so none jumps out when the playlist crossfades.
# Usage: encode.sh <wav dir> <app Assets/Music dir>
set -euo pipefail
src="$1"; dst="$2"
mkdir -p "$dst"
for w in "$src"/*.wav; do
  name="$(basename "$w" .wav)"
  ffmpeg -hide_banner -loglevel error -y -i "$w" -af loudnorm=I=-18:TP=-1.5:LRA=11 -ar 44100 -c:a libvorbis -q:a 4 "$dst/$name.ogg"
  echo "$name.ogg $(du -k "$dst/$name.ogg" | cut -f1) KB"
done
cp "$src/tracks.json" "$dst/tracks.json"
