using System.Net;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Domain.Identity;
using IPLMerchandise.Infrastructure.Cosmos;
using Microsoft.Azure.Cosmos;
using User = IPLMerchandise.Domain.Identity.User;

namespace IPLMerchandise.Infrastructure.Repositories;

public sealed class CosmosUserRepository(CosmosContainers cosmos) : IUserRepository
{
    public async Task<User?> FindByEmailAsync(string normalizedEmail, CancellationToken ct)
    {
        var id = UserDocument.IdFor(normalizedEmail);
        var doc = await CosmosQuery.ReadOrDefaultAsync<UserDocument>(cosmos.Users, id, id, ct);
        return doc?.ToDomain();
    }

    public async Task<bool> TryCreateAsync(User user, CancellationToken ct)
    {
        var doc = UserDocument.From(user);
        try
        {
            await cosmos.Users.CreateItemAsync(doc, new PartitionKey(doc.Id),
                new ItemRequestOptions { EnableContentResponseOnWrite = false }, ct);
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
