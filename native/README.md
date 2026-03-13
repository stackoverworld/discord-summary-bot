# Native Dependencies

This repository intentionally does not commit a compiled `libdave` binary.

For local development on macOS, you can provide it in either of these ways:

1. Set `LIBDAVE_PATH=/absolute/path/to/libdave.dylib` before running `dotnet build` or `dotnet publish`.
2. Drop a local copy at `native/libdave.dylib`.

Files such as `native/libdave.dylib`, `native/*.so`, and `native/*.dll` are gitignored so contributors can keep platform-specific binaries local.
