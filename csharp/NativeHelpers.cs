using System;
using System.Runtime.InteropServices;

namespace LlsmBindings
{
    internal static class NativeHelpers
    {
        private const string KERNEL32 = "kernel32.dll";

        [DllImport(KERNEL32, SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string lpLibFileName);

        [DllImport(KERNEL32, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        /// <summary>
        /// 指定 DLL をロードします（同一フォルダか PATH に存在すること）。
        /// </summary>
        public static IntPtr LoadLibrary(string path)
        {
            var h = LoadLibraryW(path);
            if (h == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new DllNotFoundException($"Failed to load '{path}', error={err}");
            }
            return h;
        }

        /// <summary>
        /// ロード済みモジュールからエクスポート関数アドレスを取得します。
        /// </summary>
        public static IntPtr GetExport(IntPtr lib, string name)
        {
            var p = GetProcAddress(lib, name);
            if (p == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                throw new EntryPointNotFoundException($"Export '{name}' not found, error={err}");
            }
            return p;
        }
    }
}
