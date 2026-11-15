using System;
using System.ClientModel;
using System.Net;
using System.Net.Http;
using Dignite.Vault.Extract.Documents.Pipelines;
using Shouldly;
using Xunit;

namespace Dignite.Vault.Extract.Documents
{
    /// <summary>
    /// <see cref="ProviderFailureClassifier"/>: which provider failures a job retry cannot fix. The split under test is
    /// the provider SDKs' own retry policy — 408, 429 and 5xx are retryable, every other 4xx is a permanent refusal.
    /// </summary>
    public class ProviderFailureClassifier_Tests
    {
        // ClientResultException.Status has a protected setter; the OpenAI SDK sets it from the HTTP response. This is
        // the real System.ClientModel type, so the classifier's by-name lookup is exercised against the real shape.
        private sealed class StatusClientResultException : ClientResultException
        {
            public StatusClientResultException(int status) : base("Service request failed.") => Status = status;
        }

        [Theory]
        [InlineData(400)] // the 2026-11-10 case: max_tokens above the model's limit
        [InlineData(401)]
        [InlineData(403)]
        [InlineData(404)]
        [InlineData(413)]
        [InlineData(422)]
        public void A_4xx_refusal_from_the_OpenAI_SDK_is_permanent(int status)
        {
            ProviderFailureClassifier.IsPermanent(new StatusClientResultException(status)).ShouldBeTrue();
        }

        [Theory]
        [InlineData(408)]
        [InlineData(429)]
        [InlineData(500)]
        [InlineData(502)]
        [InlineData(503)]
        [InlineData(504)]
        public void Timeouts_throttling_and_server_errors_stay_retryable(int status)
        {
            ProviderFailureClassifier.IsPermanent(new StatusClientResultException(status)).ShouldBeFalse();
            ProviderFailureClassifier.IsPermanent(
                new HttpRequestException("x", null, (HttpStatusCode)status)).ShouldBeFalse();
        }

        [Fact]
        public void An_SDK_exception_with_no_HTTP_response_is_retryable()
        {
            // Status 0: the SDK never got an answer (transport failure) — not a provider refusal.
            ProviderFailureClassifier.IsPermanent(new StatusClientResultException(0)).ShouldBeFalse();
        }

        [Fact]
        public void An_HttpClient_based_provider_refusal_is_permanent()
        {
            // PaddleOcrClient throws HttpRequestException(statusCode: ...) for a non-success answer.
            ProviderFailureClassifier.IsPermanent(
                new HttpRequestException("PaddleOCR server returned 400", null, HttpStatusCode.BadRequest)).ShouldBeTrue();
        }

        [Fact]
        public void An_Azure_SDK_refusal_is_recognized_by_type_name()
        {
            ProviderFailureClassifier.IsPermanent(new Azure.RequestFailedException(400)).ShouldBeTrue();
            ProviderFailureClassifier.IsPermanent(new Azure.RequestFailedException(503)).ShouldBeFalse();
        }

        [Fact]
        public void The_status_is_found_through_wrapping_exceptions()
        {
            var wrapped = new InvalidOperationException("outer",
                new AggregateException(new TimeoutException(), new StatusClientResultException(400)));

            ProviderFailureClassifier.IsPermanent(wrapped).ShouldBeTrue();
            ProviderFailureClassifier.TryGetHttpStatus(wrapped, out var status).ShouldBeTrue();
            status.ShouldBe(400);
        }

        [Fact]
        public void Failures_without_an_HTTP_status_are_retryable()
        {
            ProviderFailureClassifier.IsPermanent(new TimeoutException()).ShouldBeFalse();
            ProviderFailureClassifier.IsPermanent(new InvalidOperationException("no status")).ShouldBeFalse();
            ProviderFailureClassifier.IsPermanent(new HttpRequestException("connection refused")).ShouldBeFalse();
            ProviderFailureClassifier.TryGetHttpStatus(new TimeoutException(), out _).ShouldBeFalse();
        }

        [Fact]
        public void IsInvalidInput_recognises_InvalidDataException_anywhere_in_the_chain()
        {
            ProviderFailureClassifier.IsInvalidInput(new System.IO.InvalidDataException("x")).ShouldBeTrue();
            ProviderFailureClassifier.IsInvalidInput(
                new InvalidOperationException("outer", new System.IO.InvalidDataException("x"))).ShouldBeTrue();
            ProviderFailureClassifier.IsInvalidInput(
                new AggregateException(new TimeoutException(), new System.IO.InvalidDataException("x"))).ShouldBeTrue();
        }

        [Fact]
        public void IsInvalidInput_is_false_for_other_failures()
        {
            ProviderFailureClassifier.IsInvalidInput(new InvalidOperationException("x")).ShouldBeFalse();
            ProviderFailureClassifier.IsInvalidInput(new TimeoutException()).ShouldBeFalse();
            ProviderFailureClassifier.IsInvalidInput(
                new HttpRequestException("x", null, HttpStatusCode.BadRequest)).ShouldBeFalse();

            // The marker is about the input, not the provider's HTTP answer.
            ProviderFailureClassifier.IsPermanent(new System.IO.InvalidDataException("x")).ShouldBeFalse();
        }
    }
}

namespace Azure
{
    /// <summary>
    /// Stand-in with the full name and <c>Status</c> property of Azure.Core's <c>RequestFailedException</c>, so the
    /// classifier's by-name path is tested without taking a dependency on the Azure SDK.
    /// </summary>
    internal sealed class RequestFailedException : Exception
    {
        public RequestFailedException(int status) : base("Azure request failed.") => Status = status;

        public int Status { get; }
    }
}
