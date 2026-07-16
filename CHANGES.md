ENet-CSharp 2.6.0
--------

- Fixed-size native packet-buffer pooling: packet blocks up to 1024 bytes (984-byte payloads) are recycled through a shared pool capped at 576 retained blocks, with transparent malloc fallback for larger packets; pool statistics via `Library.GetPoolStatistics()` / `enet_pool_get_statistics`, drain via `Library.DrainPool()` / `enet_pool_drain`, opt-out via the `ENET_NO_POOL` compile definition
- Managed project retargeted to `netstandard2.1` + `net10.0` (dropped EOL `netcoreapp3.1`/`net8.0`), packaging moved from the stale nuspec into the csproj, embedded PDBs
- Distribution moved to GitHub Packages under `Ahmed310` (nuget.org untouched); CI now tests on Windows/Linux/macOS against the built binaries, packs, and publishes on version tags; macOS native library is now universal (arm64 + x86_64)
- Wrapper stability fixes: native-callback delegates are rooted so the GC cannot collect them while native holds the function pointer (`Packet.SetFreeCallback`, `Host.SetInterceptCallback`/`SetChecksumCallback`, `Library.Initialize(Callbacks)`); `Packet.Create` validates `offset <= length` (prevented a native size_t underflow) and rejects `NoAllocate` with managed arrays (dangling pointer); `Packet.CopyTo` validates the destination size; packet creation failures now throw instead of yielding an unset packet; callback delegates carry `[UnmanagedFunctionPointer(Cdecl)]`
- Native fixes: `enet_packet_create_offset` rejects `dataOffset > dataLength`
- New test suite (`Source/Managed.Tests`) covering connect/disconnect, payload boundaries (984/985/1024), fragmentation, 10k-packet loops, pool exhaustion, burst sends over multiple peers, and shutdown with held buffers
- Version lockstep: managed `Library.version` and native `ENET_VERSION` both report 2.6.0; the wire protocol is unchanged and remains interoperable with 2.5.3 peers

The most notable changes that were made before 2.0.3 version
--------

- Added functionality for easier bindings
- Added monotonic time
- Improved connection-related calculations
- Improved compatibility with various compilers
- Improved transmission statistics
- Eliminated unnecessary memory allocations
- Removed/replaced legacy functionality
- Amalgamated code base into a single header
- Cleaned and reorganized code base

For other changes, check the [release section](https://github.com/nxrighthere/ENet-CSharp/releases).
