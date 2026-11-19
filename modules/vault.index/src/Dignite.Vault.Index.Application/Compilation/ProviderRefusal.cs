using System;
using System.Net.Http;
using System.Reflection;

namespace Dignite.Vault.Index.Compilation;

/// <summary>
/// Tells a model provider's permanent refusal of a call from a failure a retry can fix: a 4xx answer other than 408
/// (Request Timeout) and 429 (Too Many Requests) refuses the request itself (a rejected key, an unknown model, a body
/// it cannot take), and the same request is refused on every retry. 408, 429, 5xx, timeouts and failures with no HTTP
/// status are retryable. It is the split the provider SDKs' own retry policies make (<c>System.ClientModel</c>'s
/// <c>ClientRetryPolicy</c> behind the OpenAI SDK, <c>Azure.Core</c>'s default classifier), and the one Extract's
/// pipeline jobs make with their own copy of this reader, which Index cannot reference.
/// <para>
/// The status is read from the exception chain: <see cref="HttpRequestException.StatusCode"/>, and the <c>Status</c>
/// of <c>System.ClientModel.ClientResultException</c> (every <c>IChatClient</c> over the OpenAI SDK) or
/// <c>Azure.RequestFailedException</c>, matched by type name so this layer takes no dependency on either SDK.
/// </para>
/// </summary>
public static class ProviderRefusal
{
    private static readonly string[] StatusBearingTypeNames =
    {
        "System.ClientModel.ClientResultException",
        "Azure.RequestFailedException"
    };

    private const int MaxChainDepth = 16;

    /// <summary><c>true</c> when the provider answered with a 4xx status other than 408 or 429.</summary>
    public static bool IsPermanent(Exception exception)
        => TryGetHttpStatus(exception, 0, out var status) && status is >= 400 and < 500 && status != 408 && status != 429;

    private static bool TryGetHttpStatus(Exception? exception, int depth, out int status)
    {
        status = 0;
        if (exception == null || depth > MaxChainDepth)
        {
            return false;
        }

        if (exception is HttpRequestException { StatusCode: { } httpStatus })
        {
            status = (int)httpStatus;
            return true;
        }

        if (TryReadSdkStatus(exception, out status))
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (TryGetHttpStatus(inner, depth + 1, out status))
                {
                    return true;
                }
            }

            return false;
        }

        return TryGetHttpStatus(exception.InnerException, depth + 1, out status);
    }

    private static bool TryReadSdkStatus(Exception exception, out int status)
    {
        status = 0;
        for (var type = exception.GetType(); type != null && type != typeof(Exception); type = type.BaseType)
        {
            if (Array.IndexOf(StatusBearingTypeNames, type.FullName) < 0)
            {
                continue;
            }

            // A status of 0 means the SDK got no HTTP response at all (a transport failure): not a provider answer.
            if (type.GetProperty("Status", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception) is int value
                && value > 0)
            {
                status = value;
                return true;
            }

            return false;
        }

        return false;
    }
}
