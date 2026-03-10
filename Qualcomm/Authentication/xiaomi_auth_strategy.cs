// ============================================================================
// SakuraEDL - Xiaomi Auth Strategy | 小米认证策略
// ============================================================================
// [ZH] 小米认证 - 支持小米设备免授权绕过
// [EN] Xiaomi Auth - Support Xiaomi device auth bypass
// [JA] Xiaomi認証 - Xiaomiデバイスの認証バイパスをサポート
// [KO] Xiaomi 인증 - Xiaomi 기기 인증 우회 지원
// [RU] Аутентификация Xiaomi - Обход аутентификации устройств Xiaomi
// [ES] Autenticación Xiaomi - Soporte para bypass de autenticación Xiaomi
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Qualcomm.Protocol;

namespace SakuraEDL.Qualcomm.Authentication
{
    public class XiaomiAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;

        public string Name { get { return "Xiaomi (MiAuth Bypass)"; } }

        /// <summary>
        /// 当需要显示授权令牌时触发 (Token 为 VQ 开头的 Base64 格式)
        /// </summary>
        public event Action<string> OnAuthTokenRequired;

        /// <summary>
        /// 最后获取的授权令牌
        /// </summary>
        public string LastAuthToken { get; private set; }

        // 预置签名 (edlclient 签名库)
        private static readonly string[] AuthSignsBase64 = new[]
        {
            "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso" +
            "+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZOlfn4wUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT" +
            "/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN" +
            "/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",
            
            "vzXWATo51hZr4Dh+a5sA/Q4JYoP4Ee3oFZSGbPZ2tBsaMupn" +
            "+6tPbZDkXJRLUzAqHaMtlPMKaOHrEWZysCkgCJqpOPkUZNaSbEKpPQ6uiOVJpJwA" +
            "/PmxuJ72inzSPevriMAdhQrNUqgyu4ATTEsOKnoUIuJTDBmzCeuh/34SOjTdO4Pc+s3ORfMD0TX+WImeUx4c9xVdSL/xirPl" +
            "/BouhfuwFd4qPPyO5RqkU/fevEoJWGHaFjfI302c9k7EpfRUhq1z+wNpZblOHuj0B3/7VOkK8KtSvwLkmVF" +
            "/t9ECiry6G5iVGEOyqMlktNlIAbr2MMYXn6b4Y3GDCkhPJ5LUkQ=="
        };

        public XiaomiAuthStrategy(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        public async Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken))
        {
            _log("[MiAuth] 正在尝试小米免授权绕过...");
            LastAuthToken = null;

            try
            {
                // 1. 尝试预置签名
                int index = 1;
                foreach (var base64 in AuthSignsBase64)
                {
                    if (ct.IsCancellationRequested) break;

                    _log(string.Format("[MiAuth] 尝试签名库 #{0}...", index));
                    
                    // 发送 sig 命令请求
                    string sigCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"256\" verbose=\"1\"/></data>";
                    var sigResp = await client.SendRawXmlAsync(sigCmd, ct);
                    
                    if (sigResp == null || sigResp.Contains("NAK"))
                    {
                        index++;
                        continue;
                    }

                    // 发送二进制签名
                    byte[] data = Convert.FromBase64String(base64);
                    var authResp = await client.SendRawBytesAndGetResponseAsync(data, ct);

                    if (authResp != null && (authResp.ToLower().Contains("authenticated") || authResp.Contains("ACK")))
                    {
                        await Task.Delay(200, ct);
                        if (await client.PingAsync(ct))
                        {
                            _log("[MiAuth] ✅ 绕过成功！设备已解锁。");
                            return true;
                        }
                    }
                    index++;
                }

                _log("[MiAuth] 内置签名无效，正在获取授权令牌...");

                // 2. 获取 Challenge Token (VQ开头的Base64格式)
                string token = await GetAuthTokenAsync(client, ct);

                if (!string.IsNullOrEmpty(token))
                {
                    LastAuthToken = token;
                    _log(string.Format("[MiAuth] 授权令牌: {0}", token));
                    _log("[MiAuth] 💡 请复制令牌进行在线授权或官方申请。");
                    
                    // 触发事件，通知UI显示授权窗口
                    OnAuthTokenRequired?.Invoke(token);
                }
                else
                {
                    _log("[MiAuth] ❌ 无法获取授权令牌。");
                }

                return false;
            }
            catch (Exception ex)
            {
                _log("[MiAuth] 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 获取小米授权令牌 (VQ开头的Base64格式)
        /// </summary>
        public async Task<string> GetAuthTokenAsync(FirehoseClient client, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                // 发送请求获取 Challenge
                string reqCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"req\" /></data>";
                string response = await client.SendRawXmlAsync(reqCmd, ct);
                
                if (string.IsNullOrEmpty(response))
                    return null;

                // 解析 value 属性 (包含原始 Token 数据)
                string rawValue = ExtractAttribute(response, "value");
                if (string.IsNullOrEmpty(rawValue))
                    return null;

                // 如果已经是 VQ 开头，直接返回
                if (rawValue.StartsWith("VQ"))
                    return rawValue;

                // 尝试解析为十六进制并转换为 Base64
                byte[] tokenBytes = HexToBytes(rawValue);
                if (tokenBytes != null && tokenBytes.Length > 0)
                {
                    string base64Token = Convert.ToBase64String(tokenBytes);
                    // 小米 Token 通常以 VQ 开头
                    if (base64Token.StartsWith("VQ"))
                        return base64Token;
                    return base64Token;
                }

                return rawValue;
            }
            catch (Exception ex)
            {
                _log("[MiAuth] 获取令牌异常: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 使用签名进行认证 (用于在线授权后)
        /// </summary>
        public async Task<bool> AuthenticateWithSignatureAsync(FirehoseClient client, string signatureBase64, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                _log("[MiAuth] 使用在线签名进行认证...");

                // 发送 sig 命令准备接收签名
                string sigCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"256\" verbose=\"1\"/></data>";
                var sigResp = await client.SendRawXmlAsync(sigCmd, ct);

                if (sigResp == null || sigResp.Contains("NAK"))
                {
                    _log("[MiAuth] 设备拒绝签名请求");
                    return false;
                }

                // 发送签名数据
                byte[] signatureData = Convert.FromBase64String(signatureBase64);
                var authResp = await client.SendRawBytesAndGetResponseAsync(signatureData, ct);

                if (authResp != null && (authResp.ToLower().Contains("authenticated") || authResp.Contains("ACK")))
                {
                    await Task.Delay(200, ct);
                    if (await client.PingAsync(ct))
                    {
                        _log("[MiAuth] ✅ 在线授权成功！设备已解锁。");
                        return true;
                    }
                }

                _log("[MiAuth] ❌ 签名验证失败");
                return false;
            }
            catch (Exception ex)
            {
                _log("[MiAuth] 签名认证异常: " + ex.Message);
                return false;
            }
        }

        private string ExtractAttribute(string xml, string attrName)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            
            string pattern1 = attrName + "=\"";
            int start = xml.IndexOf(pattern1);
            if (start < 0) return null;
            
            start += pattern1.Length;
            int end = xml.IndexOf("\"", start);
            if (end < 0) return null;
            
            return xml.Substring(start, end - start);
        }

        private byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            
            // 移除可能的前缀和空格
            hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            
            if (hex.Length % 2 != 0) return null;
            
            try
            {
                byte[] bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                }
                return bytes;
            }
            catch
            {
                return null;
            }
        }
    }
}
