// Shared byte utility methods for MediaTek modules.
using System;

namespace SakuraEDL.MediaTek.Utility
{
    public static class ByteUtils
    {
        /// <summary>
        /// Find first occurrence of needle in haystack. Returns -1 if not found.
        /// </summary>
        public static int FindBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0 || haystack.Length < needle.Length)
                return -1;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }
    }
}
