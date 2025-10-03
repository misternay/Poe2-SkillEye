namespace SkillEye
{
    using System;
    using System.Runtime.InteropServices;
    using System.Text;
    using GameHelper;
    using GameOffsets.Natives;
    using ProcessMemoryUtilities.Managed;
    using ProcessMemoryUtilities.Native;

    internal static class ProcReader
    {
        public static T Read<T>(IntPtr address) where T : unmanaged
        {
            if (address == IntPtr.Zero) return default;
            EnsureProcessHandle();
            T result = default;
            NativeWrapper.ReadProcessMemory(_handle, address, ref result);
            return result;
        }

        public static T[] ReadArray<T>(IntPtr address, int count) where T : unmanaged
        {
            if (address == IntPtr.Zero || count <= 0) return [];
            EnsureProcessHandle();
            T[] buffer = new T[count];
            NativeWrapper.ReadProcessMemoryArray(_handle, address, buffer);
            return buffer;
        }

        public static T[] ReadStdVector<T>(StdVector vector) where T : unmanaged
        {
            long lengthBytes = vector.Last.ToInt64() - vector.First.ToInt64();
            if (lengthBytes <= 0) return [];

            int elementSize = Marshal.SizeOf<T>();
            if (elementSize <= 0 || (lengthBytes % elementSize) != 0) return [];

            int count = (int)(lengthBytes / elementSize);
            return ReadArray<T>(vector.First, count);
        }

        public static string ReadString(IntPtr address, int maxBytes = 128)
        {
            if (address == IntPtr.Zero || maxBytes <= 0) return string.Empty;
            EnsureProcessHandle();

            byte[] buffer = new byte[maxBytes];
            NativeWrapper.ReadProcessMemoryArray(_handle, address, buffer);

            int nullIndex = Array.IndexOf<byte>(buffer, 0x00, 0);
            if (nullIndex > 0)
                return Encoding.ASCII.GetString(buffer, 0, nullIndex);

            return string.Empty;
        }

        public static string ReadUnicodeString(IntPtr address, int maxBytes = 256)
        {
            if (address == IntPtr.Zero || maxBytes < 4) return string.Empty;
            EnsureProcessHandle();

            byte[] buffer = new byte[maxBytes];
            NativeWrapper.ReadProcessMemoryArray(_handle, address, buffer);

            int validByteCount = 0;
            for (int i = 0; i < buffer.Length - 2; i++)
            {
                if (buffer[i] == 0x00 && buffer[i + 1] == 0x00 && buffer[i + 2] == 0x00)
                {
                    validByteCount = (i % 2 == 0) ? i : i + 1;
                    break;
                }
            }

            if (validByteCount == 0) return string.Empty;
            return Encoding.Unicode.GetString(buffer, 0, validByteCount);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static IntPtr _handle = IntPtr.Zero;
        private static int _handlePid = 0;

        private static void EnsureProcessHandle()
        {
            int processId = (int)Core.Process.Pid;

            if (_handle == IntPtr.Zero)
            {
                _handle = NativeWrapper.OpenProcess(ProcessAccessFlags.Read, processId);
                _handlePid = processId;
                return;
            }

            if (_handlePid != processId)
            {
                DisposeHandle();
                _handle = NativeWrapper.OpenProcess(ProcessAccessFlags.Read, processId);
                _handlePid = processId;
            }
        }

        public static void DisposeHandle()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
            _handlePid = 0;
        }
    }
}