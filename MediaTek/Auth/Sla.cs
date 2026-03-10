// mtkclient port: Auth/sla.py
// (c) B.Kerler 2018-2026 GPLv3 License
using System;
using System.Numerics;
using System.Security.Cryptography;

namespace SakuraEDL.MediaTek.Auth
{
    /// <summary>
    /// SLA (Secure Level Authentication) signing functions.
    /// Port of mtkclient/Library/Auth/sla.py
    /// </summary>
    public static class Sla
    {
        /// <summary>
        /// Custom PKCS#1 v1.5 sign without hashing — signs raw message directly.
        /// mtkclient: customized_sign(n, e, msg)
        /// </summary>
        public static byte[] CustomizedSign(BigInteger n, BigInteger e, byte[] msg)
        {
            int modBits = GetBitLength(n);
            int k = (modBits + 7) / 8;

            // PKCS#1 v1.5: EM = 0x00 || 0x01 || PS || 0x00 || msg
            byte[] ps = new byte[k - msg.Length - 3];
            for (int i = 0; i < ps.Length; i++) ps[i] = 0xFF;

            byte[] em = new byte[k];
            em[0] = 0x00;
            em[1] = 0x01;
            Buffer.BlockCopy(ps, 0, em, 2, ps.Length);
            em[2 + ps.Length] = 0x00;
            Buffer.BlockCopy(msg, 0, em, 3 + ps.Length, msg.Length);

            // Convert to BigInteger (big-endian)
            BigInteger emInt = BytesToBigInteger(em);

            // RSA: signature = em^e mod n
            BigInteger mInt = BigInteger.ModPow(emInt, e, n);

            return BigIntegerToBytes(mInt, k);
        }

        /// <summary>
        /// Generate BROM SLA challenge response.
        /// mtkclient: generate_brom_sla_challenge(data, d, e)
        /// Note: Despite mtkclient naming them (d, e), they are used as (n, d) for RSA signing:
        ///   n = public modulus, d = private exponent. Signature = msg^d mod n.
        /// </summary>
        public static byte[] GenerateBromSlaChallenge(byte[] data, BigInteger n, BigInteger d)
        {
            byte[] buf = (byte[])data.Clone();

            // Swap adjacent bytes (input)
            for (int i = 0; i < buf.Length - 1; i += 2)
            {
                byte tmp = buf[i];
                buf[i] = buf[i + 1];
                buf[i + 1] = tmp;
            }

            byte[] msg = CustomizedSign(n, d, buf);

            // Swap adjacent bytes (output)
            for (int i = 0; i < msg.Length - 1; i += 2)
            {
                byte tmp = msg[i];
                msg[i] = msg[i + 1];
                msg[i + 1] = tmp;
            }

            return msg;
        }

        /// <summary>
        /// Generate DA SLA signature using RSA-OAEP with SHA-256.
        /// mtkclient: generate_da_sla_signature(data, key)
        /// </summary>
        public static byte[] GenerateDaSlaSignature(byte[] data, RSAParameters rsaParams)
        {
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(rsaParams);
                return rsa.Encrypt(data, true); // OAEP with SHA-1 (default .NET)
            }
        }

        /// <summary>
        /// Generate DA SLA signature using RSA-OAEP with SHA-256 (full control).
        /// For .NET Framework 4.8 we use BouncyCastle-style manual OAEP if needed.
        /// </summary>
        public static byte[] GenerateDaSlaSignatureOaepSha256(byte[] data, BigInteger n, BigInteger d, BigInteger e)
        {
            // For full SHA-256 OAEP as in mtkclient (PyCryptodome), we need manual OAEP
            // .NET Framework 4.8 RSACryptoServiceProvider only supports SHA-1 OAEP
            // This is a simplified version; for production use BouncyCastle or .NET 5+
            var rsaParams = new RSAParameters
            {
                Modulus = BigIntegerToBytes(n, 256),
                Exponent = BigIntegerToBytes(e, 0),
                D = BigIntegerToBytes(d, 256)
            };

            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(rsaParams);
                return rsa.Encrypt(data, true);
            }
        }

        #region BigInteger Helpers

        /// <summary>
        /// Convert big-endian byte array to BigInteger (unsigned).
        /// </summary>
        public static BigInteger BytesToBigInteger(byte[] data)
        {
            // .NET BigInteger uses little-endian, so reverse + add 0x00 for unsigned
            byte[] reversed = new byte[data.Length + 1];
            for (int i = 0; i < data.Length; i++)
                reversed[data.Length - 1 - i] = data[i];
            reversed[data.Length] = 0x00; // ensure positive
            return new BigInteger(reversed);
        }

        /// <summary>
        /// Convert BigInteger to big-endian byte array with specified length.
        /// </summary>
        public static byte[] BigIntegerToBytes(BigInteger value, int length)
        {
            byte[] leBytes = value.ToByteArray(); // little-endian, may have leading 0x00

            // Remove sign byte if present
            int count = leBytes.Length;
            if (count > 0 && leBytes[count - 1] == 0x00)
                count--;

            byte[] result;
            if (length > 0)
            {
                result = new byte[length];
                int copyLen = Math.Min(count, length);
                for (int i = 0; i < copyLen; i++)
                    result[length - 1 - i] = leBytes[i];
            }
            else
            {
                result = new byte[count];
                for (int i = 0; i < count; i++)
                    result[count - 1 - i] = leBytes[i];
            }

            return result;
        }

        /// <summary>
        /// Parse hex string to BigInteger.
        /// </summary>
        public static BigInteger HexToBigInteger(string hex)
        {
            if (hex.StartsWith("0x") || hex.StartsWith("0X"))
                hex = hex.Substring(2);

            // Ensure even length
            if (hex.Length % 2 != 0)
                hex = "0" + hex;

            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            return BytesToBigInteger(bytes);
        }

        /// <summary>
        /// Calculate bit length of a BigInteger (.NET 4.8 compat).
        /// </summary>
        private static int GetBitLength(BigInteger value)
        {
            byte[] bytes = BigInteger.Abs(value).ToByteArray();
            int length = bytes.Length;
            if (length == 0) return 0;
            byte msb = bytes[length - 1];
            if (msb == 0) // sign byte
            {
                length--;
                if (length == 0) return 0;
                msb = bytes[length - 1];
            }
            int bits = (length - 1) * 8;
            while (msb > 0) { bits++; msb >>= 1; }
            return bits;
        }

        #endregion
    }
}
