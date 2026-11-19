#nullable enable

using System.Net;

namespace MajMusic.Core.Network
{
    /// <summary>Describes the account state reported by a source login endpoint.</summary>
    public enum LoginFailureReason
    {
        /// <summary>The account name or password did not match.</summary>
        InvalidCredentials,
        /// <summary>The account exists but has not completed source verification.</summary>
        PendingVerification,
        /// <summary>The source has disabled the account.</summary>
        UserBanned
    }

    /// <summary>Identifies an explicit login whose account name, password or account state was rejected by the source.</summary>
    public sealed class LoginRejectedException : HttpRequestException
    {
        /// <summary>Creates a safe invalid-credentials failure without retaining credentials or the source response body.</summary>
        public LoginRejectedException()
            : this(LoginFailureReason.InvalidCredentials)
        {
        }

        /// <summary>Creates a safe login failure for the specified account state.</summary>
        /// <param name="reason">The source-provided reason for rejecting the login.</param>
        public LoginRejectedException(LoginFailureReason reason)
            : base(CreateMessage(reason), null, GetStatusCode(reason))
        {
            Reason = reason;
        }

        /// <summary>Gets the source-provided account state that prevented login.</summary>
        public LoginFailureReason Reason { get; }

        /// <summary>Gets a safe localized message suitable for the account status area.</summary>
        public string UserMessage => Reason switch
        {
            LoginFailureReason.PendingVerification => "账号尚未完成验证",
            LoginFailureReason.UserBanned => "账号已被封禁",
            _ => "账号或密码错误"
        };

        /// <summary>Creates the exception message without including a source response body or credential.</summary>
        /// <param name="reason">The source-provided account state.</param>
        /// <returns>A non-sensitive diagnostic message.</returns>
        private static string CreateMessage(LoginFailureReason reason)
        {
            return reason switch
            {
                LoginFailureReason.PendingVerification => "The source account has not completed verification.",
                LoginFailureReason.UserBanned => "The source account is banned.",
                _ => "The configured source rejected the account name or password."
            };
        }

        /// <summary>Maps the account state to the HTTP status exposed by the source protocol.</summary>
        /// <param name="reason">The source-provided account state.</param>
        /// <returns>The safe status associated with the login rejection.</returns>
        private static HttpStatusCode GetStatusCode(LoginFailureReason reason)
        {
            return reason == LoginFailureReason.InvalidCredentials ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        }
    }
}