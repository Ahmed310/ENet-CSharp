using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace ENet.Tests {
	/// <summary>
	/// Initializes the ENet library once for the whole run. The library (and the native buffer
	/// pool) is single-threaded and process-global, so all test classes share this collection
	/// and parallelization is disabled in xunit.runner.json.
	/// </summary>
	[CollectionDefinition("ENet", DisableParallelization = true)]
	public class ENetCollection : ICollectionFixture<ENetFixture> {
	}

	public class ENetFixture : IDisposable {
		static ENetFixture() {
			NativeLibrary.SetDllImportResolver(typeof(Library).Assembly, ResolveNativeLibrary);
		}

		public ENetFixture() {
			if (!Library.Initialize())
				throw new InvalidOperationException("ENet library initialization failed");
		}

		public void Dispose() {
			Library.Deinitialize();
		}

		private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) {
			if (libraryName != "enet")
				return IntPtr.Zero;

			string exactPath = Environment.GetEnvironmentVariable("ENET_NATIVE_LIB_PATH");

			if (!String.IsNullOrEmpty(exactPath) && File.Exists(exactPath))
				return NativeLibrary.Load(exactPath);

			string fileName = OperatingSystem.IsWindows() ? "enet.dll" : OperatingSystem.IsMacOS() ? "libenet.dylib" : "libenet.so";
			string directory = Environment.GetEnvironmentVariable("ENET_NATIVE_LIB_DIR");

			if (!String.IsNullOrEmpty(directory)) {
				string candidate = Path.Combine(directory, fileName);

				if (File.Exists(candidate))
					return NativeLibrary.Load(candidate);
			}

			// Local dev fallback: find the CMake output by walking up to the repo root
			for (DirectoryInfo current = new DirectoryInfo(AppContext.BaseDirectory); current != null; current = current.Parent) {
				string multiConfig = Path.Combine(current.FullName, "Source", "Native", "build", "Release", fileName);

				if (File.Exists(multiConfig))
					return NativeLibrary.Load(multiConfig);

				string singleConfig = Path.Combine(current.FullName, "Source", "Native", "build", fileName);

				if (File.Exists(singleConfig))
					return NativeLibrary.Load(singleConfig);
			}

			return IntPtr.Zero;
		}
	}
}
