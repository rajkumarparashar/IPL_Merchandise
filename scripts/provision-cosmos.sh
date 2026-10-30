#!/usr/bin/env bash
# Creates the production Cosmos DB layout with the Azure CLI. Mirrors CosmosDbInitializer.cs (the source of truth for
# partition keys and indexing). Run once per environment; the API runs with Cosmos__InitializeOnStartup=false in production.
#
# Usage: RESOURCE_GROUP=rg-ipl ACCOUNT=cosmos-ipl-prod LOCATION=centralindia ./scripts/provision-cosmos.sh
# Optional: STORAGE_ACCOUNT=stiplprod also creates the product-photo storage (DESIGN.md ADR-11).
set -euo pipefail

: "${RESOURCE_GROUP:?set RESOURCE_GROUP}"
: "${ACCOUNT:?set ACCOUNT}"
: "${LOCATION:=centralindia}"
DB="${DB:-ipl-merchandise}"
MAX_RU="${MAX_RU:-4000}"   # shared autoscale ceiling for the database

# Session consistency (default). Key-based metadata writes are disabled: containers are created only by this script,
# never by the running API (Cosmos__InitializeOnStartup=false in production).
az cosmosdb create -g "$RESOURCE_GROUP" -n "$ACCOUNT" \
  --locations regionName="$LOCATION" failoverPriority=0 isZoneRedundant=true \
  --default-consistency-level Session \
  --disable-key-based-metadata-write-access true \
  --backup-policy-type Continuous

az cosmosdb sql database create -g "$RESOURCE_GROUP" -a "$ACCOUNT" -n "$DB" --max-throughput "$MAX_RU"

cat > /tmp/catalog-index.json <<'JSON'
{
  "indexingMode": "consistent",
  "includedPaths": [
    {"path": "/storeId/?"}, {"path": "/docType/?"}, {"path": "/isActive/?"}, {"path": "/searchName/?"},
    {"path": "/type/?"}, {"path": "/franchiseCode/?"}, {"path": "/createdAtUtc/?"}, {"path": "/productId/?"},
    {"path": "/changedAtUtc/?"}, {"path": "/status/?"}, {"path": "/price/?"}, {"path": "/isFeatured/?"}
  ],
  "excludedPaths": [{"path": "/*"}],
  "compositeIndexes": [
    [{"path": "/createdAtUtc", "order": "ascending"}, {"path": "/id", "order": "ascending"}],
    [{"path": "/changedAtUtc", "order": "descending"}, {"path": "/id", "order": "descending"}],
    [{"path": "/price", "order": "ascending"}, {"path": "/id", "order": "ascending"}],
    [{"path": "/searchName", "order": "ascending"}, {"path": "/id", "order": "ascending"}]
  ]
}
JSON

cat > /tmp/orders-index.json <<'JSON'
{
  "indexingMode": "consistent",
  "includedPaths": [{"path": "/customerId/?"}, {"path": "/docType/?"}, {"path": "/createdAtUtc/?"}],
  "excludedPaths": [{"path": "/*"}],
  "compositeIndexes": [[{"path": "/createdAtUtc", "order": "descending"}, {"path": "/id", "order": "descending"}]]
}
JSON

cat > /tmp/point-read-index.json <<'JSON'
{ "indexingMode": "consistent", "includedPaths": [], "excludedPaths": [{"path": "/*"}] }
JSON

az cosmosdb sql container create -g "$RESOURCE_GROUP" -a "$ACCOUNT" -d "$DB" -n catalog --partition-key-path /storeId --ttl -1 --idx @/tmp/catalog-index.json
az cosmosdb sql container create -g "$RESOURCE_GROUP" -a "$ACCOUNT" -d "$DB" -n carts   --partition-key-path /customerId --ttl -1 --idx @/tmp/point-read-index.json
az cosmosdb sql container create -g "$RESOURCE_GROUP" -a "$ACCOUNT" -d "$DB" -n orders  --partition-key-path /customerId --idx @/tmp/orders-index.json
az cosmosdb sql container create -g "$RESOURCE_GROUP" -a "$ACCOUNT" -d "$DB" -n users   --partition-key-path /id --idx @/tmp/point-read-index.json

# Product photos (ADR-11). The container is private: browsers load photos through Front Door / CDN (Private Link),
# and the API runs with ImageStorage__CreateContainerIfMissing=false.
if [[ -n "${STORAGE_ACCOUNT:-}" ]]; then
  az storage account create -g "$RESOURCE_GROUP" -n "$STORAGE_ACCOUNT" -l "$LOCATION" \
    --sku Standard_ZRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false
  az storage container create --account-name "$STORAGE_ACCOUNT" -n product-images --public-access off --auth-mode login
  echo "Photos: set ImageStorage__ConnectionString (or a managed identity) and ImageStorage__PublicBaseUrl to the CDN URL."
fi

echo "Done. Store the connection string in Key Vault and expose it to the API as Cosmos__ConnectionString:"
echo "  az cosmosdb keys list -g $RESOURCE_GROUP -n $ACCOUNT --type connection-strings"
