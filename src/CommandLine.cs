using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace AxsManager
{
    internal static class CommandLine
    {
        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(
            [MarshalAs(UnmanagedType.LPWStr)] string command, out int count);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        public static string Join(params string[] values)
        {
            return string.Join(" ", values.Select(Quote));
        }

        public static string[] Split(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) { return new string[0]; }
            int count;
            var pointer = CommandLineToArgvW(command, out count);
            if (pointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("Cannot parse service command line.");
            }
            try
            {
                var args = new string[count];
                for (int i = 0; i < count; i++)
                {
                    args[i] = Marshal.PtrToStringUni(
                        Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
                }
                return args;
            }
            finally { LocalFree(pointer); }
        }
    }
}
