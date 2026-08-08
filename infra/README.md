# 🛠️ Infrastructure labs

The infrastructure layer mirrors every management workflow in Azure CLI and
Azure PowerShell while keeping local emulator configuration separate.

- [`azure-cli/`](azure-cli/README.md) — Bash labs using `az`.
- [`powershell/`](powershell/README.md) — behaviorally equivalent Az PowerShell labs.
- [`local/`](local/README.md) — emulator seed files and documented local credentials.

Live scripts create billable resources. Read
[`../docs/COST-AND-CLEANUP.md`](../docs/COST-AND-CLEANUP.md) before running one.

## ☁️ Create a live Storage sandbox

Modules 4, 6, and 7 default to Azurite, but their data-plane labs can also target
a real account. Create one shared sandbox after `az login`:

```bash
export RESOURCE_GROUP="rg-expedition-storage-sandbox"
export AZURE_STORAGE_ACCOUNT="stexpedition$RANDOM$RANDOM"

az group create --name "$RESOURCE_GROUP" --location westeurope --output none
az storage account create \
  --name "$AZURE_STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --location westeurope \
  --sku Standard_LRS \
  --allow-shared-key-access false \
  --output none

principal_id="$(az ad signed-in-user show --query id --output tsv)"
scope="$(az storage account show \
  --name "$AZURE_STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --query id --output tsv)"

for role in \
  "Storage Blob Data Contributor" \
  "Storage Queue Data Contributor" \
  "Storage Table Data Contributor"
do
  az role assignment create \
    --assignee-object-id "$principal_id" \
    --assignee-principal-type User \
    --role "$role" \
    --scope "$scope" \
    --output none
done
```

⏳ Role assignments can take several minutes to propagate. Keep
`AZURE_STORAGE_ACCOUNT` exported, then run the module's Azure CLI lab; the lab
detects the variable, uses your signed-in identity, and removes its container,
queue, or table afterward. PowerShell learners pass the same account with
`-StorageAccountName $env:AZURE_STORAGE_ACCOUNT`.

🧹 The account itself remains billable until you remove its resource group:

```bash
az group delete --name "$RESOURCE_GROUP" --yes
```
