using System;
using Dignite.Vault.Index.Reranking;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dignite.Vault.Index.Reranking
{
    public class HttpRerankerOptions
    {
        /// <summary>The API base, such as <c>https://api.siliconflow.cn/v1</c>; <c>/rerank</c> is appended.</summary>
        public string Endpoint { get; set; } = default!;

        public string ApiKey { get; set; } = default!;

        /// <summary>The rerank model, such as <c>BAAI/bge-reranker-v2-m3</c>.</summary>
        public string ModelId { get; set; } = default!;

        /// <summary>
        /// The outer bound on one rerank, retries included (<c>HttpClient.Timeout</c>). A search waits for it, so keep it
        /// short. A host that adds a resilience pipeline to the returned client keeps that pipeline's total timeout below it.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class HttpRerankerServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="HttpReranker"/> as Vault Index's <see cref="IReranker"/>, on a pooled
        /// <see cref="System.Net.Http.HttpClient"/> from <c>IHttpClientFactory</c>. A host calls it only when it configures a rerank
        /// model; without a registration, search skips reranking. Returns the client's builder so the host can add its
        /// HTTP middleware (retries, circuit breaker); the module adds none.
        /// </summary>
        public static IHttpClientBuilder AddVaultIndexHttpReranker(this IServiceCollection services, Action<HttpRerankerOptions> configure)
        {
            var options = new HttpRerankerOptions();
            configure(options);
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ModelId))
            {
                throw new ArgumentException("A rerank endpoint, API key and model id are required.", nameof(configure));
            }

            services.Configure(configure);
            services.RemoveAll<IReranker>();
            return services.AddHttpClient<IReranker, HttpReranker>(client => client.Timeout = options.Timeout);
        }
    }
}
