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

        /// <summary>How long one call may take. A search waits for it, so keep it short.</summary>
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
        /// model; without a registration, search skips reranking.
        /// </summary>
        public static IServiceCollection AddVaultIndexHttpReranker(this IServiceCollection services, Action<HttpRerankerOptions> configure)
        {
            var options = new HttpRerankerOptions();
            configure(options);
            if (string.IsNullOrWhiteSpace(options.Endpoint) || string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.ModelId))
            {
                throw new ArgumentException("A rerank endpoint, API key and model id are required.", nameof(configure));
            }

            services.Configure(configure);
            services.RemoveAll<IReranker>();
            services.AddHttpClient<IReranker, HttpReranker>(client => client.Timeout = options.Timeout);
            return services;
        }
    }
}
