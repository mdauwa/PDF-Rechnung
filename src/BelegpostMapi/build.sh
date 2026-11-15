#!/bin/sh
# Baut BelegpostMapi.dll als 64- und 32-Bit-Variante (mingw-w64, statisch gelinkt, ohne Laufzeit-Abhängigkeiten).
set -e
cd "$(dirname "$0")"
mkdir -p out
for arch in x86_64 i686; do
  [ $arch = x86_64 ] && name=x64 || name=x86
  $arch-w64-mingw32-gcc -O2 -Wall -Wextra -Wno-unused-parameter -shared -municode -static -static-libgcc \
    -o out/BelegpostMapi-$name.dll BelegpostMapi.c -Wl,--kill-at -Wl,--enable-stdcall-fixup -lwtsapi32 -lshell32
  $arch-w64-mingw32-strip out/BelegpostMapi-$name.dll
done
ls -l out
