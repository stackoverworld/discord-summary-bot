# Native Dependencies

This repository includes the official Linux x64 `libdave.so` binary for the Railway fast path.

For local development on macOS, you can provide it in either of these ways:

1. Set `LIBDAVE_PATH=/absolute/path/to/libdave.dylib` before running `dotnet build` or `dotnet publish`.
2. Drop a local copy at `native/libdave.dylib`.

Upstream source:

- https://github.com/discord/libdave
- https://github.com/discord/libdave/releases/tag/v1.1.1/cpp

The Linux binary in this folder was extracted from the official release archive:

- `libdave-Linux-X64-boringssl.zip`

License files shipped with that archive are copied into `native/licenses/`.

Files such as `native/libdave.dylib` and `native/*.dll` remain gitignored so contributors can keep local platform-specific binaries private.
