// ============================================================================
// SakuraEDL - Auth Strategy Interface | 认证策略接口
// ============================================================================
// [ZH] 认证策略接口 - 处理不同厂商的特殊认证逻辑
// [EN] Auth Strategy Interface - Handle vendor-specific authentication logic
// [JA] 認証戦略インターフェース - ベンダー固有の認証ロジックを処理
// [KO] 인증 전략 인터페이스 - 벤더별 인증 로직 처리
// [RU] Интерфейс стратегии аутентификации - Специфичная логика вендоров
// [ES] Interfaz de estrategia de auth - Lógica de autenticación por vendor
// ============================================================================
// Copyright (c) 2025-2026 SakuraEDL | MIT License
// ============================================================================

using System.Threading;
using System.Threading.Tasks;
using SakuraEDL.Qualcomm.Protocol;

namespace SakuraEDL.Qualcomm.Authentication
{
    /// <summary>
    /// 认证策略接口
    /// </summary>
    public interface IAuthStrategy
    {
        /// <summary>
        /// 策略名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 执行认证
        /// </summary>
        /// <param name="client">Firehose 客户端</param>
        /// <param name="programmerPath">Programmer 文件路径</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否认证成功</returns>
        Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken));
    }
}
