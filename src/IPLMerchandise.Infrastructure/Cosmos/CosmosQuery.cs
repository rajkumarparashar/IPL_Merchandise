using System.Net;
using Microsoft.Azure.Cosmos;

namespace IPLMerchandise.Infrastructure.Cosmos;

internal static class CosmosQuery
{
    /// <summary>
    /// Reads up to <paramref name="max"/> items from a single-partition query. Cosmos may return short pages before the
    /// end of the results, so we keep reading until we have enough or the query is exhausted.
    /// </summary>
    public static async Task<List<T>> ReadAsync<T>(Container container, QueryDefinition query, string partitionKey, int max, CancellationToken ct)
    {
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, requestOptions: new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(partitionKey),
            MaxItemCount = max
        });
        while (iterator.HasMoreResults && results.Count < max)
        {
            var page = await iterator.ReadNextAsync(ct);
            results.AddRange(page);
        }
        return results.Count > max ? results.GetRange(0, max) : results;
    }

    public static async Task<T?> ReadOrDefaultAsync<T>(Container container, string id, string partitionKey, CancellationToken ct) where T : class
    {
        try
        {
            var response = await container.ReadItemAsync<T>(id, new PartitionKey(partitionKey), cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>The status of the operation that made a transactional batch fail (the others report FailedDependency).</summary>
    public static (int Index, HttpStatusCode Status) FirstFailure(TransactionalBatchResponse response)
    {
        for (var i = 0; i < response.Count; i++)
        {
            var status = response[i].StatusCode;
            if (status != HttpStatusCode.FailedDependency && !response[i].IsSuccessStatusCode)
                return (i, status);
        }
        return (-1, response.StatusCode);
    }
}
