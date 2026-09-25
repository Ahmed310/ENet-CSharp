using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ENet.Benchmarks {
	/// <summary>
	/// Points the ENet wrapper at one specific native build and talks to it through raw P/Invokes, so the
	/// same harness binary can drive any build: 2.6.1 as shipped, the fixed pool, or ENET_NO_POOL.
	/// Initialization skips the wrapper's version check on purpose (2.6.1 reports 2.6.1); only packet and
	/// host functions whose signatures never changed are used, and pool statistics fall back to the
	/// 2.6.x export when the extended one is missing.
	/// </summary>
	internal static class NativeLibraryLoader {
		private static string libraryPath;
		private static bool hasExtendedStatistics;

		public static string LibraryPath => libraryPath;

		public static uint LinkedVersion { get; private set; }

		public static void Load(string path) {
			if (String.IsNullOrEmpty(path))
				path = FindDefault();

			if (path == null || !File.Exists(path))
				throw new FileNotFoundException("Native ENet library not found; pass --lib <path>", path ?? "(none)");

			libraryPath = Path.GetFullPath(path);

			NativeLibrary.SetDllImportResolver(typeof(Library).Assembly, Resolve);
			NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);

			IntPtr handle = NativeLibrary.Load(libraryPath);

			hasExtendedStatistics = NativeLibrary.TryGetExport(handle, "enet_pool_get_statistics_ex", out _);
			LinkedVersion = enet_linked_version();

			if (enet_initialize() != 0)
				throw new InvalidOperationException("enet_initialize failed");
		}

		public static void Unload() {
			enet_deinitialize();
		}

		public static string VersionString => $"{LinkedVersion >> 16}.{(LinkedVersion >> 8) & 0xFF}.{LinkedVersion & 0xFF}";

		public static PoolSnapshot ReadPool() {
			PoolSnapshot snapshot = new PoolSnapshot();

			if (hasExtendedStatistics) {
				PoolStatistics statistics = Library.GetPoolStatistics();

				snapshot.Hits = statistics.Hits;
				snapshot.Misses = statistics.Misses;
				snapshot.Oversized = statistics.Oversized;
				snapshot.Returned = statistics.Returned;
				snapshot.Freed = statistics.Freed;
				snapshot.Drained = statistics.Drained;
				snapshot.Retained = statistics.Retained;
				snapshot.Orphaned = statistics.Orphaned;
				snapshot.Caches = statistics.Caches;
			} else {
				enet_pool_get_statistics(out snapshot.Hits, out snapshot.Misses, out snapshot.Oversized, out snapshot.Returned, out uint retained);

				snapshot.Retained = retained;
			}

			return snapshot;
		}

		private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) {
			return name == "enet" ? NativeLibrary.Load(libraryPath) : IntPtr.Zero;
		}

		private static string FindDefault() {
			string fromEnvironment = Environment.GetEnvironmentVariable("ENET_NATIVE_LIB_PATH");

			if (!String.IsNullOrEmpty(fromEnvironment))
				return fromEnvironment;

			string fileName = OperatingSystem.IsWindows() ? "enet.dll" : OperatingSystem.IsMacOS() ? "libenet.dylib" : "libenet.so";

			for (DirectoryInfo current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
				string candidate = Path.Combine(current.FullName, "Source", "Native", "build", "Release", fileName);

				if (File.Exists(candidate))
					return candidate;

				candidate = Path.Combine(current.FullName, "Source", "Native", "build", fileName);

				if (File.Exists(candidate))
					return candidate;
			}

			return null;
		}

		[DllImport("enet", CallingConvention = CallingConvention.Cdecl)]
		private static extern int enet_initialize();

		[DllImport("enet", CallingConvention = CallingConvention.Cdecl)]
		private static extern void enet_deinitialize();

		[DllImport("enet", CallingConvention = CallingConvention.Cdecl)]
		private static extern uint enet_linked_version();

		[DllImport("enet", CallingConvention = CallingConvention.Cdecl)]
		private static extern void enet_pool_get_statistics(out ulong hits, out ulong misses, out ulong oversized, out ulong returned, out uint retained);
	}

	internal struct PoolSnapshot {
		public ulong Hits;
		public ulong Misses;
		public ulong Oversized;
		public ulong Returned;
		public ulong Freed;
		public ulong Drained;
		public uint Retained;
		public uint Orphaned;
		public uint Caches;
	}
}
