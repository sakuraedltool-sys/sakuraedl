// ============================================================================
// SakuraEDL - Spreadtrum Crypto | 展讯加解密
// ============================================================================
// [ZH] 展讯固件加解密 - AES 加密/解密固件文件
// [EN] Spreadtrum Firmware Crypto - AES encrypt/decrypt firmware files
// [JA] Spreadtrumファームウェア暗号化 - AES暗号化/復号
// [KO] Spreadtrum 펌웨어 암호화 - AES 암호화/복호화
// [RU] Криптография Spreadtrum - AES шифрование/дешифрование прошивки
// [ES] Criptografía Spreadtrum - Cifrado/descifrado AES de firmware
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SakuraEDL.Spreadtrum.Common
{
    /// <summary>
    /// 展讯固件加解密工具
    /// 支持 AES 加解密，兼容 iReverse 格式
    /// </summary>
    public static class SprdCryptograph
    {
        // 默认密码 (来自 iReverse)
        public const string DEFAULT_PASSWORD = "@qwerty1234";

        // 加密文件签名
        public const string ENCRYPTED_SIGNATURE = "EndCF";

        // 默认盐值
        private static readonly byte[] DEFAULT_SALT = new byte[] 
        { 
            0x0, 0x0, 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 
            0xF1, 0xF0, 0xEE, 0x21, 0x22, 0x45 
        };

        // PBKDF2 迭代次数
        private const int PBKDF2_ITERATIONS = 1000;

        #region 公共 API

        /// <summary>
        /// 检查数据是否已加密
        /// </summary>
        public static bool IsEncrypted(byte[] data)
        {
            if (data == null || data.Length < ENCRYPTED_SIGNATURE.Length)
                return false;

            // 检查末尾签名
            for (int i = 0; i <= 10; i++)
            {
                int offset = data.Length - ENCRYPTED_SIGNATURE.Length - i;
                if (offset < 0) break;

                string signature = Encoding.ASCII.GetString(data, offset, ENCRYPTED_SIGNATURE.Length);
                if (signature == ENCRYPTED_SIGNATURE)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 检查文件是否已加密
        /// </summary>
        public static bool IsEncrypted(string filePath)
        {
            if (!File.Exists(filePath))
                return false;

            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            {
                // 读取最后 20 字节检查签名
                if (fs.Length < 20)
                    return false;

                fs.Seek(-20, SeekOrigin.End);
                byte[] buffer = new byte[20];
                fs.Read(buffer, 0, 20);

                return Encoding.ASCII.GetString(buffer).Contains(ENCRYPTED_SIGNATURE);
            }
        }

        /// <summary>
        /// 加密数据
        /// </summary>
        public static byte[] Encrypt(byte[] data, string password = null)
        {
            if (password == null)
                password = DEFAULT_PASSWORD;

            using (var outputStream = new MemoryStream())
            {
                // 加密数据
                CryptStream(password, new MemoryStream(data), outputStream, encrypt: true);

                // 添加签名
                byte[] signature = Encoding.ASCII.GetBytes(ENCRYPTED_SIGNATURE);
                outputStream.Write(signature, 0, signature.Length);

                return outputStream.ToArray();
            }
        }

        /// <summary>
        /// 解密数据
        /// </summary>
        public static byte[] Decrypt(byte[] data, string password = null)
        {
            if (password == null)
                password = DEFAULT_PASSWORD;

            // 去除末尾签名
            byte[] rawData = GetRawData(data);

            using (var outputStream = new MemoryStream())
            {
                CryptStream(password, new MemoryStream(rawData), outputStream, encrypt: false);
                return outputStream.ToArray();
            }
        }

        /// <summary>
        /// 加密文件
        /// </summary>
        public static void EncryptFile(string inputPath, string outputPath, string password = null)
        {
            if (password == null)
                password = DEFAULT_PASSWORD;

            using (var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read))
            using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                CryptStream(password, inputStream, outputStream, encrypt: true);

                // 添加签名
                byte[] signature = Encoding.ASCII.GetBytes(ENCRYPTED_SIGNATURE);
                outputStream.Write(signature, 0, signature.Length);
            }
        }

        /// <summary>
        /// 解密文件
        /// </summary>
        public static void DecryptFile(string inputPath, string outputPath, string password = null)
        {
            if (password == null)
                password = DEFAULT_PASSWORD;

            // 读取文件并去除签名
            byte[] data = File.ReadAllBytes(inputPath);
            byte[] rawData = GetRawData(data);

            using (var inputStream = new MemoryStream(rawData))
            using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
            {
                CryptStream(password, inputStream, outputStream, encrypt: false);
            }
        }

        /// <summary>
        /// 尝试解密 (自动检测是否加密)
        /// </summary>
        public static byte[] TryDecrypt(byte[] data, string password = null)
        {
            if (!IsEncrypted(data))
                return data;  // 未加密，原样返回

            try
            {
                return Decrypt(data, password);
            }
            catch
            {
                return data;  // 解密失败，原样返回
            }
        }

        #endregion

        #region 内部实现

        /// <summary>
        /// 加解密流
        /// </summary>
        private static void CryptStream(
            string password,
            Stream inputStream,
            Stream outputStream,
            bool encrypt)
        {
            using (var aes = new AesCryptoServiceProvider())
            {
                // 确定密钥大小
                int keySizeBits = 256;  // AES-256
                for (int i = 1024; i >= 1; i--)
                {
                    if (aes.ValidKeySize(i))
                    {
                        keySizeBits = i;
                        break;
                    }
                }

                int blockSizeBits = aes.BlockSize;

                // 生成密钥和 IV
                byte[] key, iv;
                MakeKeyAndIV(password, DEFAULT_SALT, keySizeBits, blockSizeBits, out key, out iv);

                // 创建加解密器
                ICryptoTransform transform = encrypt
                    ? aes.CreateEncryptor(key, iv)
                    : aes.CreateDecryptor(key, iv);

                try
                {
                    using (var cryptoStream = new CryptoStream(outputStream, transform, CryptoStreamMode.Write))
                    {
                        const int blockSize = 1024;
                        byte[] buffer = new byte[blockSize];
                        int bytesRead;

                        while ((bytesRead = inputStream.Read(buffer, 0, blockSize)) > 0)
                        {
                            cryptoStream.Write(buffer, 0, bytesRead);
                        }
                    }
                }
                catch
                {
                    transform.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// 生成密钥和 IV
        /// </summary>
        private static void MakeKeyAndIV(
            string password,
            byte[] salt,
            int keySizeBits,
            int blockSizeBits,
            out byte[] key,
            out byte[] iv)
        {
            using (var deriveBytes = new Rfc2898DeriveBytes(password, salt, PBKDF2_ITERATIONS))
            {
                key = deriveBytes.GetBytes(keySizeBits / 8);
                iv = deriveBytes.GetBytes(blockSizeBits / 8);
            }
        }

        /// <summary>
        /// 获取原始数据 (去除末尾签名)
        /// </summary>
        private static byte[] GetRawData(byte[] data)
        {
            byte[] signatureBytes = Encoding.ASCII.GetBytes(ENCRYPTED_SIGNATURE);

            // 从末尾查找签名
            for (int i = 0; i <= 10; i++)
            {
                int offset = data.Length - signatureBytes.Length - i;
                if (offset < 0) break;

                bool found = true;
                for (int j = 0; j < signatureBytes.Length; j++)
                {
                    if (data[offset + j] != signatureBytes[j])
                    {
                        found = false;
                        break;
                    }
                }

                if (found)
                {
                    return data.Take(offset).ToArray();
                }
            }

            // 未找到签名，返回原数据
            return data;
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 生成随机密码
        /// </summary>
        public static string GenerateRandomPassword(int length = 16)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        /// <summary>
        /// 计算文件哈希 (用于验证)
        /// </summary>
        public static string ComputeFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// 计算数据哈希
        /// </summary>
        public static string ComputeHash(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        #endregion
    }
}
