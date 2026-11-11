using System;
using System.IO;
using System.Net.Http;
using System.Reflection;

namespace Dignite.Vault.Extract.Documents.Pipelines;

/// <summary>
/// Tells a provider's permanent refusal of a request from a failure that retrying can fix, for the pipeline jobs'
/// retry decision (see <c>DocumentPipelineBackgroundJobBase.EndOnPermanentProviderFailure</c>).
/// <para>
/// A 4xx answer other than 408 (Request Timeout) and 429 (Too Many Requests) means the provider refused the request
/// itself — a malformed request, more output than the model allows, a payload it cannot take, an unknown model, a
/// rejected key. The same request is refused again on every retry. 408, 429, every 5xx, timeouts and failures that
/// carry no HTTP status at all are retryable. This is the split the provider SDKs' own retry policies make:
/// <c>System.ClientModel</c>'s <c>ClientRetryPolicy</c> (behind the OpenAI SDK) and <c>Azure.Core</c>'s default
/// response classifier retry 408, 429, 500, 502, 503 and 504 and nothing else.
/// </para>
/// <para>
/// The status is read from the exception chain: <see cref="HttpRequestException.StatusCode"/> (a provider built on
/// <c>HttpClient</c>, e.g. PaddleOCR), and the <c>Status</c> of <c>System.ClientModel.ClientResultException</c>
/// (OpenAI SDK, so every <c>IChatClient</c> over it) or <c>Azure.RequestFailedException</c> (Azure SDKs). The last
/// two are matched by type name so that this layer takes no dependency on either SDK.
/// </para>
/// <para>
/// <see cref="IsInvalidInput"/> covers the other permanent case, which carries no HTTP status: the document itself
/// is invalid or over a configured limit (<see cref="InvalidDataException"/>).
/// </para>
/// </summary>
public static class ProviderFailureClassifier
{
    private static readonly string[] StatusBearingTypeNames =
    {
        "System.ClientModel.ClientResultException",
        "Azure.RequestFailedException"
    };

    // Deep enough for any real wrapping (job -> service -> chat-client decorators -> SDK); bounded so a cyclic or
    // pathological chain cannot loop.
    private const int MaxChainDepth = 16;

    /// <summary>
    /// <c>true</c> when the provider answered with a 4xx status other than 408 or 429, so the same request cannot
    /// succeed on a retry.
    /// </summary>
    public static bool IsPermanent(Exception exception)
        => TryGetHttpStatus(exception, out var status)
           && status is >= 400 and < 500
           && status != 408
           && status != 429;

    /// <summary>
    /// <c>true</c> when <paramref name="exception"/> or anything it wraps is an <see cref="InvalidDataException"/>:
    /// the text-extraction stack's marker for an input that is itself invalid or over a configured limit (a corrupt or
    /// unsafe workbook, a PDF with more pages than the OCR provider is configured to take). The same input fails the
    /// same way on every retry, so the job ends instead of retrying; the run stays Failed and can be retried once the
    /// file is replaced or the limit is raised. Not part of <see cref="IsPermanent"/>, which is about a provider's
    /// HTTP answer only.
    /// </summary>
    public static bool IsInvalidInput(Exception exception)
        => IsInvalidInput(exception, 0);

    private static bool IsInvalidInput(Exception? exception, int depth)
    {
        if (exception == null || depth > MaxChainDepth)
        {
            return false;
        }

        if (exception is InvalidDataException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                if (IsInvalidInput(inner, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        return IsInvalidInput(exception.InnerException, depth + 1);
    }

    /// <summary>
    /// The first HTTP status found on <paramref name="exception"/> or anything it wraps, if any.
    /// </summary>
    public static bool TryGetHttpStatus(Exception exception, out int status)
        => TryGetHttpStatus(exception, 0, out status);

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
